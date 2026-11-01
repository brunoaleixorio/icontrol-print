using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>
/// Pipe local "iControlPrint" (só nesta máquina) para o Monitor da bandeja: status, teste, reiniciar, atualização e novo pareamento.
/// Uma linha JSON de pedido, uma linha JSON de resposta.
/// </summary>
public sealed class PipeServer(ILogger<PipeServer> log, ServiceState state, Worker worker, TokenStore store, SupabaseApi api, IHostApplicationLifetime life) : BackgroundService
{
    public const string PipeName = "iControlPrint";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sec = new PipeSecurity();
                sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                // só quem está sentado na máquina (Interactive); nada pela rede
                sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(ct);
                JsonObject res;
                try { res = await Handle(JsonNode.Parse(line ?? "{}") as JsonObject ?? new JsonObject(), ct); }
                catch (Exception ex) { res = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
                await writer.WriteLineAsync(res.ToJsonString());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Pipe do Monitor: {Err}", ex.Message); await Task.Delay(1000, ct); }
        }
    }

    async Task<JsonObject> Handle(JsonObject req, CancellationToken ct)
    {
        switch (req["cmd"]?.GetValue<string>())
        {
            case "status":
            {
                var s = state.ToJson();
                s["update_status"] = worker.UpdateStatus;
                return s;
            }
            case "test":
            {
                var printer = req["printer"]?.GetValue<string>() ?? "";
                var job = EscPos.TestJob(printer);
                try { await Task.Run(() => RawPrinter.Send(printer, EscPos.Build(job), "iControl teste"), ct); }
                catch (Exception ex) { state.AddError("Teste · " + printer, ex.Message); return new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
                return new JsonObject { ["ok"] = true };
            }
            case "check-update":
                worker.HeartbeatNow(forceUpdate: true);
                await Task.Delay(4000, ct);
                return new JsonObject { ["ok"] = true, ["message"] = worker.UpdateStatus is { Length: > 0 } m ? m : "Verificação enviada" };
            case "restart":
                // sai com erro: o Windows religa o serviço sozinho (ações de recuperação do serviço)
                _ = Task.Run(async () => { await Task.Delay(800); log.LogInformation("Reinício pedido pelo Monitor"); Environment.Exit(1); });
                return new JsonObject { ["ok"] = true };
            case "claim":
            {
                var code = req["code"]?.GetValue<string>() ?? "";
                var r = await api.PairClaim(code, ct);
                if (r?["ok"]?.GetValue<bool>() != true) return new JsonObject { ["ok"] = false, ["error"] = r?["error"]?.GetValue<string>() ?? "Código inválido" };
                var c = r["company"]!;
                if (store.Load() is { } old) { try { await api.Unpair(old.Token, ct); } catch { /* já revogado */ } }
                store.Save(new TokenStore.Pairing(r["token"]!.GetValue<string>(), r["server_id"]!.GetValue<string>(), r["channel"]!.GetValue<string>(),
                    c["id"]!.GetValue<string>(), c["trade_name"]?.GetValue<string>() ?? "", c["cnpj"]?.GetValue<string>() ?? ""));
                log.LogInformation("Pareado de novo pelo Monitor com {Company}", c["trade_name"]?.GetValue<string>());
                _ = Task.Run(async () => { await Task.Delay(800); Environment.Exit(1); });   // religa já com a empresa nova
                return new JsonObject { ["ok"] = true, ["company"] = c["trade_name"]?.GetValue<string>() ?? "" };
            }
            default:
                return new JsonObject { ["ok"] = false, ["error"] = "Comando desconhecido" };
        }
    }
}
