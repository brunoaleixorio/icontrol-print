using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>
/// iControl Print Monitor: ícone na bandeja para o usuário logado. Situação do serviço, ilhas e impressora geral
/// (lidas do banco pelo serviço, nunca por company_id), teste, reiniciar, logs, pasta dos PDFs, pareamento e avisos.
/// </summary>
public static class TrayMonitor
{
    public static void Run(bool show)
    {
        using var single = new Mutex(true, @"Local\iControlPrintMonitor", out var first);
        if (!first) { if (show) PipeClient.SignalShow(); return; }
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var ctx = new MonitorContext(show);
        Application.Run(ctx);
    }
}

/// <summary>Conversa com o serviço pelo pipe local (identificação permite ao serviço saber se é administrador).</summary>
static class PipeClient
{
    public static JsonObject? Send(JsonObject req, int timeoutMs = 2500)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
            pipe.Connect(timeoutMs);
            using var w = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var r = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            w.WriteLine(req.ToJsonString());
            var line = r.ReadLine();
            return line is null ? null : JsonNode.Parse(line) as JsonObject;
        }
        catch { return null; }
    }

    public static void SignalShow() { try { using var e = EventWaitHandle.OpenExisting(@"Local\iControlPrintMonitorShow"); e.Set(); } catch { } }
}

/// <summary>Ícones com a marca (Garfo-i): normal, cinza (offline) e com alerta (erro).</summary>
static class Icons
{
    static readonly Dictionary<string, Icon> Cache = new();
    public static Icon Get(string name)
    {
        if (Cache.TryGetValue(name, out var i)) return i;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name + ".ico");
        var icon = s is null ? SystemIcons.Application : new Icon(s, SystemInformation.SmallIconSize);
        Cache[name] = icon;
        return icon;
    }
    public static Icon Normal => Get("icon");
    public static Icon Gray => Get("icon-gray");
    public static Icon Alert => Get("icon-alert");
}

sealed class MonitorContext : ApplicationContext
{
    readonly NotifyIcon _tray = new();
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _header = new() { Enabled = false };
    readonly ToolStripMenuItem _testMenu = new("Imprimir teste");
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer _showTimer = new() { Interval = 1000 };
    readonly EventWaitHandle _showEvent = new(false, EventResetMode.AutoReset, @"Local\iControlPrintMonitorShow");
    StatusForm? _form;
    public JsonObject? Status { get; private set; }
    string _lastErrorKey = "";
    DateTime _lastErrorAt = DateTime.MinValue;
    bool? _wasOnline;
    readonly Dictionary<string, string> _printerStatus = new();
    readonly HashSet<string> _islandsSeen = new();
    bool _first = true;

    public MonitorContext(bool show)
    {
        _menu.Items.Add(_header);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Abrir status e ilhas", null, (_, _) => ShowForm());
        _menu.Items.Add(_testMenu);
        _menu.Items.Add("Reiniciar serviço", null, (_, _) => Restart());
        _menu.Items.Add("Abrir logs", null, (_, _) => OpenLogs());
        _menu.Items.Add("Abrir pasta dos PDFs", null, (_, _) => OpenPdf());
        _menu.Items.Add("Parear de novo / Trocar empresa", null, (_, _) => Pair());
        _menu.Items.Add("Verificar atualização", null, (_, _) => CheckUpdate());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Fechar o Monitor", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = _menu;
        _tray.Text = "iControl Print";
        _tray.Icon = Icons.Gray;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowForm();
        _tray.BalloonTipClicked += (_, _) => ShowForm();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        _showTimer.Tick += (_, _) => { if (_showEvent.WaitOne(0)) ShowForm(); };
        _showTimer.Start();
        Refresh();
        if (show) ShowForm();
    }

    public void Refresh()
    {
        var s = PipeClient.Send(new JsonObject { ["cmd"] = "status" });
        Status = s;
        bool reachable = s?["ok"]?.GetValue<bool>() == true;
        bool online = reachable && s!["online"]?.GetValue<bool>() == true;
        string company = s?["company"]?.GetValue<string>() ?? "";
        bool recentError = DateTime.Now - _lastErrorAt < TimeSpan.FromMinutes(10);

        string title;
        if (!reachable) { title = "Serviço parado"; _tray.Icon = Icons.Gray; }
        else if (s!["paired"]?.GetValue<bool>() != true) { title = "Não pareado"; _tray.Icon = Icons.Alert; }
        else if (s["revoked"]?.GetValue<bool>() == true) { title = "Revogado: pareie de novo"; _tray.Icon = Icons.Alert; }
        else if (!online) { title = $"Offline ({company})"; _tray.Icon = Icons.Gray; }
        else { title = $"Conectado a {company}"; _tray.Icon = recentError || NoPrinterIslands(s).Any() ? Icons.Alert : Icons.Normal; }
        _header.Text = title;
        var tip = $"iControl Print · {title}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;

        _testMenu.DropDownItems.Clear();
        foreach (var t in TestTargets())
        {
            var (label, printer, mode, paper) = t;
            var item = new ToolStripMenuItem(label) { Enabled = reachable && printer.Length > 0 };
            item.Click += (_, _) => Test(printer, mode, paper, label);
            _testMenu.DropDownItems.Add(item);
        }
        if (_testMenu.DropDownItems.Count == 0) _testMenu.DropDownItems.Add(new ToolStripMenuItem("Nenhuma impressora") { Enabled = false });

        Notify(s, reachable, online);
        _form?.Bind(s, title);
        _first = false;
    }

    public static IEnumerable<JsonObject> Islands(JsonObject? s) => (s?["config"]?["islands"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();
    public static JsonObject General(JsonObject? s) => s?["config"]?["general"] as JsonObject ?? new JsonObject();
    public static int DefaultPaper(JsonObject? s) => (int?)s?["config"]?["default_paper_mm"] ?? 80;
    static IEnumerable<JsonObject> NoPrinterIslands(JsonObject? s) => Islands(s).Where(i => i["active"]?.GetValue<bool>() != false && string.IsNullOrEmpty(i["printer_name"]?.ToString()));

    /// <summary>Alvos de teste: ilhas, impressora geral e cada impressora do Windows.</summary>
    public IEnumerable<(string label, string printer, string mode, int paper)> TestTargets()
    {
        var s = Status; var def = DefaultPaper(s);
        foreach (var i in Islands(s).Where(i => i["active"]?.GetValue<bool>() != false))
            yield return ($"Ilha {i["name"]}", i["printer_name"]?.ToString() ?? "", i["print_mode"]?.ToString() ?? "auto", Paper(i, def));
        var g = General(s);
        if (!string.IsNullOrEmpty(g["printer_name"]?.ToString())) yield return ("Impressora geral", g["printer_name"]!.ToString(), g["print_mode"]?.ToString() ?? "auto", Paper(g, def));
        foreach (var p in (s?["printers"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(p => p["kind"]?.ToString() != "fax"))
            yield return (p["name"]?.ToString() ?? "", p["name"]?.ToString() ?? "", "auto", def);
    }
    public static int Paper(JsonObject o, int def) => (int?)(double?)o["paper_width_mm"] is int w && w > 0 ? w : def;

    void Notify(JsonObject? s, bool reachable, bool online)
    {
        var top = (s?["errors"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var key = top is null ? "" : $"{top["at"]}|{top["message"]}";
        if (!_first && key.Length > 0 && key != _lastErrorKey)
        {
            _lastErrorAt = DateTime.Now;
            _tray.ShowBalloonTip(8000, "Falha na impressão", $"{top!["title"]}: {top["message"]}", ToolTipIcon.Error);
        }
        _lastErrorKey = key;

        foreach (var p in (s?["printers"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (p["kind"]?.ToString() is "fax" or "file") continue;
            var name = p["name"]?.GetValue<string>() ?? ""; var st = p["status"]?.GetValue<string>() ?? "ok";
            if (!_first && _printerStatus.TryGetValue(name, out var old) && old == "ok" && st != "ok")
                _tray.ShowBalloonTip(8000, "Impressora com problema", $"{name}: {st.Replace('_', ' ')}", ToolTipIcon.Warning);
            _printerStatus[name] = st;
        }

        // ilha nova criada no sistema, ainda sem impressora
        foreach (var i in Islands(s))
        {
            var id = i["id"]?.ToString() ?? "";
            if (_islandsSeen.Add(id) && !_first && string.IsNullOrEmpty(i["printer_name"]?.ToString()))
                _tray.ShowBalloonTip(8000, "Nova ilha sem impressora", $"{i["name"]}: escolha a impressora no Monitor ou no sistema.", ToolTipIcon.Warning);
        }

        var nowOnline = reachable && online;
        if (!_first && _wasOnline == true && !nowOnline)
            _tray.ShowBalloonTip(8000, "iControl Print offline", reachable ? "Sem conexão com o iControl Food. Confira a internet." : "O serviço de impressão parou.", ToolTipIcon.Warning);
        _wasOnline = nowOnline;
    }

    void ShowForm()
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new StatusForm(this);
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Bind(Status, _header.Text ?? "");
        _form.Show(); _form.WindowState = FormWindowState.Normal; _form.Activate();
    }

    public void Test(string printer, string mode, int paper, string label)
    {
        var r = PipeClient.Send(new JsonObject { ["cmd"] = "test", ["printer"] = printer, ["mode"] = mode, ["paper"] = paper, ["label"] = label }, 30000);
        if (r?["ok"]?.GetValue<bool>() == true) _tray.ShowBalloonTip(4000, "Teste enviado", $"{label}: {r["how"]}", ToolTipIcon.Info);
        else MessageBox.Show(r?["error"]?.GetValue<string>() ?? "O serviço não respondeu.", "Imprimir teste", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Refresh();
    }

    public void Restart()
    {
        var r = PipeClient.Send(new JsonObject { ["cmd"] = "restart" });
        if (r is null)
        {
            try { Process.Start(new ProcessStartInfo("sc.exe", "start iControlPrint") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }); }
            catch { MessageBox.Show("Não foi possível iniciar o serviço.", "iControl Print", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        }
        _tray.ShowBalloonTip(3000, "iControl Print", "Reiniciando o serviço...", ToolTipIcon.Info);
    }

    public void OpenLogs() { try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.Logs}\"") { UseShellExecute = true }); } catch { } }

    public void OpenPdf()
    {
        try { Directory.CreateDirectory(DriverPrinter.PdfFolder); } catch { }
        var last = Status?["last_file"]?.ToString() ?? "";
        var args = last.Length > 0 && File.Exists(last) ? $"/select,\"{last}\"" : $"\"{DriverPrinter.PdfFolder}\"";
        try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true }); } catch { }
    }

    public void CheckUpdate()
    {
        var r = PipeClient.Send(new JsonObject { ["cmd"] = "check-update" }, 10000);
        var latest = Status?["latest_version"]?.GetValue<string>() ?? "";
        MessageBox.Show(r?["message"]?.GetValue<string>() ?? "O serviço não respondeu.",
            $"Versão instalada {AppInfo.Version}{(latest.Length > 0 ? $" · publicada {latest}" : "")}", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void Pair()
    {
        var code = Prompt.Ask("Parear de novo / Trocar empresa",
            "Em Configurações > PDV > Impressão > Adicionar servidor, gere o código (vale 10 minutos) e digite aqui:");
        if (string.IsNullOrWhiteSpace(code)) return;
        try
        {
            var cfg = AppSettings.Load();
            using var http = new HttpClient { BaseAddress = new Uri(cfg.SupabaseUrl + "/rest/v1/"), Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("apikey", cfg.PublishableKey);
            var res = http.PostAsJsonAsync("rpc/print_pair_preview", new { p_code = code }).GetAwaiter().GetResult();
            var pv = JsonNode.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if (pv?["ok"]?.GetValue<bool>() != true) { MessageBox.Show(pv?["error"]?.GetValue<string>() ?? pv?["message"]?.GetValue<string>() ?? "Código inválido.", "Pareamento", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            var c = pv["company"]!;
            var current = Status?["company"]?.GetValue<string>() ?? "";
            var msg = $"Conectar a {c["trade_name"]} ({c["cnpj"]})?" + (current.Length > 0 ? $"\n\nEste computador deixa de imprimir para {current}." : "");
            if (MessageBox.Show(msg, "Confirmar empresa", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var r = PipeClient.Send(new JsonObject { ["cmd"] = "claim", ["code"] = code }, 20000);
            if (r?["ok"]?.GetValue<bool>() == true) _tray.ShowBalloonTip(5000, "Pareado", $"Conectado a {r["company"]}. O serviço está reiniciando.", ToolTipIcon.Info);
            else MessageBox.Show(r?["error"]?.GetValue<string>() ?? "O serviço não respondeu. Ele está rodando?", "Pareamento", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível validar o código: " + ex.Message, "Pareamento", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>Configurar ilha ou impressora geral (grava no banco; o sistema vê na hora). Exige administrador do Windows ou PIN.</summary>
    public void Configure(JsonObject? island)
    {
        var s = Status;
        var target = island ?? General(s);
        var title = island is null ? "Impressora geral do sistema" : $"Ilha {island["name"]}";
        var pinRequired = s?["config"]?["pin_required"]?.GetValue<bool>() == true;
        using var dlg = new ConfigDialog(title, target, s?["printers"] as JsonArray ?? new JsonArray(), DefaultPaper(s), askPin: true, pinRequired);
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var req = island is null
            ? new JsonObject { ["cmd"] = "general_save", ["fields"] = dlg.Fields(), ["pin"] = dlg.Pin }
            : new JsonObject { ["cmd"] = "island_save", ["island_id"] = island["id"]?.ToString(), ["fields"] = dlg.Fields(), ["pin"] = dlg.Pin };
        var r = PipeClient.Send(req, 20000);
        if (r?["ok"]?.GetValue<bool>() == true) { _tray.ShowBalloonTip(3000, "Salvo", $"{title}: alteração gravada no sistema.", ToolTipIcon.Info); PipeClient.Send(new JsonObject { ["cmd"] = "islands" }, 10000); Refresh(); }
        else MessageBox.Show(r?["error"]?.GetValue<string>() ?? "O serviço não respondeu.", "Não foi possível salvar", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop(); _showTimer.Stop(); _tray.Visible = false; _tray.Dispose(); _form?.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Tela do Monitor: Situação (status e erros) e Ilhas (ilhas + impressora geral).</summary>
sealed class StatusForm : Form
{
    readonly MonitorContext _ctx;
    readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    readonly Label _info = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f) };
    readonly ListView _errors = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill };
    readonly ListView _islands = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill };
    readonly Label _islandsNote = new() { AutoSize = true, ForeColor = Color.DarkGoldenrod, Padding = new Padding(4) };

    public StatusForm(MonitorContext ctx)
    {
        _ctx = ctx;
        Text = "iControl Print Monitor";
        Icon = Icons.Normal;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(780, 560); MinimumSize = new Size(600, 440);
        Font = new Font("Segoe UI", 9.5f);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        // --- Situação
        var tabStatus = new TabPage("Situação") { Padding = new Padding(8) };
        _errors.Columns.Add("Quando", 110); _errors.Columns.Add("Job", 200); _errors.Columns.Add("Erro", 380);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        top.Controls.Add(_title); top.Controls.Add(_info);
        var errBox = new GroupBox { Text = "Últimos erros", Dock = DockStyle.Fill, Padding = new Padding(8) };
        errBox.Controls.Add(_errors);
        tabStatus.Controls.Add(errBox); tabStatus.Controls.Add(top);
        // --- Ilhas
        var tabIslands = new TabPage("Ilhas e impressora geral") { Padding = new Padding(8) };
        foreach (var (h, w) in new[] { ("Ilha", 150), ("Ativa", 50), ("Impressora", 210), ("Modo", 110), ("Papel", 70), ("Vias", 45), ("Alterado por", 140) }) _islands.Columns.Add(h, w);
        _islands.DoubleClick += (_, _) => ConfigureSelected();
        var islandBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        void IB(string t, Action a) { var b = new Button { Text = t, AutoSize = true }; b.Click += (_, _) => a(); islandBtns.Controls.Add(b); }
        IB("Configurar impressora/modo/papel", ConfigureSelected);
        IB("Imprimir teste", TestSelected);
        IB("Atualizar do sistema", () => { PipeClient.Send(new JsonObject { ["cmd"] = "islands" }, 10000); _ctx.Refresh(); });
        var hint = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(4),
            Text = "Criar, renomear, excluir ilhas e escolher produtos é feito no sistema (Configurações > PDV > Impressão)." };
        tabIslands.Controls.Add(_islands); tabIslands.Controls.Add(_islandsNote); tabIslands.Controls.Add(hint); tabIslands.Controls.Add(islandBtns);
        tabs.TabPages.Add(tabStatus); tabs.TabPages.Add(tabIslands);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        void Add(string text, Action a) { var b = new Button { Text = text, AutoSize = true }; b.Click += (_, _) => a(); buttons.Controls.Add(b); }
        Add("Reiniciar serviço", _ctx.Restart);
        Add("Abrir logs", _ctx.OpenLogs);
        Add("Abrir pasta dos PDFs", _ctx.OpenPdf);
        Add("Parear de novo / Trocar empresa", _ctx.Pair);
        Add("Verificar atualização", _ctx.CheckUpdate);

        Controls.Add(tabs); Controls.Add(buttons);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    JsonObject? SelectedTarget(out bool general)
    {
        general = false;
        if (_islands.SelectedItems.Count == 0) { MessageBox.Show("Selecione uma ilha ou a impressora geral.", "iControl Print"); return null; }
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
        if (printer.Length == 0) { MessageBox.Show("Defina a impressora antes de testar.", "iControl Print"); return; }
        _ctx.Test(printer, o["print_mode"]?.ToString() ?? "auto", MonitorContext.Paper(o, MonitorContext.DefaultPaper(_ctx.Status)), general ? "Impressora geral" : $"Ilha {o["name"]}");
    }

    static string ModeLabel(string? m) => m switch { "raw" => "Direto", "driver" => "Driver", _ => "Automático" };

    public void Bind(JsonObject? s, string title)
    {
        if (IsDisposed) return;
        _title.Text = title;
        if (s?["ok"]?.GetValue<bool>() != true) { _info.Text = "O serviço iControl Print não está respondendo. Clique em Reiniciar serviço."; return; }
        string V(string k) => s[k]?.ToString() ?? "";
        var sb = new StringBuilder();
        if (V("cnpj").Length > 0) sb.AppendLine($"Empresa: {V("company")} · CNPJ {V("cnpj")}");
        sb.AppendLine($"Situação: {(s["online"]?.GetValue<bool>() == true ? "online" : "offline")}{(V("role").Length > 0 ? $" · {(V("role") == "principal" ? "servidor principal" : "servidor reserva")}" : "")}");
        sb.AppendLine($"Versão: {V("version")}{(V("latest_version").Length > 0 && V("latest_version") != V("version") ? $" (publicada {V("latest_version")})" : "")}");
        sb.AppendLine($"Fila no sistema: {V("queue")} · erros nas últimas 2 h: {V("server_errors")}");
        sb.AppendLine($"Último sinal: {V("last_heartbeat")}{(V("last_print_ok").Length > 0 ? $" · última impressão: {V("last_print_ok")}" : "")}");
        if (V("last_file").Length > 0) sb.AppendLine($"Último PDF: {V("last_file")}");
        if (V("last_heartbeat_error").Length > 0) sb.AppendLine($"Conexão: {V("last_heartbeat_error")}");
        if (V("update_status").Length > 0) sb.AppendLine($"Atualização: {V("update_status")}");
        sb.Append($"Computador: {V("machine")}");
        _info.Text = sb.ToString();

        _errors.BeginUpdate(); _errors.Items.Clear();
        foreach (var e in (s["errors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            _errors.Items.Add(new ListViewItem(new[] { e["at"]?.ToString() ?? "", e["title"]?.ToString() ?? "", e["message"]?.ToString() ?? "" }));
        _errors.EndUpdate();

        var def = MonitorContext.DefaultPaper(s);
        string PaperText(JsonObject o) { var w = MonitorContext.Paper(o, def); return w >= 200 ? "A4" : $"{w} mm"; }
        var sel = _islands.SelectedItems.Count > 0 ? _islands.SelectedItems[0].Index : -1;
        _islands.BeginUpdate(); _islands.Items.Clear();
        var g = MonitorContext.General(s);
        _islands.Items.Add(new ListViewItem(new[] { "Impressora geral (cupons)", "sim", g["printer_name"]?.ToString() is { Length: > 0 } gp ? gp : "(não definida)", ModeLabel(g["print_mode"]?.ToString()), PaperText(g), g["copies"]?.ToString() ?? "1", g["updated_by"]?.ToString() ?? "" }) { Tag = null, Font = new Font(Font, FontStyle.Bold) });
        int missing = 0;
        foreach (var i in MonitorContext.Islands(s))
        {
            var active = i["active"]?.GetValue<bool>() != false;
            var printer = i["printer_name"]?.ToString() ?? "";
            if (active && printer.Length == 0) missing++;
            var item = new ListViewItem(new[] { i["name"]?.ToString() ?? "", active ? "sim" : "não", printer.Length > 0 ? printer : "Nova ilha sem impressora", ModeLabel(i["print_mode"]?.ToString()), PaperText(i), i["copies"]?.ToString() ?? "1", i["updated_by"]?.ToString() ?? "" }) { Tag = i };
            if (!active) item.ForeColor = Color.Gray; else if (printer.Length == 0) item.ForeColor = Color.DarkOrange;
            _islands.Items.Add(item);
        }
        _islands.EndUpdate();
        if (sel >= 0 && sel < _islands.Items.Count) _islands.Items[sel].Selected = true;
        _islandsNote.Text = missing > 0 ? $"{missing} ilha(s) sem impressora: os pedidos delas dão erro até configurar." : $"Sincronizado com o sistema às {V("config_at")}.";
    }
}

/// <summary>Configurar impressora, modo, papel, colunas e vias (ilha ou impressora geral).</summary>
sealed class ConfigDialog : Form
{
    readonly ComboBox _printer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly ComboBox _paper = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly NumericUpDown _cols = new() { Minimum = 0, Maximum = 120, Width = 80 };
    readonly NumericUpDown _copies = new() { Minimum = 1, Maximum = 5, Width = 80 };
    readonly TextBox _pin = new() { UseSystemPasswordChar = true, Width = 120, MaxLength = 8 };
    readonly Label _warn = new() { AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DarkGoldenrod };
    readonly JsonArray _printers;
    static readonly (string v, string t)[] Modes = [("auto", "Automático"), ("raw", "Direto (ESC/POS)"), ("driver", "Driver do Windows")];
    static readonly (int v, string t)[] Papers = [(0, "Padrão"), (58, "58 mm"), (76, "76 mm"), (80, "80 mm"), (112, "112 mm"), (210, "A4")];
    public string Pin => _pin.Text.Trim();

    public ConfigDialog(string title, JsonObject current, JsonArray printers, int defaultPaper, bool askPin, bool pinRequired)
    {
        _printers = printers;
        Text = title; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f); AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(12);
        _printer.Items.Add("(nenhuma)");
        foreach (var p in printers.OfType<JsonObject>())
        {
            var kind = p["kind"]?.ToString();
            _printer.Items.Add(p["name"]?.ToString() + (kind == "file" ? "  [arquivo PDF/XPS]" : kind == "thermal" ? "  [térmica]" : kind == "fax" ? "  [fax: não suportado]" : ""));
        }
        var cur = current["printer_name"]?.ToString() ?? "";
        var idx = printers.OfType<JsonObject>().Select((p, i) => (p, i)).FirstOrDefault(x => x.p["name"]?.ToString() == cur).i;
        _printer.SelectedIndex = cur.Length > 0 && printers.OfType<JsonObject>().Any(p => p["name"]?.ToString() == cur) ? idx + 1 : 0;
        foreach (var m in Modes) _mode.Items.Add(m.t);
        _mode.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.v == (current["print_mode"]?.ToString() ?? "auto")));
        foreach (var p in Papers) _paper.Items.Add(p.v == 0 ? $"Padrão ({defaultPaper} mm)" : p.t);
        var w = (int?)(double?)current["paper_width_mm"] ?? 0;
        _paper.SelectedIndex = Math.Max(0, Array.FindIndex(Papers, p => p.v == w));
        _cols.Value = Math.Clamp((decimal)((double?)current["chars_per_line"] ?? 0), 0, 120);
        _copies.Value = Math.Clamp((decimal)((double?)current["copies"] ?? 1), 1, 5);

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c) { t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 8, 0) }); t.Controls.Add(c); }
        Row("Impressora", _printer); Row("Modo de impressão", _mode); Row("Papel", _paper); Row("Colunas (0 = auto)", _cols); Row("Vias", _copies);
        if (askPin) Row(pinRequired ? "PIN (ou abra como administrador)" : "PIN", _pin);
        t.Controls.Add(new Label()); t.Controls.Add(_warn);
        if (!pinRequired) _warn.Text = "Sem PIN definido no sistema: salvar exige abrir o Monitor como administrador do Windows.";
        var ok = new Button { Text = "Salvar", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        var bar = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom };
        bar.Controls.Add(cancel); bar.Controls.Add(ok);
        Controls.Add(t); Controls.Add(bar);
        AcceptButton = ok; CancelButton = cancel;
        _mode.SelectedIndexChanged += (_, _) => UpdateWarn(pinRequired);
        _printer.SelectedIndexChanged += (_, _) => UpdateWarn(pinRequired);
        UpdateWarn(pinRequired);
    }

    JsonObject? SelectedPrinter() => _printer.SelectedIndex > 0 ? _printers.OfType<JsonObject>().ElementAt(_printer.SelectedIndex - 1) : null;

    void UpdateWarn(bool pinRequired)
    {
        var p = SelectedPrinter();
        var mode = Modes[_mode.SelectedIndex].v;
        var msgs = new List<string>();
        if (p?["kind"]?.ToString() == "fax") msgs.Add("Fax não é suportado.");
        if (p?["kind"]?.ToString() == "file") msgs.Add($"Impressora de arquivo: salva o PDF/XPS em {DriverPrinter.PdfFolder} (sempre no modo Driver).");
        var driver = mode == "driver" || (mode == "auto" && p is not null && p["auto_mode"]?.ToString() == "driver");
        if (driver) msgs.Add("Modo Driver: guilhotina, gaveta e sinal sonoro ficam desativados.");
        if (!pinRequired) msgs.Add("Sem PIN definido no sistema: salvar exige abrir o Monitor como administrador do Windows.");
        _warn.Text = string.Join("\n", msgs);
    }

    public JsonObject Fields()
    {
        var p = SelectedPrinter();
        return new JsonObject
        {
            ["printer_name"] = p?["name"]?.ToString() ?? "",
            ["print_mode"] = Modes[_mode.SelectedIndex].v,
            ["paper_width_mm"] = Papers[_paper.SelectedIndex].v,
            ["chars_per_line"] = (int)_cols.Value,
            ["copies"] = (int)_copies.Value,
        };
    }
}

/// <summary>Caixa simples para digitar o código.</summary>
static class Prompt
{
    public static string? Ask(string title, string text)
    {
        using var f = new Form { Text = title, Width = 460, Height = 190, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false, Font = new Font("Segoe UI", 9.5f), Icon = Icons.Normal };
        var lbl = new Label { Left = 12, Top = 12, Width = 420, Height = 40, Text = text };
        var box = new TextBox { Left = 12, Top = 58, Width = 200, CharacterCasing = CharacterCasing.Upper, Font = new Font("Consolas", 14) };
        var ok = new Button { Text = "Continuar", Left = 250, Top = 100, Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Left = 345, Top = 100, Width = 90, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { lbl, box, ok, cancel }); f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
    }
}
