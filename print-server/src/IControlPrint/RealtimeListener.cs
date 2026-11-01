using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>
/// Supabase Realtime (Broadcast): o banco avisa "tem job" no canal secreto deste servidor e o worker puxa na hora.
/// Se cair, reconecta sozinho; o pull a cada 15 s garante a impressão mesmo sem o aviso.
/// </summary>
public sealed class RealtimeListener(AppSettings cfg, ILogger log)
{
    public async Task RunAsync(Func<string> channel, Action<string> onEvent, CancellationToken ct)
    {
        var delay = 2;
        while (!ct.IsCancellationRequested)
        {
            var topic = channel();
            if (string.IsNullOrEmpty(topic)) { await Task.Delay(5000, ct); continue; }
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                var wsUrl = cfg.SupabaseUrl.Replace("https://", "wss://") + $"/realtime/v1/websocket?apikey={Uri.EscapeDataString(cfg.PublishableKey)}&vsn=1.0.0";
                await ws.ConnectAsync(new Uri(wsUrl), ct);
                var refN = 1;
                await Send(ws, new JsonObject
                {
                    ["topic"] = "realtime:" + topic, ["event"] = "phx_join", ["ref"] = (refN++).ToString(),
                    ["payload"] = new JsonObject { ["config"] = new JsonObject { ["broadcast"] = new JsonObject { ["self"] = false }, ["presence"] = new JsonObject { ["key"] = "" }, ["private"] = false } },
                }, ct);
                log.LogInformation("Realtime conectado");
                delay = 2;
                using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = Task.Run(async () =>
                {
                    while (!hbCts.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(25), hbCts.Token);
                        await Send(ws, new JsonObject { ["topic"] = "phoenix", ["event"] = "heartbeat", ["payload"] = new JsonObject(), ["ref"] = (refN++).ToString() }, hbCts.Token);
                    }
                }, hbCts.Token);

                var buffer = new byte[16 * 1024];
                var sb = new StringBuilder();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    var msg = sb.ToString(); sb.Clear();
                    try
                    {
                        var node = JsonNode.Parse(msg);
                        var ev = node?["event"]?.GetValue<string>();
                        // "job" = imprimir agora; "config" = ilhas ou impressora geral mudaram no sistema
                        if (ev == "broadcast") onEvent(node?["payload"]?["event"]?.GetValue<string>() ?? "job");
                        else if (ev == "phx_reply" && node?["payload"]?["status"]?.GetValue<string>() == "error")
                            log.LogWarning("Realtime recusou a inscrição: {Msg}", msg);
                    }
                    catch { /* mensagem que não interessa */ }
                }
                hbCts.Cancel();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Realtime desconectado: {Err}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            delay = Math.Min(delay * 2, 60);
        }
    }

    static readonly SemaphoreSlim SendLock = new(1, 1);
    static async Task Send(ClientWebSocket ws, JsonObject obj, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
        await SendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { SendLock.Release(); }
    }
}
