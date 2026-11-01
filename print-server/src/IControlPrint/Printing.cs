using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>Comandos ESC/POS (Epson, Elgin, Bematech, Daruma, Tanca e compatíveis).</summary>
public static class EscPos
{
    public static readonly byte[] Init = [0x1B, 0x40];
    public static readonly byte[] CodePage850 = [0x1B, 0x74, 0x02];                 // PC850 (acentos)
    public static readonly byte[] SizeNormal = [0x1D, 0x21, 0x00];
    public static readonly byte[] SizeTall = [0x1D, 0x21, 0x01];                    // altura dupla
    public static readonly byte[] SizeDouble = [0x1D, 0x21, 0x11];                  // altura e largura dupla
    public static readonly byte[] Cut = [0x1D, 0x56, 0x42, 0x00];                  // avança e corta (parcial)
    public static readonly byte[] Drawer = [0x1B, 0x70, 0x00, 0x19, 0xFA];          // pulso na gaveta (pino 2)
    public static readonly byte[] Beep = [0x1B, 0x42, 0x03, 0x02];                  // 3 bipes
    public static byte[] Feed(int n) => [0x1B, 0x64, (byte)Math.Clamp(n, 0, 20)];

    static readonly Encoding Enc = Encoding.GetEncoding(850);

    public static byte[] Build(PrintJob job)
    {
        var buf = new List<byte>(4096);
        buf.AddRange(Init);
        buf.AddRange(CodePage850);
        if (job.Beep) buf.AddRange(Beep);
        buf.AddRange(job.FontSize switch { "grande" => SizeTall, "extra" => SizeDouble, _ => SizeNormal });
        var text = (job.Text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (!text.EndsWith("\r\n")) text += "\r\n";
        buf.AddRange(Enc.GetBytes(text));
        buf.AddRange(SizeNormal);
        buf.AddRange(Feed(job.FeedLines));
        if (job.CutPaper) buf.AddRange(Cut);
        if (job.OpenDrawer) buf.AddRange(Drawer);
        return buf.ToArray();
    }

    public static PrintJob TestJob(string printer, int paperMm = 80)
    {
        var w = paperMm <= 58 ? 32 : 48;
        var line = new string('-', w);
        var t = $"{"*** TESTE iCONTROL PRINT ***".PadLeft((w + 28) / 2)}\n{line}\nMáquina: {AppInfo.Machine}\nImpressora: {printer}\nVersão: {AppInfo.Version}\nData: {DateTime.Now:dd/MM/yyyy HH:mm:ss}\n{line}\nAcentuação: ação, café, pão, maçã\n";
        return new PrintJob { Id = "teste", Title = "Teste", Text = t, PrinterName = printer, PaperWidthMm = paperMm, CutPaper = true, FeedLines = 4 };
    }
}

/// <summary>Envio RAW: spooler do Windows (winspool) ou rede TCP 9100.</summary>
public static class RawPrinter
{
    public static void Send(string printer, byte[] data, string docName)
    {
        var target = (printer ?? "").Trim();
        if (TryTcp(target, out var host, out var port)) { SendTcp(host, port, data); return; }
        if (target.Length == 0) target = Winspool.DefaultPrinter() ?? throw new InvalidOperationException("Nenhuma impressora padrão no Windows");
        Winspool.SendRaw(target, data, docName);
    }

    // "tcp://192.168.0.50:9100", "192.168.0.50:9100" ou "192.168.0.50"
    static bool TryTcp(string s, out string host, out int port)
    {
        host = ""; port = 9100;
        var v = s.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? s[6..] : s;
        var parts = v.Split(':');
        if (!System.Net.IPAddress.TryParse(parts[0], out _)) return false;
        host = parts[0];
        if (parts.Length > 1 && int.TryParse(parts[1], out var p)) port = p;
        return true;
    }

    static void SendTcp(string host, int port, byte[] data)
    {
        using var client = new TcpClient { SendTimeout = 8000, ReceiveTimeout = 8000 };
        var connect = client.ConnectAsync(host, port);
        if (!connect.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException($"Impressora {host}:{port} não respondeu");
        using var s = client.GetStream();
        s.Write(data, 0, data.Length);
        s.Flush();
    }
}

public static class Winspool
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    class DOC_INFO_1 { public string? pDocName; public string? pOutputFile; public string? pDataType; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PRINTER_INFO_2
    {
        public string pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation;
        public IntPtr pDevMode;
        public string pSepFile, pPrintProcessor, pDatatype, pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 di);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool GetDefaultPrinter(StringBuilder? pszBuffer, ref int size);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool EnumPrinters(int flags, string? name, int level, IntPtr pPrinterEnum, int cbBuf, out int pcbNeeded, out int pcReturned);

    const int PRINTER_ENUM_LOCAL = 2, PRINTER_ENUM_CONNECTIONS = 4;
    const uint PRINTER_ATTRIBUTE_DEFAULT = 0x4, PRINTER_STATUS_OFFLINE = 0x80, PRINTER_STATUS_ERROR = 0x2, PRINTER_STATUS_PAPER_OUT = 0x10, PRINTER_STATUS_PAUSED = 0x1;

    public static void SendRaw(string printer, byte[] data, string docName)
    {
        if (!OpenPrinter(printer, out var h, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Impressora \"{printer}\" não encontrada no Windows");
        var mem = Marshal.AllocCoTaskMem(data.Length);
        try
        {
            if (StartDocPrinter(h, 1, new DOC_INFO_1 { pDocName = docName, pDataType = "RAW" }) == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter falhou");
            try
            {
                if (!StartPagePrinter(h)) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter falhou");
                Marshal.Copy(data, 0, mem, data.Length);
                if (!WritePrinter(h, mem, data.Length, out var written) || written != data.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"WritePrinter enviou {written} de {data.Length} bytes");
                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { Marshal.FreeCoTaskMem(mem); ClosePrinter(h); }
    }

    public static string? DefaultPrinter()
    {
        int size = 0;
        GetDefaultPrinter(null, ref size);
        if (size == 0) return null;
        var sb = new StringBuilder(size);
        return GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
    }

    /// <summary>Impressoras do Windows com status, para o mapeamento ilha -> impressora no sistema.</summary>
    public static JsonArray List()
    {
        var arr = new JsonArray();
        EnumPrinters(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0) return arr;
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, null, 2, buf, needed, out _, out var count)) return arr;
            var size = Marshal.SizeOf<PRINTER_INFO_2>();
            var def = DefaultPrinter();
            for (int i = 0; i < count; i++)
            {
                var p = Marshal.PtrToStructure<PRINTER_INFO_2>(buf + i * size);
                var st = p.Status;
                var status = (st & PRINTER_STATUS_OFFLINE) != 0 ? "offline" : (st & PRINTER_STATUS_PAPER_OUT) != 0 ? "sem_papel"
                    : (st & PRINTER_STATUS_ERROR) != 0 ? "erro" : (st & PRINTER_STATUS_PAUSED) != 0 ? "pausada" : "ok";
                arr.Add(new JsonObject
                {
                    ["name"] = p.pPrinterName,
                    ["port"] = p.pPortName ?? "",
                    ["driver"] = p.pDriverName ?? "",
                    ["default"] = p.pPrinterName == def || (p.Attributes & PRINTER_ATTRIBUTE_DEFAULT) != 0,
                    ["status"] = status,
                    ["jobs"] = (int)p.cJobs,
                });
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return arr;
    }
}

/// <summary>Imprime um job com até N tentativas na principal e, se falhar, na reserva da ilha.</summary>
public sealed class JobPrinter(ILogger<JobPrinter> log, AppSettings cfg)
{
    public (bool ok, string error, string usedPrinter) Print(PrintJob job)
    {
        var data = EscPos.Build(job);
        var doc = $"iControl {job.Title}".Trim();
        string lastError = "";
        foreach (var printer in new[] { job.PrinterName, job.PrinterBackup }.Where((p, i) => i == 0 || !string.IsNullOrWhiteSpace(p)).Distinct())
        {
            for (int attempt = 1; attempt <= Math.Max(1, cfg.MaxAttempts); attempt++)
            {
                try
                {
                    RawPrinter.Send(printer, data, doc);
                    log.LogInformation("Job {Id} ({Title}) impresso em {Printer} (tentativa {N})", job.Id, job.Title, Name(printer), attempt);
                    return (true, "", Name(printer));
                }
                catch (Exception ex)
                {
                    lastError = $"{Name(printer)}: {ex.Message}";
                    log.LogWarning("Job {Id} falhou em {Printer} (tentativa {N}): {Err}", job.Id, Name(printer), attempt, ex.Message);
                    Thread.Sleep(TimeSpan.FromSeconds(attempt * 2));
                }
            }
        }
        return (false, lastError, "");
    }

    static string Name(string p) => string.IsNullOrWhiteSpace(p) ? "padrão do Windows" : p;
}
