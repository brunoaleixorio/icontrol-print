using System.Text;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>Comandos usados pelo instalador (Inno Setup). Saída em uma linha "OK|..." ou "ERRO|..." no arquivo --out.</summary>
public static class Cli
{
    public static async Task<int> Run(string[] args, AppSettings cfg)
    {
        var store = new TokenStore();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var api = new SupabaseApi(http, cfg);
        string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }

        switch (args[0])
        {
            case "--preview":
            {
                var r = await api.PairPreview(Arg("--preview"));
                if (r?["ok"]?.GetValue<bool>() != true) { WriteOut(args, "ERRO|" + (r?["error"]?.GetValue<string>() ?? "Código inválido")); return 2; }
                var c = r["company"]!;
                WriteOut(args, $"OK|{S(c["trade_name"])}|{S(c["name"])}|{FormatDoc(S(c["cnpj"]))}");
                return 0;
            }
            case "--claim":
            {
                var r = await api.PairClaim(Arg("--claim"));
                if (r?["ok"]?.GetValue<bool>() != true) { WriteOut(args, "ERRO|" + (r?["error"]?.GetValue<string>() ?? "Código inválido")); return 2; }
                var c = r["company"]!;
                // pareamento anterior desta máquina deixa de valer
                if (store.Load() is { } old) { try { await api.Unpair(old.Token); } catch { /* já revogado */ } }
                store.Save(new TokenStore.Pairing(S(r["token"]), S(r["server_id"]), S(r["channel"]), S(c["id"]), S(c["trade_name"]), FormatDoc(S(c["cnpj"]))));
                WriteOut(args, $"OK|{S(c["trade_name"])}|{S(c["name"])}|{FormatDoc(S(c["cnpj"]))}|{S(r["role"])}");
                return 0;
            }
            case "--unpair":
            {
                if (store.Load() is { } p) { try { await api.Unpair(p.Token); } catch (Exception ex) { Console.WriteLine(ex.Message); } }
                store.Delete();
                WriteOut(args, "OK");
                return 0;
            }
            case "--status":
            {
                var p = store.Load();
                WriteOut(args, p is null ? "NAO_PAREADO" : $"PAREADO|{p.CompanyName}|{p.Cnpj}");
                return p is null ? 3 : 0;
            }
            case "--printers":
                WriteOut(args, Winspool.List().ToJsonString());
                return 0;
            case "--test":
            {
                var printer = Arg("--test");
                var job = EscPos.TestJob(printer);
                RawPrinter.Send(printer, EscPos.Build(job), "iControl teste");
                WriteOut(args, "OK");
                return 0;
            }
            default:
                Console.WriteLine("Uso: IControlPrint.exe [--preview COD | --claim COD | --unpair | --status | --printers | --test IMPRESSORA] [--out arquivo]");
                return 1;
        }
    }

    static string S(JsonNode? n) => n?.GetValue<string>()?.Replace("|", "/") ?? "";

    static string FormatDoc(string d)
    {
        var x = new string(d.Where(char.IsDigit).ToArray());
        return x.Length == 14 ? $"{x[..2]}.{x[2..5]}.{x[5..8]}/{x[8..12]}-{x[12..]}" : x.Length == 11 ? $"{x[..3]}.{x[3..6]}.{x[6..9]}-{x[9..]}" : d;
    }

    public static void WriteOut(string[] args, string line)
    {
        Console.WriteLine(line);
        var i = Array.IndexOf(args, "--out");
        if (i >= 0 && i + 1 < args.Length) File.WriteAllText(args[i + 1], line, new UTF8Encoding(true));
    }
}
