using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.Json.Nodes;
using QRCoder;

namespace IControlPrint;

/// <summary>
/// Balança na porta serial deste computador (Toledo, Filizola, Urano: protocolo ENQ).
/// O sistema pede o peso pelo banco (tablets e outros caixas) ou pela ponte local (http://127.0.0.1).
/// </summary>
public static class ScaleReader
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<(double weight, string raw)> ReadAsync(JsonObject monitorConfig, CancellationToken ct = default)
    {
        var sc = monitorConfig["scale"] as JsonObject;
        var portName = (sc?["port"]?.ToString() ?? "").Trim().ToUpperInvariant();
        if (portName.Length == 0) throw new PrintFailure("Balança sem porta configurada (Configurações › PDV › Balança).");
        int baud = int.TryParse(sc?["baud"]?.ToString(), out var b) && b > 0 ? b : 9600;
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(5), ct)) throw new PrintFailure("A balança está ocupada. Tente de novo.");
        try
        {
            return await Task.Run(() =>
            {
                using var sp = new SerialPort(portName, baud, Parity.None, 8, StopBits.One) { ReadTimeout = 300, WriteTimeout = 1000, Encoding = Encoding.Latin1 };
                try { sp.Open(); }
                catch (Exception ex) { throw new PrintFailure($"Não abriu a porta {portName} da balança: {ex.Message}"); }
                sp.DiscardInBuffer();
                sp.Write(new byte[] { 0x05 }, 0, 1); // ENQ
                var sb = new StringBuilder();
                var until = DateTime.UtcNow.AddSeconds(2);
                var buf = new byte[64];
                while (DateTime.UtcNow < until)
                {
                    try
                    {
                        int n = sp.Read(buf, 0, buf.Length);
                        sb.Append(Encoding.Latin1.GetString(buf, 0, n));
                        if (sb.ToString().Contains('\x03')) break;
                    }
                    catch (TimeoutException) { if (sb.Length > 0 && sb.ToString().Contains('\x03')) break; }
                }
                var raw = sb.ToString();
                return (Parse(raw), raw);
            }, ct);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Mesma leitura do sistema (src/utils/scale.js): "STX 01.250 ETX", "02500", IIIII instável, SSSSS sobrecarga.</summary>
    public static double Parse(string raw)
    {
        var s = new string((raw ?? "").Select(c => c < 32 ? ' ' : c).ToArray()).Trim();
        if (s.Contains("III")) throw new PrintFailure("Peso instável. Espere a balança parar e tente de novo.");
        if (s.Contains("SSS")) throw new PrintFailure("Peso acima da capacidade da balança.");
        if (s.Contains("NNN")) throw new PrintFailure("Peso negativo. Zere a balança.");
        var m = System.Text.RegularExpressions.Regex.Match(s, @"\d+[.,]\d+|\d+");
        if (!m.Success) throw new PrintFailure("A balança não respondeu com o peso.");
        var v = m.Value.Contains('.') || m.Value.Contains(',')
            ? double.Parse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture)
            : double.Parse(m.Value, CultureInfo.InvariantCulture) / 1000.0;
        return Math.Round(v, 3);
    }
}

/// <summary>
/// Etiquetas de produção e validade: ZPL (Zebra/Elgin), PPLA (Argox/Elgin) ou pelo driver do Windows.
/// Campos: nome, fabricação, validade, lote, responsável, peso, observação, empresa e QR Code.
/// </summary>
public static class LabelPrinter
{
    public sealed record Label(string Name, string Made, string Expiry, string Lot, string Responsible, string Weight, string Extra, string Company, string Qr);

    public static Label From(JsonNode? n) => new(
        S(n?["name"]), S(n?["made"]), S(n?["expiry"]), S(n?["lot"]), S(n?["responsible"]), S(n?["weight"]), S(n?["extra"]), S(n?["company"]), S(n?["qr"]));

    static string S(JsonNode? v) => v?.ToString() ?? "";

    public static void Print(JsonObject monitorConfig, JsonNode? payload)
    {
        var cfg = monitorConfig["labels"] as JsonObject ?? throw new PrintFailure("Impressora de etiquetas não configurada neste computador.");
        var printer = S(cfg["printer_name"]).Trim();
        if (printer.Length == 0) throw new PrintFailure("Impressora de etiquetas não configurada neste computador.");
        var lang = S(cfg["language"]).ToLowerInvariant();
        double w = Num(cfg["width_mm"], 60), h = Num(cfg["height_mm"], 40);
        int dpi = (int)Num(cfg["dpi"], 203);
        int copies = Math.Clamp((int)Num(payload?["copies"], 1), 1, 500);
        var label = From(payload?["label"]);
        if (label.Name.Length == 0) throw new PrintFailure("Etiqueta sem nome do produto.");
        var doc = $"iControl Etiqueta {label.Lot}".Trim();
        switch (lang)
        {
            case "zpl": RawPrinter.Send(printer, Zpl(label, w, h, dpi, copies), doc); break;
            case "ppla": RawPrinter.Send(printer, Ppla(label, h, copies), doc); break;
            default: Driver(printer, label, w, h, copies, doc); break;
        }
    }

    static double Num(JsonNode? v, double d) => double.TryParse(v?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x) && x > 0 ? x : d;

    static List<string> Lines(Label l)
    {
        var lines = new List<string>();
        if (l.Made.Length > 0) lines.Add($"Fab.: {l.Made}");
        if (l.Expiry.Length > 0) lines.Add($"Val.: {l.Expiry}");
        if (l.Lot.Length > 0) lines.Add($"Lote: {l.Lot}");
        if (l.Weight.Length > 0) lines.Add($"Peso: {l.Weight}");
        if (l.Responsible.Length > 0) lines.Add($"Resp.: {l.Responsible}");
        if (l.Extra.Length > 0) lines.Add(l.Extra);
        if (l.Company.Length > 0) lines.Add(l.Company);
        return lines;
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    // ZPL: ^CI28 = UTF-8 (acentos). Medidas em pontos (203 dpi = 8 pontos por mm).
    static byte[] Zpl(Label l, double wMm, double hMm, int dpi, int copies)
    {
        double dot = dpi / 25.4;
        int W = (int)(wMm * dot), H = (int)(hMm * dot), m = (int)(2 * dot);
        bool qr = l.Qr.Length > 0 && hMm >= 25;
        int qrSize = qr ? (int)Math.Min(H - 2 * m, W * 0.38) : 0;
        int textW = W - 2 * m - (qr ? qrSize + m : 0);
        int nameH = (int)(3.6 * dot), lineH = (int)(2.6 * dot);
        int charsPerLine = Math.Max(10, textW / (lineH * 55 / 100));
        var z = new StringBuilder();
        z.Append($"^XA^CI28^PW{W}^LL{H}^LH0,0");
        int y = m;
        z.Append($"^FO{m},{y}^A0N,{nameH},{nameH}^FB{textW},2,0,L^FD{Esc(l.Name)}^FS");
        y += nameH * 2 + (int)dot;
        foreach (var line in Lines(l))
        {
            if (y + lineH > H - m) break;
            z.Append($"^FO{m},{y}^A0N,{lineH},{lineH}^FD{Esc(Clip(line, charsPerLine))}^FS");
            y += lineH + (int)(0.6 * dot);
        }
        if (qr)
        {
            int mag = Math.Clamp(qrSize / 30, 2, 10);
            z.Append($"^FO{W - m - qrSize},{m}^BQN,2,{mag}^FDQA,{Esc(l.Qr.Replace("\n", " | "))}^FS");
        }
        z.Append($"^PQ{copies}^XZ");
        return Encoding.UTF8.GetBytes(z.ToString());
    }

    static string Esc(string s) => s.Replace("^", " ").Replace("~", " ");

    // PPLA (Argox): STX L, unidade métrica (décimos de mm), fonte interna 2/3, quantidade Q.
    static byte[] Ppla(Label l, double hMm, int copies)
    {
        const char STX = '\x02';
        var p = new StringBuilder();
        p.Append($"{STX}m\r");                 // métrico
        p.Append($"{STX}L\r");
        p.Append("D11\r");                      // pontos 1x1
        int row = (int)(hMm * 10) - 40;         // linha (de baixo para cima, em 0,1 mm)
        int col = 20;
        p.Append($"1311000{row:0000}{col:0000}{Ascii(Clip(l.Name, 28))}\r");
        row -= 50;
        foreach (var line in Lines(l))
        {
            if (row < 20) break;
            p.Append($"1211000{row:0000}{col:0000}{Ascii(Clip(line, 36))}\r");
            row -= 35;
        }
        p.Append($"Q{copies:0000}\r");
        p.Append("E\r");
        return Encoding.Latin1.GetBytes(p.ToString());
    }

    static string Ascii(string s)
    {
        var n = s.Normalize(NormalizationForm.FormD);
        return new string(n.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c < 128).ToArray());
    }

    // Driver do Windows: desenha a etiqueta no tamanho configurado (qualquer impressora, inclusive em modo Windows)
    static void Driver(string printer, Label l, double wMm, double hMm, int copies, string docName)
    {
        float W = (float)(wMm / 25.4 * 100), H = (float)(hMm / 25.4 * 100), m = (float)(2 / 25.4 * 100);
        bool qr = l.Qr.Length > 0 && hMm >= 25;
        float qrSize = qr ? Math.Min(H - 2 * m, W * 0.38f) : 0;
        using var qrImg = qr ? QrBitmap(l.Qr) : null;
        using var doc = new PrintDocument { DocumentName = docName, PrintController = new StandardPrintController() };
        doc.PrinterSettings.PrinterName = printer;
        if (!doc.PrinterSettings.IsValid) throw new PrintFailure($"Impressora \"{printer}\" não encontrada no Windows");
        doc.DefaultPageSettings.PaperSize = new PaperSize($"Etiqueta {wMm}x{hMm}", (int)Math.Ceiling(W), (int)Math.Ceiling(H));
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.OriginAtMargins = false;
        int printed = 0;
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Display;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float textW = W - 2 * m - (qr ? qrSize + m : 0);
            using var nameFont = new Font("Arial", 10f, FontStyle.Bold, GraphicsUnit.Point);
            using var font = new Font("Arial", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
            float y = m;
            var nameRect = new RectangleF(m, y, textW, nameFont.GetHeight(g) * 2);
            g.DrawString(l.Name, nameFont, Brushes.Black, nameRect);
            y += g.MeasureString(l.Name, nameFont, (int)textW).Height + 2;
            float lh = font.GetHeight(g);
            foreach (var line in Lines(l))
            {
                if (y + lh > H - m) break;
                g.DrawString(line, font, Brushes.Black, new RectangleF(m, y, textW, lh));
                y += lh;
            }
            if (qrImg is not null) g.DrawImage(qrImg, W - m - qrSize, m, qrSize, qrSize);
            printed++;
            e.HasMorePages = printed < copies;
        };
        try { doc.Print(); }
        catch (Exception ex) when (ex is not PrintFailure) { throw new PrintFailure($"Driver do Windows recusou a etiqueta: {ex.Message}"); }
    }

    static Bitmap QrBitmap(string data)
    {
        using var gen = new QRCodeGenerator();
        using var qd = gen.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(qd);
        using var ms = new MemoryStream(code.GetGraphic(8));
        return new Bitmap(ms);
    }
}
