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

/// <summary>Falha de impressão com mensagem pronta para o sistema (vai no ack e no Monitor).</summary>
public sealed class PrintFailure(string message) : Exception(message);

/// <summary>Envio RAW: spooler do Windows (winspool) ou rede TCP 9100.</summary>
public static class RawPrinter
{
    public static void Send(string printer, byte[] data, string docName)
    {
        var target = (printer ?? "").Trim();
        if (target.Length == 0) throw new PrintFailure("Ilha sem impressora definida");
        if (TryTcp(target, out var host, out var port)) { SendTcp(host, port, data); return; }
        var compat = Winspool.Compatibility(target);
        if (compat is not null) throw new PrintFailure($"Impressora não aceita impressão direta ({compat})");
        Winspool.SendRaw(target, data, docName);
    }

    // "tcp://192.168.0.50:9100", "192.168.0.50:9100" ou "192.168.0.50"
    public static bool TryTcp(string s, out string host, out int port)
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
        if (!connect.Wait(TimeSpan.FromSeconds(5))) throw new PrintFailure($"Impressora {host}:{port} não respondeu");
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct JOB_INFO_1
    {
        public uint JobId;
        public string pPrinterName, pMachineName, pUserName, pDocument, pDatatype, pStatus;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public SYSTEMTIME Submitted;
    }
    [StructLayout(LayoutKind.Sequential)] struct SYSTEMTIME { public ushort Y, M, DoW, D, H, Mi, S, Ms; }

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
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetJob(IntPtr hPrinter, int jobId, int level, IntPtr pJob, int cbBuf, out int pcbNeeded);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetJob(IntPtr hPrinter, int jobId, int level, IntPtr pJob, int command);

    const int PRINTER_ENUM_LOCAL = 2, PRINTER_ENUM_CONNECTIONS = 4, JOB_CONTROL_DELETE = 5;
    const uint PRINTER_ATTRIBUTE_DEFAULT = 0x4, PRINTER_STATUS_OFFLINE = 0x80, PRINTER_STATUS_ERROR = 0x2, PRINTER_STATUS_PAPER_OUT = 0x10, PRINTER_STATUS_PAUSED = 0x1;
    const uint JOB_STATUS_PAUSED = 0x1, JOB_STATUS_ERROR = 0x2, JOB_STATUS_OFFLINE = 0x20, JOB_STATUS_PAPEROUT = 0x40,
               JOB_STATUS_PRINTED = 0x80, JOB_STATUS_DELETED = 0x100, JOB_STATUS_BLOCKED_DEVQ = 0x200, JOB_STATUS_USER_INTERVENTION = 0x400, JOB_STATUS_COMPLETE = 0x1000;

    sealed record Info(string Name, string Port, string Driver, uint Status, bool IsDefault);

    static List<Info> Enumerate()
    {
        var list = new List<Info>();
        EnumPrinters(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0) return list;
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, null, 2, buf, needed, out _, out var count)) return list;
            var size = Marshal.SizeOf<PRINTER_INFO_2>();
            var def = DefaultPrinter();
            for (int i = 0; i < count; i++)
            {
                var p = Marshal.PtrToStructure<PRINTER_INFO_2>(buf + i * size);
                list.Add(new Info(p.pPrinterName, p.pPortName ?? "", p.pDriverName ?? "", p.Status, p.pPrinterName == def || (p.Attributes & PRINTER_ATTRIBUTE_DEFAULT) != 0));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    // impressoras virtuais (PDF, XPS, Fax, OneNote, arquivo) não aceitam RAW/ESC-POS: o job "some" sem imprimir
    static string? Incompatible(Info p)
    {
        var port = p.Port.Trim().ToUpperInvariant();
        if (port.StartsWith("PORTPROMPT:") || port.StartsWith("SHRFAX:") || port == "FILE:" || port.StartsWith("XPSPORT:") || port == "NUL:" || port.StartsWith("NUL:"))
            return $"porta {p.Port}";
        var drv = p.Driver.ToLowerInvariant();
        if (drv.Contains("pdf") || drv.Contains("xps") || drv.Contains("fax") || drv.Contains("onenote") || drv.Contains("document writer"))
            return $"driver {p.Driver}";
        return null;
    }

    static string StatusText(uint st) => (st & PRINTER_STATUS_OFFLINE) != 0 ? "offline" : (st & PRINTER_STATUS_PAPER_OUT) != 0 ? "sem_papel"
        : (st & PRINTER_STATUS_ERROR) != 0 ? "erro" : (st & PRINTER_STATUS_PAUSED) != 0 ? "pausada" : "ok";

    /// <summary>null = compatível; senão o motivo (porta/driver de impressora virtual).</summary>
    public static string? Compatibility(string name)
    {
        var p = Enumerate().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return p is null ? null : Incompatible(p);
    }

    public static void SendRaw(string printer, byte[] data, string docName)
    {
        var info = Enumerate().FirstOrDefault(x => string.Equals(x.Name, printer, StringComparison.OrdinalIgnoreCase))
            ?? throw new PrintFailure($"Impressora \"{printer}\" não encontrada no Windows");
        var st = StatusText(info.Status);
        if (st is "offline" or "sem_papel" or "pausada") throw new PrintFailure($"Impressora {printer} está {st.Replace('_', ' ')}");

        if (!OpenPrinter(printer, out var h, IntPtr.Zero)) throw new PrintFailure($"Não abriu a impressora \"{printer}\" (erro {Marshal.GetLastWin32Error()})");
        var mem = Marshal.AllocCoTaskMem(data.Length);
        int jobId = 0;
        try
        {
            jobId = StartDocPrinter(h, 1, new DOC_INFO_1 { pDocName = docName, pDataType = "RAW" });
            if (jobId <= 0) throw new PrintFailure($"O spooler recusou o documento (erro {Marshal.GetLastWin32Error()})");
            try
            {
                if (!StartPagePrinter(h)) throw new PrintFailure($"StartPagePrinter falhou (erro {Marshal.GetLastWin32Error()})");
                Marshal.Copy(data, 0, mem, data.Length);
                if (!WritePrinter(h, mem, data.Length, out var written) || written != data.Length)
                    throw new PrintFailure($"Spooler recebeu {written} de {data.Length} bytes (erro {Marshal.GetLastWin32Error()})");
                EndPagePrinter(h);
            }
            finally
            {
                if (!EndDocPrinter(h)) throw new PrintFailure($"EndDocPrinter falhou (erro {Marshal.GetLastWin32Error()})");
            }
            // confere o job na fila do spooler: erro/offline/sem papel => cancela e falha (não marca impresso)
            VerifyJob(h, jobId, printer);
        }
        finally { Marshal.FreeCoTaskMem(mem); ClosePrinter(h); }
    }

    static void VerifyJob(IntPtr h, int jobId, string printer)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            var st = JobStatus(h, jobId);
            if (st is null) return;                                            // saiu da fila: já foi para a impressora
            if ((st.Value & (JOB_STATUS_PRINTED | JOB_STATUS_COMPLETE)) != 0) return;
            if ((st.Value & JOB_STATUS_DELETED) != 0) throw new PrintFailure("O job foi cancelado no spooler");
            var bad = (st.Value & JOB_STATUS_PAPEROUT) != 0 ? "sem papel" : (st.Value & JOB_STATUS_OFFLINE) != 0 ? "offline"
                : (st.Value & (JOB_STATUS_ERROR | JOB_STATUS_USER_INTERVENTION | JOB_STATUS_BLOCKED_DEVQ)) != 0 ? "com erro"
                : (st.Value & JOB_STATUS_PAUSED) != 0 ? "pausada" : null;
            if (bad is not null)
            {
                SetJob(h, jobId, 0, IntPtr.Zero, JOB_CONTROL_DELETE);              // não deixa imprimir depois em dobro
                throw new PrintFailure($"Impressora {printer} {bad} (job cancelado no spooler)");
            }
            Thread.Sleep(400);
        }
        // ainda na fila, sem erro: o spooler aceitou (impressora lenta ou fila com outros jobs)
    }

    static uint? JobStatus(IntPtr h, int jobId)
    {
        GetJob(h, jobId, 1, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return null;
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetJob(h, jobId, 1, buf, needed, out _)) return null;
            return Marshal.PtrToStructure<JOB_INFO_1>(buf).Status;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static string? DefaultPrinter()
    {
        int size = 0;
        GetDefaultPrinter(null, ref size);
        if (size == 0) return null;
        var sb = new StringBuilder(size);
        return GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
    }

    /// <summary>Impressoras do Windows com status e compatibilidade, para o mapeamento ilha -> impressora.</summary>
    public static JsonArray List()
    {
        var arr = new JsonArray();
        foreach (var p in Enumerate())
        {
            var why = Incompatible(p);
            arr.Add(new JsonObject
            {
                ["name"] = p.Name,
                ["port"] = p.Port,
                ["driver"] = p.Driver,
                ["default"] = p.IsDefault,
                ["status"] = StatusText(p.Status),
                ["compatible"] = why is null,
                ["reason"] = why is null ? "" : "Impressora não aceita impressão direta (" + why + ")",
            });
        }
        return arr;
    }
}

/// <summary>Imprime um job com até N tentativas na principal e, se falhar, na reserva da ilha.</summary>
public sealed class JobPrinter(ILogger<JobPrinter> log, AppSettings cfg, ServiceState state)
{
    public (bool ok, string error, string usedPrinter) Print(PrintJob job)
    {
        var printers = new[] { job.PrinterName, job.PrinterBackup }.Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).Distinct().ToList();
        if (printers.Count == 0) return Fail(job, "Ilha sem impressora definida");

        var data = EscPos.Build(job);
        var doc = $"iControl {job.Title}".Trim();
        string lastError = "";
        foreach (var printer in printers)
        {
            for (int attempt = 1; attempt <= Math.Max(1, cfg.MaxAttempts); attempt++)
            {
                try
                {
                    RawPrinter.Send(printer, data, doc);
                    log.LogInformation("Job {Id} ({Title}) impresso em {Printer} (tentativa {N})", job.Id, job.Title, printer, attempt);
                    state.LastPrintOk = DateTime.UtcNow;
                    return (true, "", printer);
                }
                catch (Exception ex)
                {
                    lastError = printers.Count > 1 ? $"{printer}: {ex.Message}" : ex.Message;
                    log.LogWarning("Job {Id} falhou em {Printer} (tentativa {N}): {Err}", job.Id, printer, attempt, ex.Message);
                    // sem impressora / incompatível / não encontrada: repetir não adianta
                    if (ex is PrintFailure pf && (pf.Message.StartsWith("Ilha sem") || pf.Message.StartsWith("Impressora não aceita") || pf.Message.Contains("não encontrada"))) break;
                    Thread.Sleep(TimeSpan.FromSeconds(attempt * 2));
                }
            }
        }
        return Fail(job, lastError);
    }

    (bool, string, string) Fail(PrintJob job, string error)
    {
        state.AddError(string.IsNullOrEmpty(job.IslandName) ? job.Title : $"{job.Title} · {job.IslandName}", error);
        return (false, error, "");
    }
}
