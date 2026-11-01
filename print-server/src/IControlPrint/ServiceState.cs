using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>Situação do serviço para o Monitor da bandeja (lida pelo pipe local).</summary>
public sealed class ServiceState
{
    readonly object _lock = new();
    readonly LinkedList<JsonObject> _errors = new();

    public string Company { get; set; } = "";
    public string Cnpj { get; set; } = "";
    public string Role { get; set; } = "";
    public bool Paired { get; set; }
    public bool Revoked { get; set; }
    public DateTime? LastHeartbeatOk { get; set; }
    public string LastHeartbeatError { get; set; } = "";
    public int Queue { get; set; }
    public int ServerErrors { get; set; }
    public string LatestVersion { get; set; } = "";
    public JsonArray Printers { get; set; } = [];
    public DateTime? LastPrintOk { get; set; }
    public string LastFile { get; set; } = "";
    /// <summary>Ilhas e impressora geral da empresa (print_server_islands), para o Monitor.</summary>
    public JsonObject Config { get; set; } = new();
    public DateTime? ConfigAt { get; set; }

    public bool Online => LastHeartbeatOk is { } t && DateTime.UtcNow - t < TimeSpan.FromSeconds(90);

    public void AddError(string title, string message)
    {
        lock (_lock)
        {
            _errors.AddFirst(new JsonObject { ["at"] = DateTime.Now.ToString("dd/MM HH:mm:ss"), ["title"] = title, ["message"] = message });
            while (_errors.Count > 20) _errors.RemoveLast();
        }
    }

    public JsonObject ToJson()
    {
        lock (_lock)
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["version"] = AppInfo.Version,
                ["machine"] = AppInfo.Machine,
                ["paired"] = Paired,
                ["revoked"] = Revoked,
                ["company"] = Company,
                ["cnpj"] = Cnpj,
                ["role"] = Role,
                ["online"] = Online,
                ["last_heartbeat"] = LastHeartbeatOk?.ToLocalTime().ToString("dd/MM HH:mm:ss") ?? "",
                ["last_heartbeat_error"] = LastHeartbeatError,
                ["queue"] = Queue,
                ["server_errors"] = ServerErrors,
                ["latest_version"] = LatestVersion,
                ["last_print_ok"] = LastPrintOk?.ToLocalTime().ToString("dd/MM HH:mm:ss") ?? "",
                ["printers"] = Printers.DeepClone(),
                ["last_file"] = LastFile,
                ["pdf_folder"] = DriverPrinter.PdfFolder,
                ["config"] = Config.DeepClone(),
                ["config_at"] = ConfigAt?.ToLocalTime().ToString("dd/MM HH:mm:ss") ?? "",
                ["errors"] = new JsonArray(_errors.Select(e => (JsonNode)e.DeepClone()).ToArray()),
            };
        }
    }
}
