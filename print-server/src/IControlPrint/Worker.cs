using System.Text.Json.Nodes;

namespace IControlPrint;

public sealed class Worker(ILogger<Worker> log, ILoggerFactory lf, AppSettings cfg, TokenStore store, SupabaseApi api, JobPrinter printer, Updater updater) : BackgroundService
{
    readonly SemaphoreSlim _wake = new(0, 100);
    volatile string _channel = "";
    volatile bool _revoked;
    string _lastError = "";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var p = store.Load();
        while (p is null && !ct.IsCancellationRequested)
        {
            log.LogWarning("Servidor não pareado. Rode o instalador e informe o código gerado em Configurações > Impressão.");
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            p = store.Load();
        }
        if (p is null) return;
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
            try
            {
                JsonArray printers;
                try { printers = Winspool.List(); } catch (Exception ex) { printers = []; log.LogWarning("Não listou as impressoras: {Err}", ex.Message); }
                var status = _lastError.Length > 0 ? "erro: " + _lastError : "online";
                var res = await api.Heartbeat(token, printers, status, ct);
                _revoked = false;
                var ch = res?["channel"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(ch) && ch != _channel) { _channel = ch; store.Update(x => x with { Channel = ch }); }
                if (res?["release"] is JsonObject rel) await updater.CheckAsync(rel, ct);
            }
            catch (SupabaseApi.UnauthorizedServerException ex)
            {
                _revoked = true;
                log.LogError("Servidor revogado ou token inválido: {Err}. Pareie de novo pelo instalador.", ex.Message);
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Heartbeat falhou: {Err}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(cfg.HeartbeatSeconds), ct);
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
                            var (ok, error, used) = await Task.Run(() => printer.Print(job), ct);
                            _lastError = ok ? "" : error;
                            try { await api.Ack(token, job.Id, ok, error, ct); }
                            catch (Exception ex) { log.LogWarning("Ack do job {Id} falhou: {Err}", job.Id, ex.Message); }
                        }
                    }
                }
                catch (SupabaseApi.UnauthorizedServerException) { _revoked = true; }
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
