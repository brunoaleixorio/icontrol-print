using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>Chamadas RPC do PostgREST. A empresa nunca é enviada: o banco descobre pelo token.</summary>
public sealed class SupabaseApi
{
    readonly HttpClient _http;
    readonly AppSettings _cfg;

    public SupabaseApi(HttpClient http, AppSettings cfg)
    {
        _http = http;
        _cfg = cfg;
        _http.BaseAddress = new Uri(cfg.SupabaseUrl + "/rest/v1/");
        _http.DefaultRequestHeaders.Add("apikey", cfg.PublishableKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"iControlPrint/{AppInfo.Version}");
    }

    public sealed class UnauthorizedServerException(string msg) : Exception(msg);

    async Task<JsonNode?> Rpc(string fn, object body, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("rpc/" + fn, body, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string msg = text;
            try { msg = JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text; } catch { /* texto puro */ }
            // 28000 = token inválido/revogado
            if (text.Contains("\"28000\"") || res.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedServerException(msg);
            throw new HttpRequestException($"{fn}: {(int)res.StatusCode} {msg}");
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public Task<JsonNode?> PairPreview(string code, CancellationToken ct = default) => Rpc("print_pair_preview", new { p_code = code }, ct);
    public Task<JsonNode?> PairClaim(string code, CancellationToken ct = default) =>
        Rpc("print_pair_claim", new { p_code = code, p_machine_name = AppInfo.Machine, p_version = AppInfo.Version }, ct);
    public Task<JsonNode?> Unpair(string token, CancellationToken ct = default) => Rpc("print_server_unpair", new { p_token = token }, ct);

    public Task<JsonNode?> Heartbeat(string token, JsonArray printers, string status, CancellationToken ct) =>
        Rpc("print_server_heartbeat", new { p_token = token, p_printers = printers, p_status = status, p_version = AppInfo.Version }, ct);

    public async Task<List<PrintJob>> Pull(string token, CancellationToken ct)
    {
        var node = await Rpc("print_server_pull", new { p_token = token, p_limit = 10 }, ct);
        if (node is not JsonArray arr) return [];
        return arr.Where(n => n is not null).Select(n => PrintJob.From(n!)).Where(j => j.Id.Length > 0).ToList();
    }

    // ilhas e impressora geral da empresa do token (Monitor); alterações exigem administrador do Windows ou PIN
    public Task<JsonNode?> Islands(string token, CancellationToken ct) => Rpc("print_server_islands", new { p_token = token }, ct);
    public Task<JsonNode?> IslandSave(string token, string islandId, JsonObject fields, string auth, string pin, CancellationToken ct) =>
        Rpc("print_server_island_save", new { p_token = token, p_island_id = islandId, p_fields = fields, p_auth = auth, p_pin = pin }, ct);
    public Task<JsonNode?> GeneralSave(string token, JsonObject fields, string auth, string pin, CancellationToken ct) =>
        Rpc("print_server_general_save", new { p_token = token, p_fields = fields, p_auth = auth, p_pin = pin }, ct);

    public Task<JsonNode?> Ack(string token, string jobId, bool ok, string error, CancellationToken ct) =>
        Rpc("print_server_ack", new { p_token = token, p_job_id = jobId, p_ok = ok, p_error = error ?? "" }, ct);
}

public sealed class PrintJob
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string IslandName { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string PrinterBackup { get; set; } = "";
    public int PaperWidthMm { get; set; } = 80;
    public string FontSize { get; set; } = "normal";
    public bool CutPaper { get; set; } = true;
    public bool Beep { get; set; }
    public int FeedLines { get; set; } = 4;
    public bool OpenDrawer { get; set; }
    public string PrintMode { get; set; } = "auto";     // auto | raw | driver
    public string Target { get; set; } = "";            // "geral" = impressora geral do sistema
    public string OriginType { get; set; } = "";
    public string OriginNumber { get; set; } = "";

    // leitura tolerante: o banco guarda jsonb livre (número/booleano podem vir como texto)
    public static PrintJob From(JsonNode n) => new()
    {
        Id = Str(n["id"]),
        Title = Str(n["title"]),
        Text = Str(n["text"]),
        IslandName = Str(n["island_name"]),
        PrinterName = Str(n["printer_name"]),
        PrinterBackup = Str(n["printer_backup"]),
        PaperWidthMm = Int(n["paper_width_mm"], 80),
        FontSize = Str(n["font_size"]) is { Length: > 0 } f ? f : "normal",
        CutPaper = Bool(n["cut_paper"], true),
        Beep = Bool(n["beep"], false),
        FeedLines = Int(n["feed_lines"], 4),
        OpenDrawer = Bool(n["open_drawer"], false),
        PrintMode = Str(n["print_mode"]) is { Length: > 0 } m ? m : "auto",
        Target = Str(n["target"]),
        OriginType = Str(n["origin_type"]),
        OriginNumber = Str(n["origin_number"]),
    };

    static string Str(JsonNode? v) => v is null ? "" : v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
    static int Int(JsonNode? v, int d) => v is null ? d : int.TryParse(Str(v).Trim('"'), out var i) ? i : d;
    static bool Bool(JsonNode? v, bool d)
    {
        if (v is null) return d;
        var s = Str(v).Trim('"').ToLowerInvariant();
        return s switch { "true" or "1" or "sim" => true, "false" or "0" or "nao" or "não" => false, _ => d };
    }
}
