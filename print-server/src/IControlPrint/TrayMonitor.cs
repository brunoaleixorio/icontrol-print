using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace IControlPrint;

/// <summary>
/// iControl Print Monitor: ícone na bandeja para o usuário logado. Mostra a situação do serviço,
/// imprime teste, reinicia o serviço, abre os logs, pareia de novo e avisa (notificação do Windows) quando algo falha.
/// </summary>
public static class TrayMonitor
{
    public static void Run(bool show)
    {
        using var single = new Mutex(true, @"Local\iControlPrintMonitor", out var first);
        if (!first) { if (show) PipeClient.SignalShow(); return; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        using var ctx = new MonitorContext(show);
        Application.Run(ctx);
    }
}

/// <summary>Conversa com o serviço pelo pipe local.</summary>
static class PipeClient
{
    public static JsonObject? Send(JsonObject req, int timeoutMs = 2500)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            using var w = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var r = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            w.WriteLine(req.ToJsonString());
            var line = r.ReadLine();
            return line is null ? null : JsonNode.Parse(line) as JsonObject;
        }
        catch { return null; }
    }

    // segunda instância com --show: pede para a primeira abrir a tela (evento nomeado)
    public static void SignalShow() { try { using var e = EventWaitHandle.OpenExisting(@"Local\iControlPrintMonitorShow"); e.Set(); } catch { } }
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
    JsonObject? _status;
    string _lastErrorKey = "";
    bool? _wasOnline;
    readonly Dictionary<string, string> _printerStatus = new();
    bool _first = true;

    public MonitorContext(bool show)
    {
        _menu.Items.Add(_header);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Abrir status", null, (_, _) => ShowForm());
        _menu.Items.Add(_testMenu);
        _menu.Items.Add("Reiniciar serviço", null, (_, _) => Restart());
        _menu.Items.Add("Abrir logs", null, (_, _) => OpenLogs());
        _menu.Items.Add("Parear de novo / Trocar empresa", null, (_, _) => Pair());
        _menu.Items.Add("Verificar atualização", null, (_, _) => CheckUpdate());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Fechar o Monitor", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = _menu;
        _tray.Text = "iControl Print";
        _tray.Icon = Icons.Make(Color.Gray);
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowForm();
        _tray.BalloonTipClicked += (_, _) => ShowForm();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        // segunda instância pediu para mostrar a tela (ex.: instalador ao terminar)
        _showTimer.Tick += (_, _) => { if (_showEvent.WaitOne(0)) ShowForm(); };
        _showTimer.Start();
        Refresh();
        if (show) ShowForm();
    }

    void Refresh()
    {
        var s = PipeClient.Send(new JsonObject { ["cmd"] = "status" });
        _status = s;
        bool reachable = s?["ok"]?.GetValue<bool>() == true;
        bool online = reachable && s!["online"]?.GetValue<bool>() == true;
        string company = s?["company"]?.GetValue<string>() ?? "";

        string title;
        if (!reachable) { title = "Serviço parado"; _tray.Icon = Icons.Make(Color.Firebrick); }
        else if (s!["paired"]?.GetValue<bool>() != true) { title = "Não pareado"; _tray.Icon = Icons.Make(Color.Orange); }
        else if (s["revoked"]?.GetValue<bool>() == true) { title = "Revogado: pareie de novo"; _tray.Icon = Icons.Make(Color.Firebrick); }
        else if (online) { title = $"Conectado a {company}"; _tray.Icon = Icons.Make(Color.SeaGreen); }
        else { title = $"Offline ({company})"; _tray.Icon = Icons.Make(Color.Firebrick); }
        _header.Text = title;
        var tip = $"iControl Print · {title}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;

        // impressoras para o teste
        _testMenu.DropDownItems.Clear();
        foreach (var p in (s?["printers"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var name = p["name"]?.GetValue<string>() ?? "";
            var compatible = p["compatible"]?.GetValue<bool>() != false;
            var item = new ToolStripMenuItem(compatible ? name : $"{name} (incompatível)") { Enabled = compatible && reachable };
            item.Click += (_, _) => Test(name);
            _testMenu.DropDownItems.Add(item);
        }
        if (_testMenu.DropDownItems.Count == 0) _testMenu.DropDownItems.Add(new ToolStripMenuItem("Nenhuma impressora") { Enabled = false });

        Notify(s, reachable, online);
        _form?.Bind(s, title);
        _first = false;
    }

    // notificação do Windows: job com erro, impressora offline, serviço caiu
    void Notify(JsonObject? s, bool reachable, bool online)
    {
        var top = (s?["errors"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var key = top is null ? "" : $"{top["at"]}|{top["message"]}";
        if (!_first && key.Length > 0 && key != _lastErrorKey)
            _tray.ShowBalloonTip(8000, "Falha na impressão", $"{top!["title"]}: {top["message"]}", ToolTipIcon.Error);
        _lastErrorKey = key;

        foreach (var p in (s?["printers"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (p["compatible"]?.GetValue<bool>() == false) continue;
            var name = p["name"]?.GetValue<string>() ?? ""; var st = p["status"]?.GetValue<string>() ?? "ok";
            if (!_first && _printerStatus.TryGetValue(name, out var old) && old == "ok" && st != "ok")
                _tray.ShowBalloonTip(8000, "Impressora com problema", $"{name}: {st.Replace('_', ' ')}", ToolTipIcon.Warning);
            _printerStatus[name] = st;
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
        _form.Bind(_status, _header.Text ?? "");
        _form.Show(); _form.WindowState = FormWindowState.Normal; _form.Activate();
    }

    public IEnumerable<string> CompatiblePrinters() =>
        (_status?["printers"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(p => p["compatible"]?.GetValue<bool>() != false).Select(p => p["name"]?.GetValue<string>() ?? "");

    public void Test(string printer)
    {
        var r = PipeClient.Send(new JsonObject { ["cmd"] = "test", ["printer"] = printer }, 15000);
        if (r?["ok"]?.GetValue<bool>() == true) _tray.ShowBalloonTip(4000, "Teste enviado", printer, ToolTipIcon.Info);
        else MessageBox.Show(r?["error"]?.GetValue<string>() ?? "O serviço não respondeu.", "Imprimir teste", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Refresh();
    }

    public void Restart()
    {
        var r = PipeClient.Send(new JsonObject { ["cmd"] = "restart" });
        if (r is null)
        {
            // serviço parado: iniciar exige administrador
            try { Process.Start(new ProcessStartInfo("sc.exe", "start iControlPrint") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }); }
            catch { MessageBox.Show("Não foi possível iniciar o serviço.", "iControl Print", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        }
        _tray.ShowBalloonTip(3000, "iControl Print", "Reiniciando o serviço...", ToolTipIcon.Info);
    }

    public void OpenLogs()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.Logs}\"") { UseShellExecute = true }); } catch { }
    }

    public void CheckUpdate()
    {
        var r = PipeClient.Send(new JsonObject { ["cmd"] = "check-update" }, 10000);
        var latest = _status?["latest_version"]?.GetValue<string>() ?? "";
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
            // mostra a empresa do código antes de trocar (não consome o código)
            var cfg = AppSettings.Load();
            using var http = new HttpClient { BaseAddress = new Uri(cfg.SupabaseUrl + "/rest/v1/"), Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("apikey", cfg.PublishableKey);
            var res = http.PostAsJsonAsync("rpc/print_pair_preview", new { p_code = code }).GetAwaiter().GetResult();
            var pv = JsonNode.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if (pv?["ok"]?.GetValue<bool>() != true) { MessageBox.Show(pv?["error"]?.GetValue<string>() ?? pv?["message"]?.GetValue<string>() ?? "Código inválido.", "Pareamento", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            var c = pv["company"]!;
            var current = _status?["company"]?.GetValue<string>() ?? "";
            var msg = $"Conectar a {c["trade_name"]} ({c["cnpj"]})?" + (current.Length > 0 ? $"\n\nEste computador deixa de imprimir para {current}." : "");
            if (MessageBox.Show(msg, "Confirmar empresa", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var r = PipeClient.Send(new JsonObject { ["cmd"] = "claim", ["code"] = code }, 20000);
            if (r?["ok"]?.GetValue<bool>() == true) _tray.ShowBalloonTip(5000, "Pareado", $"Conectado a {r["company"]}. O serviço está reiniciando.", ToolTipIcon.Info);
            else MessageBox.Show(r?["error"]?.GetValue<string>() ?? "O serviço não respondeu. Ele está rodando?", "Pareamento", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível validar o código: " + ex.Message, "Pareamento", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop(); _showTimer.Stop(); _tray.Visible = false; _tray.Dispose(); _form?.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Tela de status do Monitor.</summary>
sealed class StatusForm : Form
{
    readonly MonitorContext _ctx;
    readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 13, FontStyle.Bold) };
    readonly Label _info = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f) };
    readonly ListView _errors = new() { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill };
    readonly ComboBox _printers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };

    public StatusForm(MonitorContext ctx)
    {
        _ctx = ctx;
        Text = "iControl Print Monitor";
        Icon = Icons.Make(Color.SeaGreen);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(640, 520); MinimumSize = new Size(520, 420);
        Font = new Font("Segoe UI", 9.5f);

        _errors.Columns.Add("Quando", 110); _errors.Columns.Add("Job", 170); _errors.Columns.Add("Erro", 320);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
        top.Controls.Add(_title); top.Controls.Add(_info);

        var test = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var btnTest = new Button { Text = "Imprimir teste", AutoSize = true };
        btnTest.Click += (_, _) => { if (_printers.SelectedItem is string p) _ctx.Test(p); };
        test.Controls.Add(_printers); test.Controls.Add(btnTest);
        top.Controls.Add(test);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        void Add(string text, Action a) { var b = new Button { Text = text, AutoSize = true }; b.Click += (_, _) => a(); buttons.Controls.Add(b); }
        Add("Reiniciar serviço", _ctx.Restart);
        Add("Abrir logs", _ctx.OpenLogs);
        Add("Parear de novo / Trocar empresa", _ctx.Pair);
        Add("Verificar atualização", _ctx.CheckUpdate);

        var errBox = new GroupBox { Text = "Últimos erros", Dock = DockStyle.Fill, Padding = new Padding(8) };
        errBox.Controls.Add(_errors);

        Controls.Add(errBox); Controls.Add(buttons); Controls.Add(top);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void Bind(JsonObject? s, string title)
    {
        if (IsDisposed) return;
        _title.Text = title;
        if (s?["ok"]?.GetValue<bool>() != true)
        {
            _info.Text = "O serviço iControl Print não está respondendo. Clique em Reiniciar serviço.";
            return;
        }
        string V(string k) => s[k]?.ToString() ?? "";
        var sb = new StringBuilder();
        if (V("cnpj").Length > 0) sb.AppendLine($"Empresa: {V("company")} · CNPJ {V("cnpj")}");
        sb.AppendLine($"Situação: {(s["online"]?.GetValue<bool>() == true ? "online" : "offline")}{(V("role").Length > 0 ? $" · {(V("role") == "principal" ? "servidor principal" : "servidor reserva")}" : "")}");
        sb.AppendLine($"Versão: {V("version")}{(V("latest_version").Length > 0 && V("latest_version") != V("version") ? $" (publicada {V("latest_version")})" : "")}");
        sb.AppendLine($"Fila no sistema: {V("queue")} · erros nas últimas 2 h: {V("server_errors")}");
        sb.AppendLine($"Último sinal: {V("last_heartbeat")}{(V("last_print_ok").Length > 0 ? $" · última impressão: {V("last_print_ok")}" : "")}");
        if (V("last_heartbeat_error").Length > 0) sb.AppendLine($"Conexão: {V("last_heartbeat_error")}");
        if (V("update_status").Length > 0) sb.AppendLine($"Atualização: {V("update_status")}");
        sb.Append($"Computador: {V("machine")}");
        _info.Text = sb.ToString();

        var sel = _printers.SelectedItem as string;
        _printers.Items.Clear();
        foreach (var p in _ctx.CompatiblePrinters()) _printers.Items.Add(p);
        if (sel is not null && _printers.Items.Contains(sel)) _printers.SelectedItem = sel;
        else if (_printers.Items.Count > 0) _printers.SelectedIndex = 0;

        _errors.BeginUpdate();
        _errors.Items.Clear();
        foreach (var e in (s["errors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            _errors.Items.Add(new ListViewItem(new[] { e["at"]?.ToString() ?? "", e["title"]?.ToString() ?? "", e["message"]?.ToString() ?? "" }));
        _errors.EndUpdate();
    }
}

/// <summary>Caixa simples para digitar o código.</summary>
static class Prompt
{
    public static string? Ask(string title, string text)
    {
        using var f = new Form { Text = title, Width = 460, Height = 190, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false, Font = new Font("Segoe UI", 9.5f) };
        var lbl = new Label { Left = 12, Top = 12, Width = 420, Height = 40, Text = text };
        var box = new TextBox { Left = 12, Top = 58, Width = 200, CharacterCasing = CharacterCasing.Upper, Font = new Font("Consolas", 14) };
        var ok = new Button { Text = "Continuar", Left = 250, Top = 100, Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Left = 345, Top = 100, Width = 90, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { lbl, box, ok, cancel }); f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
    }
}

/// <summary>Ícone da bandeja desenhado na hora (bolinha colorida com impressora).</summary>
static class Icons
{
    static readonly Dictionary<Color, Icon> Cache = new();
    public static Icon Make(Color c)
    {
        if (Cache.TryGetValue(c, out var i)) return i;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Color.FromArgb(145, 0, 197));
            g.FillRectangle(b, new Rectangle(2, 6, 26, 18));
            g.FillRectangle(Brushes.White, 8, 2, 14, 8);
            g.FillRectangle(Brushes.White, 8, 18, 14, 10);
            using var dot = new SolidBrush(c);
            g.FillEllipse(dot, 18, 18, 13, 13);
            g.DrawEllipse(Pens.White, 18, 18, 13, 13);
        }
        var icon = Icon.FromHandle(bmp.GetHicon());
        Cache[c] = icon;
        return icon;
    }
}
