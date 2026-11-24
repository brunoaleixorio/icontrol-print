using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>
/// Ponte local para o sistema aberto NESTE computador (http://127.0.0.1:47700), usada quando a internet cai:
///   GET  /status  situação do Monitor
///   POST /print   { jobs: [...] }  imprime na hora nas ilhas (mesmo formato do PrintJob)
///   GET  /scale   peso da balança ligada aqui
/// Só escuta no próprio computador (loopback) e só responde aos endereços do sistema (CORS + Private Network Access).
/// </summary>
public sealed class LocalServer(ILogger<LocalServer> log, JobPrinter printer, ServiceState state) : BackgroundService
{
    public const int Port = 47700;

    static readonly string[] AllowedOrigins = ["https://icontrolfood.com.br", "https://www.icontrolfood.com.br", "https://delivery.icontrolfood.com.br"];
    static bool OriginOk(string origin) =>
        AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
        || origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
        || (origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && origin.EndsWith(".icontrol-food.pages.dev", StringComparison.OrdinalIgnoreCase));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        TcpListener listener;
        try { listener = new TcpListener(IPAddress.Loopback, Port); listener.Start(); }
        catch (Exception ex) { log.LogWarning("Ponte local não abriu a porta {Port}: {Err}", Port, ex.Message); return; }
        log.LogInformation("Ponte local em http://127.0.0.1:{Port}", Port);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => Handle(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    async Task Handle(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
        try
        {
            using var stream = client.GetStream();
            var (method, path, headers, body) = await ReadRequest(stream, ct);
            if (method.Length == 0) return;
            headers.TryGetValue("origin", out var origin);
            origin ??= "";
            if (origin.Length > 0 && !OriginOk(origin)) { await Write(stream, 403, origin, new JsonObject { ["ok"] = false, ["error"] = "origem não permitida" }); return; }
            if (method == "OPTIONS") { await Write(stream, 204, origin, null); return; }

            JsonObject res;
            int code = 200;
            switch ($"{method} {path.Split('?')[0]}")
            {
                case "GET /status":
                    res = new JsonObject { ["ok"] = true, ["version"] = AppInfo.Version, ["computer"] = AppInfo.Machine, ["paired"] = state.Paired, ["online"] = state.Online, ["company"] = state.Company,
                        ["scale"] = (state.MonitorConfig["scale"] as JsonObject)?["port"]?.ToString() is { Length: > 0 } };
                    break;
                case "POST /print":
                    res = await PrintJobs(body, ct);
                    if (res["ok"]?.GetValue<bool>() != true) code = 500;
                    break;
                case "GET /scale":
                    try { var (w, raw) = await ScaleReader.ReadAsync(state.MonitorConfig, ct); res = new JsonObject { ["ok"] = true, ["weight"] = w, ["raw"] = raw }; }
                    catch (Exception ex) { res = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; code = 500; }
                    break;
                default:
                    res = new JsonObject { ["ok"] = false, ["error"] = "não encontrado" }; code = 404;
                    break;
            }
            await Write(stream, code, origin, res);
        }
        catch (Exception ex) { log.LogDebug("Ponte local: {Err}", ex.Message); }
    }

    async Task<JsonObject> PrintJobs(string body, CancellationToken ct)
    {
        JsonArray jobs;
        try { jobs = JsonNode.Parse(body)?["jobs"] as JsonArray ?? []; }
        catch { return new JsonObject { ["ok"] = false, ["error"] = "pedido inválido" }; }
        if (jobs.Count == 0 || jobs.Count > 30) return new JsonObject { ["ok"] = false, ["error"] = "sem impressões" };
        var errors = new JsonArray();
        int i = 0;
        foreach (var n in jobs.OfType<JsonNode>())
        {
            var job = PrintJob.From(n);
            if (job.Id.Length == 0) job.Id = $"local-{DateTime.Now:HHmmss}-{i}";
            i++;
            var (ok, error, _) = await Task.Run(() => printer.Print(job), ct);
            if (!ok) errors.Add($"{job.IslandName}: {error}");
            else log.LogInformation("Impressão sem internet ({Title}) na ilha {Island}", job.Title, job.IslandName);
        }
        return new JsonObject { ["ok"] = errors.Count == 0, ["printed"] = i - errors.Count, ["errors"] = errors };
    }

    static async Task<(string method, string path, Dictionary<string, string> headers, string body)> ReadRequest(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        int len = 0, headerEnd = -1;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        while (headerEnd < 0)
        {
            if (len == buf.Length) return ("", "", new Dictionary<string, string>(), "");
            int n = await s.ReadAsync(buf.AsMemory(len, buf.Length - len), cts.Token);
            if (n == 0) return ("", "", new Dictionary<string, string>(), "");
            len += n;
            headerEnd = IndexOf(buf, len, HeaderEnd);
        }
        var head = Encoding.ASCII.GetString(buf, 0, headerEnd).Split("\r\n");
        var first = head[0].Split(' ');
        if (first.Length < 2) return ("", "", new Dictionary<string, string>(), "");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in head.Skip(1))
        {
            var k = h.IndexOf(':');
            if (k > 0) headers[h[..k].Trim()] = h[(k + 1)..].Trim();
        }
        int contentLength = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var c) ? Math.Clamp(c, 0, 1024 * 1024) : 0;
        var bodyBytes = new byte[contentLength];
        int have = Math.Min(len - headerEnd - 4, contentLength);
        Array.Copy(buf, headerEnd + 4, bodyBytes, 0, Math.Max(0, have));
        while (have < contentLength)
        {
            int n = await s.ReadAsync(bodyBytes.AsMemory(have, contentLength - have), cts.Token);
            if (n == 0) break;
            have += n;
        }
        return (first[0].ToUpperInvariant(), first[1], headers, Encoding.UTF8.GetString(bodyBytes, 0, have));
    }

    static readonly byte[] HeaderEnd = [13, 10, 13, 10];
    static int IndexOf(byte[] buf, int len, byte[] pat) => buf.AsSpan(0, len).IndexOf(pat);

    static async Task Write(NetworkStream s, int code, string origin, JsonObject? body)
    {
        var text = body?.ToJsonString() ?? "";
        var bytes = Encoding.UTF8.GetBytes(text);
        var reason = code switch { 200 => "OK", 204 => "No Content", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {reason}\r\n");
        if (origin.Length > 0 && OriginOk(origin))
        {
            sb.Append($"Access-Control-Allow-Origin: {origin}\r\n");
            sb.Append("Vary: Origin\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
            sb.Append("Access-Control-Allow-Private-Network: true\r\n");
            sb.Append("Access-Control-Max-Age: 600\r\n");
        }
        if (code != 204) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append($"Content-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await s.WriteAsync(head);
        if (bytes.Length > 0) await s.WriteAsync(bytes);
        await s.FlushAsync();
    }
}
