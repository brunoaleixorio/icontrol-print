using System.Reflection;
using System.Text;
using System.Text.Json;
using IControlPrint;
using Serilog;

// Servidor de impressão iControl.
// Sem argumentos: roda como serviço do Windows (ou console, para teste).
// Linha de comando (usada pelo instalador):
//   --preview CÓDIGO --out arquivo   mostra a empresa do código (não consome)
//   --claim CÓDIGO --out arquivo     pareia esta máquina (guarda o token com DPAPI)
//   --unpair                         desfaz o pareamento
//   --status --out arquivo           PAREADO|empresa|cnpj  ou  NAO_PAREADO
//   --test "Impressora"              imprime uma página de teste local
//   --monitor [--show]               ícone na bandeja (usuário logado), conversa com o serviço pelo pipe local
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Contains("--monitor"))
{
    // WinForms precisa de thread STA; o Monitor não grava log em ProgramData (usuário comum não tem permissão)
    var t = new Thread(() => TrayMonitor.Run(args.Contains("--show")));
    t.SetApartmentState(ApartmentState.STA);
    t.Start(); t.Join();
    return 0;
}

Paths.EnsureCreated();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(Path.Combine(Paths.Logs, "icontrol-print-.log"), rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true, shared: true)
    .WriteTo.Console()
    .CreateLogger();

var settings = AppSettings.Load();

if (args.Length > 0 && args[0].StartsWith("--"))
{
    try { return await Cli.Run(args, settings); }
    catch (Exception ex) { Log.Error(ex, "Falha no comando {Cmd}", args[0]); Cli.WriteOut(args, "ERRO|" + ex.Message); return 1; }
    finally { Log.CloseAndFlush(); }
}

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(o => o.ServiceName = "iControlPrint");
    builder.Services.AddSerilog();
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton<TokenStore>();
    builder.Services.AddHttpClient<SupabaseApi>(c => c.Timeout = TimeSpan.FromSeconds(20));
    builder.Services.AddSingleton<JobPrinter>();
    builder.Services.AddSingleton<Updater>();
    builder.Services.AddSingleton<ServiceState>();
    builder.Services.AddSingleton<Worker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());
    builder.Services.AddHostedService<PipeServer>();
    Log.Information("iControl Print {Version} iniciando", AppInfo.Version);
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex) { Log.Fatal(ex, "Serviço encerrado por erro"); return 1; }
finally { Log.CloseAndFlush(); }

namespace IControlPrint
{
    public static class AppInfo
    {
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        public static string Machine => Environment.MachineName;
    }

    public static class Paths
    {
        public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iControlPrint");
        public static string Logs => Path.Combine(Root, "logs");
        public static string Token => Path.Combine(Root, "token.bin");
        public static string Updates => Path.Combine(Root, "update");
        public static void EnsureCreated() { Directory.CreateDirectory(Root); Directory.CreateDirectory(Logs); Directory.CreateDirectory(Updates); }
    }

    public sealed class AppSettings
    {
        public string SupabaseUrl { get; init; } = "";
        public string PublishableKey { get; init; } = "";
        public int PullSeconds { get; init; } = 15;
        public int HeartbeatSeconds { get; init; } = 30;
        public int MaxAttempts { get; init; } = 3;

        // appsettings.json vai embutido no executável (arquivo único)
        public static AppSettings Load()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("appsettings.json")
                ?? throw new InvalidOperationException("appsettings.json não embutido");
            using var doc = JsonDocument.Parse(s);
            var sb = doc.RootElement.GetProperty("Supabase");
            var pr = doc.RootElement.GetProperty("Print");
            return new AppSettings
            {
                SupabaseUrl = sb.GetProperty("Url").GetString()!.TrimEnd('/'),
                PublishableKey = sb.GetProperty("PublishableKey").GetString()!,
                PullSeconds = pr.GetProperty("PullSeconds").GetInt32(),
                HeartbeatSeconds = pr.GetProperty("HeartbeatSeconds").GetInt32(),
                MaxAttempts = pr.GetProperty("MaxAttempts").GetInt32(),
            };
        }
    }
}
