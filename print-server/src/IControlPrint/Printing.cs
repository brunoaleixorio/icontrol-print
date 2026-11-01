using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using QRCoder;

namespace IControlPrint;

/*
 Marcações aceitas no texto do job (uma por linha, no começo):
   [c]texto   centraliza      [b]texto  negrito      [g]texto  grande (altura e largura dupla)
   [qr]dado   QR code (ex.: endereço no Google Maps)
 Linhas sem marcação saem como estão (o sistema já quebra pela quantidade de colunas da largura do papel).
*/
public sealed record TicketLine(string Text, bool Center, bool Bold, bool Big, string? Qr)
{
    public static List<TicketLine> Parse(string text)
    {
        var list = new List<TicketLine>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            string t = raw; bool c = false, b = false, g = false; string? qr = null;
            while (t.StartsWith('['))
            {
                if (t.StartsWith("[c]")) { c = true; t = t[3..]; }
                else if (t.StartsWith("[b]")) { b = true; t = t[3..]; }
                else if (t.StartsWith("[g]")) { g = true; b = true; t = t[3..]; }
                else if (t.StartsWith("[qr]")) { qr = t[4..].Trim(); t = ""; break; }
                else break;
            }
            list.Add(new TicketLine(t, c, b, g, qr));
        }
        while (list.Count > 0 && list[^1].Text.Length == 0 && list[^1].Qr is null) list.RemoveAt(list.Count - 1);
        return list;
    }
}

/// <summary>Comandos ESC/POS (Epson, Elgin, Bematech, Daruma, Tanca e compatíveis).</summary>
public static class EscPos
{
    public static readonly byte[] Init = [0x1B, 0x40];
    public static readonly byte[] CodePage850 = [0x1B, 0x74, 0x02];                 // PC850 (acentos)
    public static readonly byte[] SizeNormal = [0x1D, 0x21, 0x00];
    public static readonly byte[] SizeTall = [0x1D, 0x21, 0x01];                    // altura dupla
    public static readonly byte[] SizeDouble = [0x1D, 0x21, 0x11];                  // altura e largura dupla
    public static readonly byte[] BoldOn = [0x1B, 0x45, 0x01], BoldOff = [0x1B, 0x45, 0x00];
    public static readonly byte[] AlignLeft = [0x1B, 0x61, 0x00], AlignCenter = [0x1B, 0x61, 0x01];
    public static readonly byte[] Cut = [0x1D, 0x56, 0x42, 0x00];                  // avança e corta (parcial)
    public static readonly byte[] Drawer = [0x1B, 0x70, 0x00, 0x19, 0xFA];          // pulso na gaveta (pino 2)
    public static readonly byte[] Beep = [0x1B, 0x42, 0x03, 0x02];                  // 3 bipes
    public static byte[] Feed(int n) => [0x1B, 0x64, (byte)Math.Clamp(n, 0, 20)];

    static readonly Encoding Enc = Encoding.GetEncoding(850);

    // QR nativo da impressora (GS ( k, modelo 2)
    static IEnumerable<byte> Qr(string data, int module = 6)
    {
        var d = Encoding.UTF8.GetBytes(data);
        int len = d.Length + 3;
        byte pL = (byte)(len % 256), pH = (byte)(len / 256);
        return new byte[] { 0x1D, 0x28, 0x6B, 4, 0, 0x31, 0x41, 0x32, 0x00 }
            .Concat(new byte[] { 0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x43, (byte)module })
            .Concat(new byte[] { 0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x45, 0x30 })
            .Concat(new byte[] { 0x1D, 0x28, 0x6B, pL, pH, 0x31, 0x50, 0x30 }).Concat(d)
            .Concat(new byte[] { 0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x51, 0x30 });
    }

    public static byte[] Build(PrintJob job)
    {
        var buf = new List<byte>(4096);
        buf.AddRange(Init);
        buf.AddRange(CodePage850);
        if (job.Beep) buf.AddRange(Beep);
        var baseSize = job.FontSize switch { "grande" => SizeTall, "extra" => SizeDouble, _ => SizeNormal };
        foreach (var l in TicketLine.Parse(job.Text))
        {
            buf.AddRange(l.Center || l.Qr is not null ? AlignCenter : AlignLeft);
            if (l.Qr is not null) { buf.AddRange(Qr(l.Qr)); buf.AddRange(Enc.GetBytes("\r\n")); continue; }
            buf.AddRange(l.Big ? SizeDouble : baseSize);
            if (l.Bold) buf.AddRange(BoldOn);
            buf.AddRange(Enc.GetBytes((l.Center ? l.Text.Trim() : l.Text) + "\r\n"));
            if (l.Bold) buf.AddRange(BoldOff);
        }
        buf.AddRange(AlignLeft);
        buf.AddRange(SizeNormal);
        buf.AddRange(Feed(job.FeedLines));
        if (job.CutPaper) buf.AddRange(Cut);
        if (job.OpenDrawer) buf.AddRange(Drawer);
        return buf.ToArray();
    }

    public static PrintJob TestJob(string printer, int paperMm = 80, string mode = "auto", string label = "")
    {
        var w = paperMm <= 58 ? 32 : paperMm <= 76 ? 42 : paperMm <= 80 ? 48 : paperMm <= 112 ? 64 : 90;
        var line = new string('-', w);
        var t = $"[c][b]*** TESTE iCONTROL PRINT ***\n{line}\n{(label.Length > 0 ? $"Ilha: {label}\n" : "")}Máquina: {AppInfo.Machine}\nImpressora: {printer}\nPapel: {paperMm} mm ({w} colunas)\nModo: {mode}\nVersão: {AppInfo.Version}\nData: {DateTime.Now:dd/MM/yyyy HH:mm:ss}\n{line}\nAcentuação: ação, café, pão, maçã\n{new string('=', w)}\n[c]fim da largura acima";
        return new PrintJob { Id = "teste", Title = "Teste", Text = t, PrinterName = printer, PaperWidthMm = paperMm, PrintMode = mode, CutPaper = true, FeedLines = 4, OriginType = "teste", OriginNumber = DateTime.Now.ToString("HHmmss") };
    }
}

/// <summary>Falha de impressão com mensagem pronta para o sistema (vai no ack e no Monitor).</summary>
public sealed class PrintFailure(string message) : Exception(message);

/// <summary>Impressoras do Windows: tipo (térmica, arquivo, fax, outra) e modo automático.</summary>
public static class Printers
{
    public sealed record Info(string Name, string Port, string Driver, uint Status, bool IsDefault)
    {
        public string Kind => Printers.KindOf(this);
        public string AutoMode => Kind == "thermal" ? "raw" : "driver";
    }

    static readonly string[] ThermalHints = ["elgin", "bematech", "epson tm", "tm-t", "tm-m", "tm-u", "daruma", "tanca", "generic / text", "generic text", "text only",
        "pos-58", "pos-80", "pos58", "pos80", "pos 58", "pos 80", "58mm", "80mm", "thermal", "térmica", "termica", "sweda", "control id", "controlid", "diebold", "gertec", "knup", "xprinter", "jetway"];

    public static string KindOf(Info p)
    {
        var port = p.Port.Trim().ToUpperInvariant();
        var drv = p.Driver.ToLowerInvariant();
        var name = p.Name.ToLowerInvariant();
        if (port.StartsWith("SHRFAX:") || drv.Contains("fax") || name.Contains("fax")) return "fax";
        if (port.StartsWith("PORTPROMPT:") || port == "FILE:" || port.StartsWith("XPSPORT:") || port.StartsWith("NUL:")
            || drv.Contains("pdf") || drv.Contains("xps") || drv.Contains("onenote") || drv.Contains("document writer")) return "file";
        if (ThermalHints.Any(h => drv.Contains(h) || name.Contains(h))) return "thermal";
        return "other";
    }

    public static Info? Find(string name) => Winspool.Enumerate().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lista para o sistema e o Monitor (nome, porta, driver, status, tipo, modo automático).</summary>
    public static JsonArray List()
    {
        var arr = new JsonArray();
        foreach (var p in Winspool.Enumerate())
        {
            var kind = p.Kind;
            arr.Add(new JsonObject
            {
                ["name"] = p.Name, ["port"] = p.Port, ["driver"] = p.Driver, ["default"] = p.IsDefault,
                ["status"] = Winspool.StatusText(p.Status), ["kind"] = kind, ["auto_mode"] = p.AutoMode,
                ["compatible"] = kind != "fax",
                ["reason"] = kind == "fax" ? "Fax não é suportado" : kind == "file" ? "Imprime em arquivo (PDF/XPS) no modo Driver" : "",
            });
        }
        return arr;
    }
}

/// <summary>Envio RAW: spooler do Windows (winspool) ou rede TCP 9100.</summary>
public static class RawPrinter
{
    public static void Send(string target, byte[] data, string docName)
    {
        if (TryTcp(target, out var host, out var port)) { SendTcp(host, port, data); return; }
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

/// <summary>
/// Modo Driver do Windows: desenha o cupom (GDI/PrintDocument) com fonte monoespaçada, negrito, tamanho e QR.
/// Página na largura configurada e altura do conteúdo (rolo contínuo); A4 (210 mm) pagina normal.
/// Impressoras de arquivo (PDF/XPS/PORTPROMPT:) salvam em C:\iControlPrint\PDF sem diálogo.
/// </summary>
public static class DriverPrinter
{
    public static string PdfFolder => @"C:\iControlPrint\PDF";

    /// <summary>Retorna o caminho do arquivo quando a impressora é de arquivo; senão null.</summary>
    public static string? Print(PrintJob job, Printers.Info printer, string docName)
    {
        var lines = TicketLine.Parse(job.Text);
        int widthMm = job.PaperWidthMm > 0 ? job.PaperWidthMm : 80;
        bool a4 = widthMm >= 200;
        int cols = Math.Max(20, lines.Where(l => l.Qr is null).Select(l => (l.Big ? 2 : 1) * l.Text.TrimEnd().Length).DefaultIfEmpty(48).Max());
        cols = Math.Max(cols, widthMm <= 58 ? 32 : widthMm <= 80 ? 48 : widthMm <= 112 ? 64 : 90);

        // largura útil em centésimos de polegada (margem de 2 mm de cada lado no rolo)
        float pageW = widthMm / 25.4f * 100f;
        float marginX = a4 ? 50f : 2f / 25.4f * 100f;
        float usable = pageW - 2 * marginX;

        using var probe = new Bitmap(1, 1);
        using var gProbe = Graphics.FromImage(probe);
        gProbe.PageUnit = GraphicsUnit.Display; // 1/100 pol.
        float size = 9f;
        using (var f = new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point))
        {
            var w = gProbe.MeasureString(new string('M', 40), f, int.MaxValue, StringFormat.GenericTypographic).Width / 40f;
            size = Math.Clamp(size * (usable / cols) / Math.Max(0.01f, w), 5f, 14f);
        }
        using var font = new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point);
        using var bold = new Font("Consolas", size, FontStyle.Bold, GraphicsUnit.Point);
        using var big = new Font("Consolas", size * 1.6f, FontStyle.Bold, GraphicsUnit.Point);
        float lineH = font.GetHeight(gProbe) * 100f / gProbe.DpiY;
        float bigH = big.GetHeight(gProbe) * 100f / gProbe.DpiY;
        float qrSize = Math.Min(usable * 0.55f, 120f);
        float contentH = lines.Sum(l => l.Qr is not null ? qrSize + lineH : l.Big ? bigH : lineH) + lineH * Math.Max(1, job.FeedLines);
        float marginTop = a4 ? 50f : 8f;

        string? file = null;
        using var doc = new PrintDocument { DocumentName = docName, PrintController = new StandardPrintController() };
        doc.PrinterSettings.PrinterName = printer.Name;
        if (!doc.PrinterSettings.IsValid) throw new PrintFailure($"Impressora \"{printer.Name}\" não encontrada no Windows");
        if (printer.Kind == "file")
        {
            Directory.CreateDirectory(PdfFolder);
            var ext = printer.Driver.Contains("xps", StringComparison.OrdinalIgnoreCase) || printer.Port.StartsWith("XPSPort", StringComparison.OrdinalIgnoreCase) ? ".xps" : ".pdf";
            var origin = string.IsNullOrWhiteSpace(job.OriginType) ? "impressao" : job.OriginType;
            var number = string.IsNullOrWhiteSpace(job.OriginNumber) ? job.Id[..Math.Min(8, job.Id.Length)] : job.OriginNumber;
            file = Path.Combine(PdfFolder, Safe($"{origin}-{number}-{DateTime.Now:yyyyMMdd-HHmmss}") + ext);
            doc.PrinterSettings.PrintToFile = true;
            doc.PrinterSettings.PrintFileName = file;
        }
        if (!a4)
        {
            // rolo: largura configurada e altura do conteúdo (mínimo 10 cm)
            var ps = new PaperSize($"iControl {widthMm}mm", (int)Math.Ceiling(pageW), (int)Math.Ceiling(Math.Max(394f, contentH + marginTop * 2)));
            doc.DefaultPageSettings.PaperSize = ps;
        }
        else
        {
            var a4Size = doc.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.A4);
            if (a4Size is not null) doc.DefaultPageSettings.PaperSize = a4Size;
        }
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.OriginAtMargins = false;

        int idx = 0;
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Display;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float y = marginTop;
            float bottom = e.PageBounds.Height - (a4 ? 50f : 4f);
            while (idx < lines.Count)
            {
                var l = lines[idx];
                float h = l.Qr is not null ? qrSize + lineH : l.Big ? bigH : lineH;
                if (a4 && y + h > bottom && y > marginTop) { e.HasMorePages = true; return; }
                if (l.Qr is not null)
                {
                    using var img = QrImage(l.Qr);
                    g.DrawImage(img, marginX + (usable - qrSize) / 2f, y, qrSize, qrSize);
                }
                else
                {
                    var f = l.Big ? big : l.Bold ? bold : font;
                    var text = l.Center ? l.Text.Trim() : l.Text;
                    float x = marginX;
                    if (l.Center) x = marginX + Math.Max(0, (usable - g.MeasureString(text, f, int.MaxValue, StringFormat.GenericTypographic).Width) / 2f);
                    g.DrawString(text, f, Brushes.Black, x, y, StringFormat.GenericTypographic);
                }
                y += h; idx++;
            }
            e.HasMorePages = false;
        };
        try { doc.Print(); }
        catch (Exception ex) when (ex is not PrintFailure) { throw new PrintFailure($"Driver do Windows recusou a impressão: {ex.Message}"); }

        if (file is not null)
        {
            // confere o arquivo criado (o driver de PDF grava depois que o spooler processa)
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until && !(File.Exists(file) && new FileInfo(file).Length > 0)) Thread.Sleep(300);
            if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new PrintFailure($"O arquivo não foi criado: {file}");
        }
        else Winspool.VerifyLastJob(printer.Name, docName);
        return file;
    }

    static Bitmap QrImage(string data)
    {
        using var gen = new QRCodeGenerator();
        using var qd = gen.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qd).GetGraphic(8);
        using var ms = new MemoryStream(png);
        return new Bitmap(ms);
    }

    static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));
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
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool EnumJobs(IntPtr hPrinter, int firstJob, int noJobs, int level, IntPtr pJob, int cbBuf, out int pcbNeeded, out int pcReturned);

    const int PRINTER_ENUM_LOCAL = 2, PRINTER_ENUM_CONNECTIONS = 4, JOB_CONTROL_DELETE = 5;
    const uint PRINTER_ATTRIBUTE_DEFAULT = 0x4, PRINTER_STATUS_OFFLINE = 0x80, PRINTER_STATUS_ERROR = 0x2, PRINTER_STATUS_PAPER_OUT = 0x10, PRINTER_STATUS_PAUSED = 0x1;
    const uint JOB_STATUS_PAUSED = 0x1, JOB_STATUS_ERROR = 0x2, JOB_STATUS_OFFLINE = 0x20, JOB_STATUS_PAPEROUT = 0x40,
               JOB_STATUS_PRINTED = 0x80, JOB_STATUS_DELETED = 0x100, JOB_STATUS_BLOCKED_DEVQ = 0x200, JOB_STATUS_USER_INTERVENTION = 0x400, JOB_STATUS_COMPLETE = 0x1000;

    public static List<Printers.Info> Enumerate()
    {
        var list = new List<Printers.Info>();
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
                list.Add(new Printers.Info(p.pPrinterName, p.pPortName ?? "", p.pDriverName ?? "", p.Status, p.pPrinterName == def || (p.Attributes & PRINTER_ATTRIBUTE_DEFAULT) != 0));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public static string StatusText(uint st) => (st & PRINTER_STATUS_OFFLINE) != 0 ? "offline" : (st & PRINTER_STATUS_PAPER_OUT) != 0 ? "sem_papel"
        : (st & PRINTER_STATUS_ERROR) != 0 ? "erro" : (st & PRINTER_STATUS_PAUSED) != 0 ? "pausada" : "ok";

    public static void EnsureReady(Printers.Info info)
    {
        var st = StatusText(info.Status);
        if (st is "offline" or "sem_papel" or "pausada") throw new PrintFailure($"Impressora {info.Name} está {st.Replace('_', ' ')}");
    }

    public static void SendRaw(string printer, byte[] data, string docName)
    {
        if (!OpenPrinter(printer, out var h, IntPtr.Zero)) throw new PrintFailure($"Não abriu a impressora \"{printer}\" (erro {Marshal.GetLastWin32Error()})");
        var mem = Marshal.AllocCoTaskMem(data.Length);
        try
        {
            int jobId = StartDocPrinter(h, 1, new DOC_INFO_1 { pDocName = docName, pDataType = "RAW" });
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
            VerifyJob(h, jobId, printer);
        }
        finally { Marshal.FreeCoTaskMem(mem); ClosePrinter(h); }
    }

    /// <summary>Modo Driver: acha o job pelo nome do documento na fila e confere se ficou sem erro.</summary>
    public static void VerifyLastJob(string printer, string docName)
    {
        if (!OpenPrinter(printer, out var h, IntPtr.Zero)) return;
        try
        {
            var id = FindJob(h, docName);
            if (id > 0) VerifyJob(h, id, printer);
        }
        finally { ClosePrinter(h); }
    }

    static int FindJob(IntPtr h, string docName)
    {
        EnumJobs(h, 0, 50, 1, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0) return 0;
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumJobs(h, 0, 50, 1, buf, needed, out _, out var count)) return 0;
            var size = Marshal.SizeOf<JOB_INFO_1>();
            int found = 0;
            for (int i = 0; i < count; i++)
            {
                var j = Marshal.PtrToStructure<JOB_INFO_1>(buf + i * size);
                if (j.pDocument == docName) found = (int)j.JobId;
            }
            return found;
        }
        finally { Marshal.FreeHGlobal(buf); }
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
}

/// <summary>Imprime um job: escolhe o modo (Direto ESC/POS ou Driver), tenta a principal e a reserva, confere o spooler.</summary>
public sealed class JobPrinter(ILogger<JobPrinter> log, AppSettings cfg, ServiceState state)
{
    public (bool ok, string error, string usedPrinter) Print(PrintJob job)
    {
        var printers = new[] { job.PrinterName, job.PrinterBackup }.Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).Distinct().ToList();
        if (printers.Count == 0) return Fail(job, job.Target == "geral" ? "Impressora geral do sistema não definida" : "Ilha sem impressora definida");

        var doc = $"iControl {job.Title} {job.Id[..Math.Min(8, job.Id.Length)]}".Trim();
        string lastError = "";
        foreach (var printer in printers)
        {
            for (int attempt = 1; attempt <= Math.Max(1, cfg.MaxAttempts); attempt++)
            {
                try
                {
                    var how = PrintOne(job, printer, doc);
                    log.LogInformation("Job {Id} ({Title}) impresso em {Printer} [{How}] (tentativa {N})", job.Id, job.Title, printer, how, attempt);
                    state.LastPrintOk = DateTime.UtcNow;
                    return (true, "", printer);
                }
                catch (Exception ex)
                {
                    lastError = printers.Count > 1 ? $"{printer}: {ex.Message}" : ex.Message;
                    log.LogWarning("Job {Id} falhou em {Printer} (tentativa {N}): {Err}", job.Id, printer, attempt, ex.Message);
                    if (ex is PrintFailure pf && (pf.Message.Contains("não encontrada") || pf.Message.Contains("não é suportado") || pf.Message.Contains("não aceita"))) break;
                    Thread.Sleep(TimeSpan.FromSeconds(attempt * 2));
                }
            }
        }
        return Fail(job, lastError);
    }

    /// <summary>Imprime em uma impressora. Retorna como saiu ("Direto", "Driver" ou o arquivo).</summary>
    public string PrintOne(PrintJob job, string printer, string doc)
    {
        if (RawPrinter.TryTcp(printer, out _, out _)) { RawPrinter.Send(printer, EscPos.Build(job), doc); return "Direto (rede)"; }
        var info = Printers.Find(printer) ?? throw new PrintFailure($"Impressora \"{printer}\" não encontrada no Windows");
        if (info.Kind == "fax") throw new PrintFailure("Impressora de fax não é suportada");
        Winspool.EnsureReady(info);
        var mode = job.PrintMode is "raw" or "driver" ? job.PrintMode : info.AutoMode;
        if (info.Kind == "file") mode = "driver";                                // PDF/XPS só pelo driver
        if (mode == "raw") { Winspool.SendRaw(info.Name, EscPos.Build(job), doc); return "Direto"; }
        var file = DriverPrinter.Print(job, info, doc);
        if (file is not null)
        {
            state.LastFile = file;
            log.LogInformation("Arquivo salvo: {File}", file);
            return "arquivo " + file;
        }
        return "Driver";
    }

    (bool, string, string) Fail(PrintJob job, string error)
    {
        state.AddError(string.IsNullOrEmpty(job.IslandName) ? job.Title : $"{job.Title} · {job.IslandName}", error);
        return (false, error, "");
    }
}
