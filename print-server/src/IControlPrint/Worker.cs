using System.Text.Json.Nodes;

namespace IControlPrint;

public sealed class Worker(ILogger<Worker> log, ILoggerFactory lf, AppSettings cfg, TokenStore store, SupabaseApi api, JobPrinter printer, Updater updater, ServiceState state) : BackgroundService
{
    readonly SemaphoreSlim _wake = new(0, 100);
    readonly SemaphoreSlim _hbWake = new(0, 10);
    volatile string _channel = "";
    volatile bool _revoked;
    volatile bool _forceUpdate;
    string _lastError = "";

    /// <summary>Monitor: "Verificar atualização" e status na hora.</summary>
    public void HeartbeatNow(bool forceUpdate = false) { _forceUpdate |= forceUpdate; if (_hbWake.CurrentCount < 2) _hbWake.Release(); }
    public string UpdateStatus => updater.Status;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { state.Printers = Winspool.List(); } catch { /* sem spooler */ }
        var p = store.Load();
        while (p is null && !ct.IsCancellationRequested)
        {
            state.Paired = false;
            log.LogWarning("Servidor não pareado. Use o Monitor da bandeja (Parear) ou o instalador, com o código gerado em Configurações > Impressão.");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            p = store.Load();
        }
        if (p is null) return;
        state.Paired = true; state.Company = p.CompanyName; state.Cnpj = p.Cnpj;
        try { store.Save(p); } catch { /* reaplica a permissão restrita no arquivo do token (versões antigas) */ }
        _channel = p.Channel;
        log.LogInformation("Pareado com {Company} ({Cnpj})", p.CompanyName, p.Cnpj);

        var realtime = new RealtimeListener(cfg, lf.CreateLogger<RealtimeListener>());
        await Task.WhenAll(
            HeartbeatLoop(ct),
            PullLoop(ct),
            realtime.RunAsync(() => _revoked ? "" : _channel, () => { if (_wake.CurrentCount < 5) _wake.Release(); }, ct));
    }

    async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var token = store.Load()?.Token;
            if (token is null) { await Task.Delay(TimeSpan.FromMinutes(1), ct); continue; }
            var delay = TimeSpan.FromSeconds(cfg.HeartbeatSeconds);
            try
            {
                JsonArray printers;
                try { printers = Winspool.List(); state.Printers = (JsonArray)printers.DeepClone(); }
                catch (Exception ex) { printers = []; log.LogWarning("Não listou as impressoras: {Err}", ex.Message); }
                var status = _lastError.Length > 0 ? "erro: " + _lastError : "online";
                var res = await api.Heartbeat(token, printers, status, ct);
                _revoked = false; state.Revoked = false;
                state.LastHeartbeatOk = DateTime.UtcNow; state.LastHeartbeatError = "";
                state.Queue = (int?)res?["queue"] ?? 0;
                state.ServerErrors = (int?)res?["errors"] ?? 0;
                state.Role = res?["role"]?.GetValue<string>() ?? "";
                if (res?["company"]?["trade_name"]?.GetValue<string>() is { Length: > 0 } cn) state.Company = cn;
                var ch = res?["channel"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(ch) && ch != _channel) { _channel = ch; store.Update(x => x with { Channel = ch }); }
                if (res?["release"] is JsonObject rel)
                {
                    state.LatestVersion = rel["version"]?.GetValue<string>() ?? "";
                    var force = _forceUpdate; _forceUpdate = false;
                    await updater.CheckAsync(rel, ct, force);
                }
            }
            catch (SupabaseApi.UnauthorizedServerException ex)
            {
                _revoked = true; state.Revoked = true; state.LastHeartbeatError = "Servidor revogado: pareie de novo";
                log.LogError("Servidor revogado ou token inválido: {Err}. Pareie de novo pelo Monitor ou pelo instalador.", ex.Message);
                delay = TimeSpan.FromMinutes(5);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { state.LastHeartbeatError = ex.Message; log.LogWarning("Heartbeat falhou: {Err}", ex.Message); }
            try { await _hbWake.WaitAsync(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    async Task PullLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_revoked && store.Load()?.Token is { } token)
            {
                try
                {
                    // puxa até esvaziar a fila (lotes de 10)
                    for (int round = 0; round < 10; round++)
                    {
                        var jobs = await api.Pull(token, ct);
                        if (jobs.Count == 0) break;
                        foreach (var job in jobs)
                        {
                            var (ok, error, _) = await Task.Run(() => printer.Print(job), ct);
                            _lastError = ok ? "" : error;
                            try { await api.Ack(token, job.Id, ok, error, ct); }
                            catch (Exception ex) { log.LogWarning("Ack do job {Id} falhou: {Err}", job.Id, ex.Message); }
                        }
                    }
                }
                catch (SupabaseApi.UnauthorizedServerException) { _revoked = true; state.Revoked = true; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogWarning("Pull falhou: {Err}", ex.Message); }
            }
            // acorda pelo Realtime ou a cada 15 s
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(cfg.PullSeconds), ct); }
            catch (OperationCanceledException) { break; }
            while (_wake.CurrentCount > 0) _wake.Wait(0);
        }
    }
}
