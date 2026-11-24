using System.Text;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>Cores e fontes da marca (iControl Food) para a janela do Monitor.</summary>
static class Brand
{
    public static readonly Color Purple = Color.FromArgb(0x91, 0x00, 0xC5);
    public static readonly Color PurpleSoft = Color.FromArgb(0xF3, 0xE6, 0xFA);
    public static readonly Color Teal = Color.FromArgb(0x00, 0xA8, 0x8F);
    public static readonly Color Green = Color.FromArgb(0x16, 0xA3, 0x4A);
    public static readonly Color Amber = Color.FromArgb(0xD9, 0x77, 0x06);
    public static readonly Color Red = Color.FromArgb(0xDC, 0x26, 0x26);
    public static readonly Color Gray = Color.FromArgb(0x64, 0x74, 0x8B);
    public static readonly Color Back = Color.FromArgb(0xF7, 0xF7, 0xFA);
    public static readonly Color Border = Color.FromArgb(0xE4, 0xE4, 0xEA);
    public static Font F(float size, FontStyle st = FontStyle.Regular) => new("Segoe UI", size, st);

    public static Button Btn(string text, Action a, bool primary = false, Color? color = null)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = F(9.5f, primary ? FontStyle.Bold : FontStyle.Regular),
            Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 0, 8, 8),
            BackColor = primary ? (color ?? Purple) : Color.White, ForeColor = primary ? Color.White : (color ?? Color.FromArgb(0x1F, 0x29, 0x37)),
        };
        b.FlatAppearance.BorderColor = primary ? (color ?? Purple) : Border;
        b.Click += (_, _) => a();
        return b;
    }

    /// <summary>Cartão branco com título.</summary>
    public static (Panel card, FlowLayoutPanel body) Card(string title)
    {
        var card = new Panel { BackColor = Color.White, Padding = new Padding(0, 0, 14, 12), Margin = new Padding(0, 0, 0, 12), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(680, 0) };
        card.Paint += (_, e) => { using var p = new Pen(Border); e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1); };
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(14, 10), BackColor = Color.White };
        body.Controls.Add(new Label { Text = title, AutoSize = true, Font = F(10.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) });
        card.Controls.Add(body);
        return (card, body);
    }
}

/// <summary>
/// Janela do iControl Monitor no padrão do sistema: cabeçalho roxo e menu ao lado (Início, Impressoras, WhatsApp, Sobre).
/// </summary>
sealed class StatusForm : Form
{
    readonly MonitorContext _ctx;
    readonly Label _headTitle = new() { AutoSize = true, Font = Brand.F(14, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.Transparent };
    readonly Label _headSub = new() { AutoSize = true, Font = Brand.F(9.5f), ForeColor = Color.FromArgb(0xEE, 0xDD, 0xFF), BackColor = Color.Transparent };
    readonly Label _headChip = new() { AutoSize = true, Font = Brand.F(9.5f, FontStyle.Bold), Padding = new Padding(10, 4, 10, 4), ForeColor = Color.White };
    readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Brand.Back, Padding = new Padding(18) };
    readonly Dictionary<string, Button> _nav = new();
    readonly Dictionary<string, Control> _pages = new();
    string _page = "inicio";

    // Início
    readonly Label _homeConn = new() { AutoSize = true, Font = Brand.F(10) };
    readonly Label _homePrint = new() { AutoSize = true, Font = Brand.F(10) };
    readonly Label _homeWa = new() { AutoSize = true, Font = Brand.F(10) };
    readonly ListView _errors = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Width = 640, Height = 160, BorderStyle = BorderStyle.None };
    // Impressoras
    readonly ListView _islands = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    readonly Label _islandsNote = new() { AutoSize = true, ForeColor = Brand.Amber, Padding = new Padding(0, 6, 0, 6) };
    // WhatsApp
    readonly Label _waState = new() { AutoSize = true, Font = Brand.F(16, FontStyle.Bold) };
    readonly Label _waDetail = new() { AutoSize = true, Font = Brand.F(10), ForeColor = Brand.Gray, MaximumSize = new Size(420, 0) };
    readonly PictureBox _waQr = new() { Width = 260, Height = 260, SizeMode = PictureBoxSizeMode.Zoom, Visible = false, BackColor = Color.White };
    readonly Label _waQrHelp = new() { AutoSize = true, MaximumSize = new Size(420, 0), Font = Brand.F(9.5f), Visible = false,
        Text = "No celular do restaurante: WhatsApp > Aparelhos conectados > Conectar um aparelho, e aponte para este código. Ele se renova sozinho." };
    readonly Label _waStats = new() { AutoSize = true, Font = Brand.F(10), MaximumSize = new Size(640, 0) };
    readonly ListView _waErrors = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Width = 640, Height = 110, BorderStyle = BorderStyle.None };
    readonly Button _waLogout, _waReconnect;
    string _qrShown = "";
    DateTime _summaryAt = DateTime.MinValue;
    // Sobre
    readonly Label _about = new() { AutoSize = true, Font = Brand.F(10) };

    public StatusForm(MonitorContext ctx)
    {
        _ctx = ctx;
        Text = "iControl Monitor";
        Icon = Icons.Normal;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(940, 640); MinimumSize = new Size(760, 520);
        Font = Brand.F(9.5f);
        BackColor = Brand.Back;

        // cabeçalho roxo
        var head = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Brand.Purple, Padding = new Padding(18, 12, 18, 12) };
        head.Paint += (_, e) =>
        {
            using var br = new System.Drawing.Drawing2D.LinearGradientBrush(head.ClientRectangle, Brand.Purple, Brand.Teal, 0f);
            e.Graphics.FillRectangle(br, head.ClientRectangle);
        };
        var headText = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, Dock = DockStyle.Left, WrapContents = false };
        headText.Controls.Add(_headTitle); headText.Controls.Add(_headSub);
        var chipBox = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0) };
        chipBox.Controls.Add(_headChip);
        head.Controls.Add(headText); head.Controls.Add(chipBox);
        _headTitle.Text = "iControl Monitor";

        // menu ao lado
        var nav = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 200, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.White, Padding = new Padding(10, 14, 10, 10) };
        nav.Paint += (_, e) => { using var p = new Pen(Brand.Border); e.Graphics.DrawLine(p, nav.Width - 1, 0, nav.Width - 1, nav.Height); };
        void Nav(string key, string label)
        {
            var b = new Button { Text = "   " + label, Width = 178, Height = 40, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, Font = Brand.F(10.5f), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 4) };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => Show(key);
            _nav[key] = b; nav.Controls.Add(b);
        }
        Nav("inicio", "Início"); Nav("impressoras", "Impressoras"); Nav("whatsapp", "WhatsApp"); Nav("sobre", "Sobre e suporte");

        _waLogout = Brand.Btn("Desconectar número", () => WaLogout(true), color: Brand.Red);
        _waReconnect = Brand.Btn("Refazer conexão (novo QR Code)", () => WaLogout(false), primary: true, color: Brand.Green);

        _pages["inicio"] = BuildHome();
        _pages["impressoras"] = BuildPrinters();
        _pages["whatsapp"] = BuildWhatsApp();
        _pages["sobre"] = BuildAbout();
        foreach (var p in _pages.Values) { p.Dock = DockStyle.Fill; p.Visible = false; _content.Controls.Add(p); }

        Controls.Add(_content); Controls.Add(nav); Controls.Add(head);
        Show("inicio");
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void Show(string key)
    {
        _page = key;
        foreach (var (k, b) in _nav)
        {
            var on = k == key;
            b.BackColor = on ? Brand.PurpleSoft : Color.White;
            b.ForeColor = on ? Brand.Purple : Color.FromArgb(0x1F, 0x29, 0x37);
            b.Font = Brand.F(10.5f, on ? FontStyle.Bold : FontStyle.Regular);
        }
        foreach (var (k, p) in _pages) p.Visible = k == key;
        if (key == "whatsapp") { _summaryAt = DateTime.MinValue; LoadSummary(); }
    }

    static FlowLayoutPanel Page() => new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Brand.Back };

    Control BuildHome()
    {
        var page = Page();
        var (c1, b1) = Brand.Card("Conexão com o iControl Food"); b1.Controls.Add(_homeConn); page.Controls.Add(c1);
        var (c2, b2) = Brand.Card("Impressão"); b2.Controls.Add(_homePrint);
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        row.Controls.Add(Brand.Btn("Ver impressoras", () => Show("impressoras")));
        row.Controls.Add(Brand.Btn("Abrir pasta dos PDFs", _ctx.OpenPdf));
        b2.Controls.Add(row); page.Controls.Add(c2);
        var (c3, b3) = Brand.Card("WhatsApp"); b3.Controls.Add(_homeWa);
        b3.Controls.Add(Brand.Btn("Abrir WhatsApp", () => Show("whatsapp"))); page.Controls.Add(c3);
        var (c4, b4) = Brand.Card("Últimos erros");
        _errors.Columns.Add("Quando", 110); _errors.Columns.Add("Onde", 180); _errors.Columns.Add("Erro", 340);
        b4.Controls.Add(_errors); page.Controls.Add(c4);
        return page;
    }

    Control BuildPrinters()
    {
        var page = new Panel { BackColor = Brand.Back };
        foreach (var (h, w) in new[] { ("Ilha", 160), ("Ativa", 50), ("Impressora", 220), ("Modo", 100), ("Papel", 70), ("Vias", 45), ("Alterado por", 130) }) _islands.Columns.Add(h, w);
        _islands.DoubleClick += (_, _) => ConfigureSelected();
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        top.Controls.Add(new Label { Text = "Impressora geral e ilhas", AutoSize = true, Font = Brand.F(13, FontStyle.Bold) });
        top.Controls.Add(new Label { AutoSize = true, ForeColor = Brand.Gray, MaximumSize = new Size(640, 0), Margin = new Padding(0, 2, 0, 8),
            Text = "Escolha a impressora de cada ilha (dois cliques). Criar, renomear e escolher os produtos de cada ilha é feito no sistema, em Configurações > Impressoras." });
        var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
        btns.Controls.Add(Brand.Btn("Configurar impressora", ConfigureSelected, primary: true));
        btns.Controls.Add(Brand.Btn("Imprimir teste", TestSelected));
        btns.Controls.Add(Brand.Btn("Atualizar do sistema", () => { PipeClient.Send(new JsonObject { ["cmd"] = "islands" }, 10000); _ctx.Refresh(); }));
        page.Controls.Add(_islands); page.Controls.Add(_islandsNote); page.Controls.Add(top); page.Controls.Add(btns);
        _islandsNote.Dock = DockStyle.Top;
        return page;
    }

    Control BuildWhatsApp()
    {
        var page = Page();
        var (c1, b1) = Brand.Card("Conexão do WhatsApp");
        b1.Controls.Add(_waState); b1.Controls.Add(_waDetail);
        b1.Controls.Add(_waQr); b1.Controls.Add(_waQrHelp);
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        row.Controls.Add(_waReconnect); row.Controls.Add(_waLogout);
        b1.Controls.Add(row);
        page.Controls.Add(c1);
        var (c2, b2) = Brand.Card("Hoje"); b2.Controls.Add(_waStats); page.Controls.Add(c2);
        var (c3, b3) = Brand.Card("Envios com erro (últimas 24 h)");
        _waErrors.Columns.Add("Quando", 110); _waErrors.Columns.Add("Número", 140); _waErrors.Columns.Add("Erro", 370);
        b3.Controls.Add(_waErrors); page.Controls.Add(c3);
        var note = new Label { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Brand.Gray,
            Text = "Ligar o WhatsApp, escolher este computador, os textos dos avisos e o robô de atendimento ficam no sistema: Configurações > WhatsApp." };
        page.Controls.Add(note);
        return page;
    }

    Control BuildAbout()
    {
        var page = Page();
        var (c1, b1) = Brand.Card("Este computador"); b1.Controls.Add(_about); page.Controls.Add(c1);
        var (c2, b2) = Brand.Card("Ações");
        var grid = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(660, 0) };
        grid.Controls.Add(Brand.Btn("Verificar atualização", _ctx.CheckUpdate, primary: true));
        grid.Controls.Add(Brand.Btn("Reiniciar serviço", _ctx.Restart));
        grid.Controls.Add(Brand.Btn("Abrir logs", _ctx.OpenLogs));
        grid.Controls.Add(Brand.Btn("Abrir pasta dos PDFs", _ctx.OpenPdf));
        grid.Controls.Add(Brand.Btn("Parear de novo / Trocar empresa", _ctx.Pair));
        b2.Controls.Add(grid); page.Controls.Add(c2);
        return page;
    }

    JsonObject? SelectedTarget(out bool general)
    {
        general = false;
        if (_islands.SelectedItems.Count == 0) { MessageBox.Show("Selecione uma ilha ou a impressora geral.", "iControl Monitor"); return null; }
        var tag = _islands.SelectedItems[0].Tag as JsonObject;
        general = tag is null;
        return tag;
    }
    void ConfigureSelected() { var t = SelectedTarget(out var general); if (t is not null || general) _ctx.Configure(general ? null : t); }
    void TestSelected()
    {
        var t = SelectedTarget(out var general);
        if (t is null && !general) return;
        var o = t ?? MonitorContext.General(_ctx.Status);
        var printer = o["printer_name"]?.ToString() ?? "";
        if (printer.Length == 0) { MessageBox.Show("Defina a impressora antes de testar.", "iControl Monitor"); return; }
        _ctx.Test(printer, o["print_mode"]?.ToString() ?? "auto", MonitorContext.Paper(o, MonitorContext.DefaultPaper(_ctx.Status)), general ? "Impressora geral" : $"Ilha {o["name"]}");
    }

    void WaLogout(bool disconnect)
    {
        var msg = disconnect
            ? "Desconectar o número do WhatsApp deste computador?\n\nOs avisos de pedido e o robô param até alguém ler o QR Code de novo."
            : "Refazer a conexão? O número atual é desconectado e aparece um QR Code novo para ler com o celular do restaurante.";
        if (MessageBox.Show(msg, "WhatsApp", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var r = PipeClient.Send(new JsonObject { ["cmd"] = "wa_logout" }, 10000);
        if (r?["ok"]?.GetValue<bool>() != true) MessageBox.Show(r?["error"]?.GetValue<string>() ?? "O serviço não respondeu.", "WhatsApp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _ctx.Refresh();
    }

    void LoadSummary()
    {
        if (DateTime.Now - _summaryAt < TimeSpan.FromSeconds(15)) return;
        _summaryAt = DateTime.Now;
        var r = PipeClient.Send(new JsonObject { ["cmd"] = "wa_summary" }, 8000);
        var s = r?["summary"] as JsonObject;
        if (s is null) { _waStats.Text = "Sem dados agora."; return; }
        _waStats.Text = $"Na fila: {s["queue"]}   ·   Enviadas hoje: {s["sent_today"]}   ·   Conversas aguardando atendente: {s["chats_waiting"]}";
        _waErrors.BeginUpdate(); _waErrors.Items.Clear();
        foreach (var e in (s["errors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var at = DateTime.TryParse(e["at"]?.ToString(), out var d) ? d.ToLocalTime().ToString("dd/MM HH:mm") : "";
            _waErrors.Items.Add(new ListViewItem(new[] { at, "+" + e["phone"], e["error"]?.ToString() ?? "" }));
        }
        if (_waErrors.Items.Count == 0) _waErrors.Items.Add(new ListViewItem(new[] { "", "", "Nenhum erro." }));
        _waErrors.EndUpdate();
    }

    static string ModeLabel(string? m) => m switch { "raw" => "Direto", "driver" => "Driver", _ => "Automático" };

    public void Bind(JsonObject? s, string title)
    {
        if (IsDisposed) return;
        var ok = s?["ok"]?.GetValue<bool>() == true;
        string V(string k) => s?[k]?.ToString() ?? "";
        var online = ok && s!["online"]?.GetValue<bool>() == true;
        _headSub.Text = ok ? (V("company").Length > 0 ? $"{V("company")}{(V("cnpj").Length > 0 ? " · CNPJ " + V("cnpj") : "")}" : "Não pareado") : "O serviço não está respondendo";
        _headChip.Text = !ok ? "Serviço parado" : online ? "● Online" : "● Offline";
        _headChip.BackColor = !ok ? Brand.Red : online ? Brand.Green : Brand.Gray;

        if (!ok)
        {
            _homeConn.Text = "O serviço iControl Monitor não está respondendo. Use Sobre e suporte > Reiniciar serviço.";
            return;
        }
        // Início
        var conn = new StringBuilder();
        conn.AppendLine(title);
        conn.AppendLine($"Papel: {(V("role") == "principal" ? "servidor principal" : V("role").Length > 0 ? "servidor reserva" : "-")} · último sinal: {V("last_heartbeat")}");
        if (V("last_heartbeat_error").Length > 0) conn.AppendLine($"Problema de conexão: {V("last_heartbeat_error")}");
        if (V("update_status").Length > 0) conn.Append($"Atualização: {V("update_status")}");
        _homeConn.Text = conn.ToString().TrimEnd();
        _homeConn.ForeColor = online ? Color.FromArgb(0x1F, 0x29, 0x37) : Brand.Red;
        _homePrint.Text = $"Fila no sistema: {V("queue")} · erros nas últimas 2 h: {V("server_errors")}\nÚltima impressão: {(V("last_print_ok").Length > 0 ? V("last_print_ok") : "-")}";

        var wa = V("wa_status");
        var (waText, waColor) = wa switch
        {
            "conectado" => ("Conectado", Brand.Green),
            "aguardando_qr" => ("Aguardando leitura do QR Code", Brand.Amber),
            "iniciando" => ("Iniciando...", Brand.Amber),
            "desconectado" => ("Desconectado", Brand.Red),
            "erro" => ("Erro", Brand.Red),
            _ => ("Desligado neste computador", Brand.Gray),
        };
        _homeWa.Text = $"{waText}{(V("wa_phone").Length > 0 && wa == "conectado" ? " · +" + V("wa_phone") : "")}";
        _homeWa.ForeColor = waColor;

        _errors.BeginUpdate(); _errors.Items.Clear();
        foreach (var e in (s!["errors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            _errors.Items.Add(new ListViewItem(new[] { e["at"]?.ToString() ?? "", e["title"]?.ToString() ?? "", e["message"]?.ToString() ?? "" }));
        if (_errors.Items.Count == 0) _errors.Items.Add(new ListViewItem(new[] { "", "", "Nenhum erro. Tudo certo!" }));
        _errors.EndUpdate();

        // WhatsApp
        _waState.Text = waText; _waState.ForeColor = waColor;
        var det = new StringBuilder();
        if (wa == "conectado") det.AppendLine($"Número +{V("wa_phone")}{(V("wa_connected_at").Length > 0 ? $" · conectado desde {V("wa_connected_at")}" : "")}");
        if (wa == "conectado") det.AppendLine(s["wa_bot"]?.GetValue<bool>() == true ? "Robô de atendimento ligado." : "Robô de atendimento desligado (só avisos de pedido).");
        if (wa is "desligado" or "") det.AppendLine("Para usar, ligue em Configurações > WhatsApp no sistema e escolha este computador.");
        if (V("wa_error").Length > 0 && wa != "conectado") det.AppendLine(V("wa_error"));
        _waDetail.Text = det.ToString().TrimEnd();
        var qr = V("whatsapp_qr");
        var showQr = wa == "aguardando_qr" && qr.Length > 0;
        if (showQr && qr != _qrShown)
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var qd = gen.CreateQrCode(qr, QRCoder.QRCodeGenerator.ECCLevel.L);
            var png = new QRCoder.PngByteQRCode(qd).GetGraphic(6);
            using var ms = new MemoryStream(png);
            var old = _waQr.Image; _waQr.Image = new Bitmap(Image.FromStream(ms)); old?.Dispose();
            _qrShown = qr;
        }
        _waQr.Visible = showQr; _waQrHelp.Visible = showQr;
        _waLogout.Visible = wa == "conectado";
        _waReconnect.Visible = wa is "conectado" or "desconectado" or "erro" or "aguardando_qr";
        if (_page == "whatsapp" && Visible) LoadSummary();

        // Sobre
        var about = new StringBuilder();
        about.AppendLine($"Versão instalada: {V("version")}{(V("latest_version").Length > 0 && V("latest_version") != V("version") ? $" (nova versão publicada: {V("latest_version")})" : "")}");
        about.AppendLine($"Computador: {V("machine")}");
        if (V("company").Length > 0) about.AppendLine($"Empresa: {V("company")}");
        if (V("last_file").Length > 0) about.AppendLine($"Último PDF: {V("last_file")}");
        about.Append("Ajuda: ajuda.icontrolfood.com.br");
        _about.Text = about.ToString();

        // Impressoras
        var def = MonitorContext.DefaultPaper(s);
        string PaperText(JsonObject o) { var w = MonitorContext.Paper(o, def); return w >= 200 ? "A4" : $"{w} mm"; }
        var sel = _islands.SelectedItems.Count > 0 ? _islands.SelectedItems[0].Index : -1;
        _islands.BeginUpdate(); _islands.Items.Clear();
        var g = MonitorContext.General(s);
        _islands.Items.Add(new ListViewItem(new[] { "Impressora geral (cupons)", "sim", g["printer_name"]?.ToString() is { Length: > 0 } gp ? gp : "(não definida)", ModeLabel(g["print_mode"]?.ToString()), PaperText(g), g["copies"]?.ToString() ?? "1", g["updated_by"]?.ToString() ?? "" }) { Tag = null, Font = Brand.F(9.5f, FontStyle.Bold) });
        int missing = 0;
        foreach (var i in MonitorContext.Islands(s))
        {
            var active = i["active"]?.GetValue<bool>() != false;
            var printer = i["printer_name"]?.ToString() ?? "";
            if (active && printer.Length == 0) missing++;
            var item = new ListViewItem(new[] { i["name"]?.ToString() ?? "", active ? "sim" : "não", printer.Length > 0 ? printer : "Nova ilha sem impressora", ModeLabel(i["print_mode"]?.ToString()), PaperText(i), i["copies"]?.ToString() ?? "1", i["updated_by"]?.ToString() ?? "" }) { Tag = i };
            if (!active) item.ForeColor = Color.Gray; else if (printer.Length == 0) item.ForeColor = Brand.Amber;
            _islands.Items.Add(item);
        }
        _islands.EndUpdate();
        if (sel >= 0 && sel < _islands.Items.Count) _islands.Items[sel].Selected = true;
        _islandsNote.Text = missing > 0 ? $"{missing} ilha(s) sem impressora: os pedidos delas dão erro até configurar." : $"Sincronizado com o sistema às {V("config_at")}.";
        _islandsNote.ForeColor = missing > 0 ? Brand.Amber : Brand.Gray;
    }
}
