using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>
/// Atualização automática: o heartbeat devolve a versão publicada (tabela print_server_releases).
/// Se for mais nova, baixa o instalador, confere o SHA-256 e roda em modo silencioso (ele para, troca e religa o serviço).
/// </summary>
public sealed class Updater(ILogger<Updater> log)
{
    const string AllowedHost = "download.icontrolfood.com.br";
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    string _tried = File.Exists(TriedFile) ? File.ReadAllText(TriedFile).Trim() : "";
    static string TriedFile => Path.Combine(Paths.Updates, "tentada.txt");

    public async Task CheckAsync(JsonObject rel, CancellationToken ct)
    {
        var version = rel["version"]?.GetValue<string>() ?? "";
        var url = rel["url"]?.GetValue<string>() ?? "";
        var sha = (rel["sha256"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        if (!Version.TryParse(version, out var remote) || !Version.TryParse(AppInfo.Version, out var local) || remote <= local) return;
        if (_tried == version) return; // já tentou esta versão: não fica em loop se a instalação falhar
        // só do nosso domínio e sempre com SHA-256
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith("/print/"))
        { log.LogWarning("Atualização {V} recusada: endereço fora de {Host} ({Url})", version, AllowedHost, url); return; }
        if (sha.Length != 64) { log.LogWarning("Atualização {V} recusada: sem SHA-256", version); return; }

        _tried = version;
        File.WriteAllText(TriedFile, version);
        try
        {
            log.LogInformation("Atualizando de {L} para {R}", AppInfo.Version, version);
            var file = Path.Combine(Paths.Updates, $"iControlPrint-Setup-{version}.exe");
            await using (var s = await Http.GetStreamAsync(url, ct))
            await using (var f = File.Create(file)) await s.CopyToAsync(f, ct);
            {
                await using var f = File.OpenRead(file);
                var got = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
                if (got != sha) { log.LogError("Atualização {V} recusada: SHA-256 não confere", version); File.Delete(file); return; }
            }
            Process.Start(new ProcessStartInfo(file, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{Path.Combine(Paths.Logs, "update-" + version + ".log")}\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) { log.LogError(ex, "Falha ao atualizar para {V}", version); }
    }
}
