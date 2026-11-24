using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace IControlPrint;

/// <summary>
/// WhatsApp do restaurante (sessão por QR Code): avisos de pedido e robô de atendimento. Liga só no computador escolhido no
/// sistema (Configurações > WhatsApp). Roda o icontrol-whatsapp.exe (Baileys) como processo filho e conversa por linhas JSON.
/// Mensagens recebidas vão para o banco (print_server_whatsapp_inbound), que decide a resposta do robô.
/// Proteções: intervalo mínimo entre avisos com variação, limite por hora e só mensagens da fila do sistema.
/// </summary>
public sealed class WhatsAppBridge(ILogger<WhatsAppBridge> log, TokenStore store, SupabaseApi api, ServiceState state) : BackgroundService
{
    static string Exe => Path.Combine(AppContext.BaseDirectory, "icontrol-whatsapp.exe");
    static string AuthDir => Path.Combine(Paths.Root, "whatsapp");

    Process? _proc;
    readonly object _lock = new();
    string _status = "desligado", _phone = "", _error = "", _qr = "";
    DateTime? _connectedAt;
    string _lastLogout = "";
    volatile bool _bot;
    readonly Dictionary<string, TaskCompletionSource<string>> _waiting = new();
    readonly Queue<DateTime> _sentTimes = new();
    DateTime _lastSent = DateTime.MinValue;
    readonly SemaphoreSlim _wake = new(0);
    readonly Channel<JsonObject> _inbound = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Desconectar / refazer a conexão pelo Monitor: sai do número e gera um QR Code novo.</summary>
    public bool Logout()
    {
        if (_proc is not { HasExited: false }) return false;
        Send(new JsonObject { ["cmd"] = "logout" });
        Wake();
        return true;
    }

    /// <summary>Fila, enviadas hoje e erros (aba WhatsApp do Monitor).</summary>
    public async Task<JsonNode?> Summary(CancellationToken ct)
    {
        var token = store.Load()?.Token;
        return token is null ? null : await api.WhatsAppSummary(token, ct);
    }

    void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = Task.Run(() => InboundLoop(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            var token = store.Load()?.Token;
            var delay = TimeSpan.FromSeconds(15);
            if (token is not null && !state.Revoked)
            {
                try
                {
                    var cfg = await api.WhatsAppState(token, Snapshot(), ct);
                    var enabled = cfg?["enabled"]?.GetValue<bool>() == true;
                    _bot = cfg?["bot"]?.GetValue<bool>() == true;
                    if (enabled && File.Exists(Exe))
                    {
                        EnsureRunning();
                        var logout = cfg?["logout_at"]?.GetValue<string>() ?? "";
                        if (logout.Length > 0 && logout != _lastLogout)
                        {
                            if (_lastLogout.Length > 0 || _status == "conectado") Send(new JsonObject { ["cmd"] = "logout" });
                            _lastLogout = logout;
                        }
                        if (_status == "conectado") await SendQueue(token, cfg!, ct);
                        delay = TimeSpan.FromSeconds(_status == "aguardando_qr" ? 4 : _bot ? 5 : 10);
                    }
                    else
                    {
                        if (enabled) SetState("erro", error: "Atualize o iControl Monitor: o módulo de WhatsApp não está instalado.");
                        Stop();
                    }
                }
                catch (SupabaseApi.UnauthorizedServerException) { Stop(); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogWarning("WhatsApp: {Err}", ex.Message); }
            }
            try { await _wake.WaitAsync(delay, ct); } catch (OperationCanceledException) { break; }
        }
        Stop();
    }

    // mensagem recebida -> banco (robô). Se o robô respondeu, a fila é lida na hora.
    async Task InboundLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _inbound.Reader.ReadAllAsync(ct))
            {
                var token = store.Load()?.Token;
                if (token is null || !_bot) continue;
                try
                {
                    var r = await api.WhatsAppInbound(token, msg, ct);
                    if (r?["reply"]?.GetValue<bool>() == true) Wake();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogWarning("WhatsApp: mensagem recebida não registrada: {Err}", ex.Message); }
            }
        }
        catch (OperationCanceledException) { /* serviço parando */ }
    }

    JsonObject Snapshot()
    {
        lock (_lock)
        {
            state.WhatsApp = $"{_status}{(_phone.Length > 0 ? " · +" + _phone : "")}";
            state.WhatsAppStatus = _status;
            state.WhatsAppPhone = _phone;
            state.WhatsAppError = _error;
            state.WhatsAppConnectedAt = _status == "conectado" ? _connectedAt : null;
            state.WhatsAppQr = _status == "aguardando_qr" ? _qr : "";
            state.WhatsAppBot = _bot;
            return new JsonObject { ["status"] = _status, ["phone"] = _phone, ["error"] = _error, ["qr"] = _status == "aguardando_qr" ? _qr : "" };
        }
    }

    void SetState(string status, string? phone = null, string? error = null)
    {
        bool changed;
        lock (_lock)
        {
            changed = status != _status;
            if (status == "conectado" && _status != "conectado") _connectedAt = DateTime.UtcNow;
            _status = status; if (phone is not null) _phone = phone; _error = error ?? "";
        }
        Snapshot();
        if (changed) Wake(); // o sistema vê a mudança (ex.: conectado) na hora
    }

    async Task SendQueue(string token, JsonNode cfg, CancellationToken ct)
    {
        var minGap = TimeSpan.FromSeconds(Math.Max(5, cfg["min_interval_sec"]?.GetValue<double>() ?? 10));
        var maxHour = (int)Math.Clamp(cfg["max_per_hour"]?.GetValue<double>() ?? 60, 1, 120);
        while (_sentTimes.Count > 0 && DateTime.UtcNow - _sentTimes.Peek() > TimeSpan.FromHours(1)) _sentTimes.Dequeue();
        // respostas da conversa (robô/atendente) não entram no limite por hora dos avisos
        var room = Math.Max(0, maxHour - _sentTimes.Count);
        var msgs = await api.WhatsAppPull(token, 5, ct);
        foreach (var m in msgs)
        {
            var id = m["id"]?.GetValue<string>() ?? "";
            if (id.Length == 0) continue;
            var reply = m["reply"]?.GetValue<bool>() == true;
            if (!reply && room <= 0) { await api.WhatsAppRelease(token, id, ct); continue; }
            // aviso: intervalo mínimo + variação de 0 a 4 s; resposta de conversa: 1,5 a 3,5 s (tempo de "digitar")
            var wait = reply
                ? _lastSent + TimeSpan.FromMilliseconds(Random.Shared.Next(1500, 3500)) - DateTime.UtcNow
                : _lastSent + minGap + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 4000)) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiting) _waiting[id] = tcs;
            Send(new JsonObject { ["cmd"] = "send", ["id"] = id, ["phone"] = m["phone"]?.GetValue<string>() ?? "", ["text"] = m["text"]?.GetValue<string>() ?? "" });
            string err;
            try { err = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(45), ct); }
            catch (TimeoutException) { err = "Sem resposta do WhatsApp"; }
            finally { lock (_waiting) _waiting.Remove(id); }
            _lastSent = DateTime.UtcNow;
            if (!reply) { _sentTimes.Enqueue(_lastSent); room--; }
            try { await api.WhatsAppAck(token, id, err.Length == 0, err, ct); }
            catch (Exception ex) { log.LogWarning("Ack do WhatsApp {Id} falhou: {Err}", id, ex.Message); }
            if (err.Length > 0) state.AddError("WhatsApp", err);
            if (_status != "conectado") break;
        }
    }

    void EnsureRunning()
    {
        if (_proc is { HasExited: false }) return;
        try
        {
            Directory.CreateDirectory(AuthDir);
            Restrict(AuthDir);
            var psi = new ProcessStartInfo(Exe, $"--auth \"{AuthDir}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) OnLine(line); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) log.LogDebug("whatsapp: {Line}", line); };
            p.Exited += (_, _) => SetState("desconectado", error: "Módulo do WhatsApp parou; reiniciando.");
            p.Start();
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            _proc = p;
            SetState("iniciando");
            log.LogInformation("WhatsApp: módulo iniciado");
        }
        catch (Exception ex) { SetState("erro", error: ex.Message); log.LogWarning("WhatsApp não iniciou: {Err}", ex.Message); }
    }

    void OnLine(string line)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(line); } catch (JsonException) { return; }
        switch (n?["ev"]?.GetValue<string>())
        {
            case "qr": lock (_lock) _qr = n["qr"]?.GetValue<string>() ?? ""; Snapshot(); Wake(); break;
            case "status":
                var st = n["status"]?.GetValue<string>() ?? "";
                SetState(st, n["phone"]?.GetValue<string>() ?? (st == "conectado" ? null : ""), n["error"]?.GetValue<string>());
                if (st == "conectado") log.LogInformation("WhatsApp conectado");
                break;
            case "sent": Done(n["id"]?.GetValue<string>(), ""); break;
            case "failed": Done(n["id"]?.GetValue<string>(), n["error"]?.GetValue<string>() ?? "Falha no envio"); break;
            case "inbound":
                if (_bot && n is JsonObject o) { o.Remove("ev"); _inbound.Writer.TryWrite(o); }
                break;
        }
    }

    void Done(string? id, string err)
    {
        if (id is null) return;
        lock (_waiting) if (_waiting.TryGetValue(id, out var t)) t.TrySetResult(err);
    }

    void Send(JsonObject cmd)
    {
        try { _proc?.StandardInput.WriteLine(cmd.ToJsonString()); _proc?.StandardInput.Flush(); }
        catch (Exception ex) { log.LogWarning("WhatsApp: comando não enviado: {Err}", ex.Message); }
    }

    void Stop()
    {
        if (_proc is null) { SetState("desligado"); return; }
        try { if (!_proc.HasExited) { Send(new JsonObject { ["cmd"] = "quit" }); if (!_proc.WaitForExit(3000)) _proc.Kill(true); } } catch { /* já saiu */ }
        _proc.Dispose(); _proc = null;
        SetState("desligado");
    }

    // sessão do WhatsApp = chave da conta: só SYSTEM e administradores leem a pasta
    static void Restrict(string dir)
    {
        try
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(sec);
        }
        catch { /* sem permissão para mudar: segue com a padrão */ }
    }
}
