using System.Diagnostics;
using Topaz.Agent.Hosting;

namespace Topaz.Agent.Setup.Ui;

/// <summary>
/// The one window of the setup: settings (station name; server, DB and Firebird password under «Дополнительно»),
/// progress, result with the verification code, and the status of an installed agent.
/// </summary>
public sealed class SetupForm : Form
{
    public enum Page { Settings, Progress, Done, Status }

    private static readonly Color Accent = Color.FromArgb(0x16, 0x68, 0xdc);
    private static readonly Color Muted = Color.FromArgb(0x59, 0x59, 0x59);
    private static readonly Color Good = Color.FromArgb(0x38, 0x9e, 0x0d);
    private static readonly Color Warn = Color.FromArgb(0xd4, 0x6b, 0x08);
    private static readonly Color Bad = Color.FromArgb(0xcf, 0x13, 0x22);

    private readonly IMachine _machine;
    private SetupPlan _plan;
    private readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold), ForeColor = Color.White };
    private readonly Label _subtitle = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f), ForeColor = Color.FromArgb(0xdd, 0xe8, 0xff) };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 8), AutoScroll = true };
    private readonly Button _primary = new() { AutoSize = true, MinimumSize = new Size(120, 34) };
    private readonly Button _secondary = new() { AutoSize = true, MinimumSize = new Size(100, 34) };
    private readonly Button _extra = new() { AutoSize = true, MinimumSize = new Size(100, 34), Visible = false };

    // settings page
    private TextBox? _name, _server, _db, _password;
    private Label? _dbStatus, _error;
    private Panel? _advanced;
    private LinkLabel? _advancedToggle;
    // progress page
    private readonly Dictionary<string, (Label Mark, Label Text)> _steps = new();
    private FlowLayoutPanel? _stepList;
    private bool _busy;

    public SetupForm(IMachine machine, SetupPlan plan, Page page)
    {
        _machine = machine;
        _plan = plan;
        Text = "Topaz Center — агент АЗС";
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 500);
        BackColor = Color.White;
        try { Icon = Icon.ExtractAssociatedIcon(SetupApp.CurrentExe); } catch (Exception) { /* default icon */ }

        var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Accent, Padding = new Padding(24, 14, 24, 10) };
        var headerText = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Accent };
        headerText.Controls.AddRange([_title, _subtitle]);
        header.Controls.Add(headerText);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(24, 12, 24, 12), BackColor = Color.FromArgb(0xf5, 0xf6, 0xf8) };
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = footer.BackColor };
        right.Controls.AddRange([_primary, _secondary]);
        _extra.Dock = DockStyle.Left;
        footer.Controls.AddRange([right, _extra]);
        _primary.BackColor = Accent;
        _primary.ForeColor = Color.White;
        _primary.FlatStyle = FlatStyle.Flat;
        _primary.FlatAppearance.BorderSize = 0;
        _primary.Font = new Font(Font, FontStyle.Bold);

        Controls.AddRange([_body, footer, header]);
        AcceptButton = _primary;
        CancelButton = _secondary;
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };

        switch (page)
        {
            case Page.Status: ShowStatus(); break;
            default: ShowSettings(null); break;
        }
    }

    // ------------------------------------------------------------------ pages

    private void ShowSettings(string? error)
    {
        Reset(_plan.IsUpdate ? "Обновление агента Topaz Center" : "Установка агента Topaz Center",
            $"Версия {AgentInfo.Version}. Агент читает базу Топаза (только чтение) и передаёт данные на сервер.");
        var col = Column();

        col.Controls.Add(Caption("Название АЗС"));
        _name = new TextBox { Text = _plan.StationName, Width = 540, Font = new Font("Segoe UI", 11f), MaxLength = 100, PlaceholderText = "например, АЗС-5 Северная" };
        col.Controls.Add(_name);
        col.Controls.Add(Hint("Под этим названием АЗС появится на сервере. Его можно будет поправить при подключении."));

        col.Controls.Add(Caption("База Топаза", top: 14));
        var dbRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _dbStatus = new Label { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 6, 8, 0) };
        var pick = new LinkLabel { Text = "Выбрать…", AutoSize = true, Margin = new Padding(0, 6, 0, 0), LinkColor = Accent };
        pick.LinkClicked += (_, _) => PickDatabase();
        dbRow.Controls.AddRange([_dbStatus, pick]);
        col.Controls.Add(dbRow);

        _advancedToggle = new LinkLabel { Text = "Дополнительно ▸", AutoSize = true, Margin = new Padding(0, 16, 0, 4), LinkColor = Accent };
        col.Controls.Add(_advancedToggle);
        _advanced = new Panel { AutoSize = true, Visible = false, Margin = new Padding(0) };
        var adv = Column();
        adv.Dock = DockStyle.None;
        adv.Controls.Add(Caption("Сервер Topaz Center"));
        _server = new TextBox { Text = _plan.ServerUrl, Width = 540 };
        adv.Controls.Add(_server);
        adv.Controls.Add(Caption("Путь к базе Топаза (TopazAZS.fdb)", top: 10));
        _db = new TextBox { Text = _plan.DatabasePath ?? "", Width = 540 };
        _db.TextChanged += (_, _) => UpdateDbStatus(_db.Text);
        adv.Controls.Add(_db);
        adv.Controls.Add(Caption("Пароль Firebird", top: 10));
        _password = new TextBox { UseSystemPasswordChar = true, Width = 260, PlaceholderText = _plan.IsUpdate ? "как сейчас" : "стандартный пароль Топаза" };
        adv.Controls.Add(_password);
        _advanced.Controls.Add(adv);
        col.Controls.Add(_advanced);
        _advancedToggle.LinkClicked += (_, _) => ToggleAdvanced(!_advanced.Visible);

        _error = new Label { AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = Bad, Margin = new Padding(0, 12, 0, 0), Text = error ?? "" };
        col.Controls.Add(_error);
        _body.Controls.Add(col);
        UpdateDbStatus(_plan.DatabasePath);

        Buttons(_plan.IsUpdate ? "Обновить" : "Установить", () => _ = InstallAsync(), "Отмена", Close);
        ActiveControl = _name;
    }

    private void ShowProgress()
    {
        Reset("Установка…", "Это займёт до минуты. Не закрывайте окно.");
        var col = Column();
        _stepList = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        _steps.Clear();
        col.Controls.Add(_stepList);
        col.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Width = 540, Height = 6, Margin = new Padding(0, 16, 0, 0) });
        _body.Controls.Add(col);
        Buttons(null, null, null, null);
    }

    private void Step(StepReport s)
    {
        if (_stepList is null) return;
        if (!_steps.TryGetValue(s.Key, out var row))
        {
            var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            row = (new Label { AutoSize = true, Width = 22, Font = new Font("Segoe UI Symbol", 11f), Margin = new Padding(0, 0, 6, 0) },
                   new Label { AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, 2, 0, 0) });
            line.Controls.AddRange([row.Mark, row.Text]);
            _stepList.Controls.Add(line);
            _steps[s.Key] = row;
        }
        (row.Mark.Text, row.Mark.ForeColor) = s.State switch
        {
            StepState.Done => ("✔", Good),
            StepState.Warning => ("⚠", Warn),
            StepState.Failed => ("✖", Bad),
            _ => ("…", Muted),
        };
        row.Text.Text = s.Title + (s.Detail is null ? "" : "\n" + s.Detail);
    }

    private void ShowDone(SetupResult r)
    {
        var connected = r.Connection?.Connected == true;
        Reset(r.Outcome == SetupOutcome.InstalledWithWarnings ? "Агент установлен, есть замечания" : _plan.IsUpdate ? "Агент обновлён" : "Агент установлен и работает",
            $"Служба «Topaz Center: агент АЗС» запущена. Версия {AgentInfo.Version}.");
        var col = Column();
        if (connected)
        {
            col.Controls.Add(Big($"АЗС «{r.Connection!.StationName}» подключена к серверу.", Good));
            col.Controls.Add(Hint("Данные передаются. Больше ничего делать не нужно."));
        }
        else if (r.Connection?.Code is { } code)
        {
            AddCode(col, code);
        }
        else
        {
            col.Controls.Add(Big("Заявка на подключение уйдёт, как только появится связь с сервером.", Warn));
            col.Controls.Add(Hint("Код подтверждения можно будет посмотреть, запустив агент из меню «Пуск» → «Topaz Center — агент АЗС». Данные тем временем копятся на этом компьютере."));
        }
        if (r.Warnings.Count > 0)
        {
            col.Controls.Add(Caption("Замечания", top: 16));
            foreach (var w in r.Warnings) col.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = Warn, Text = "• " + w, Margin = new Padding(0, 2, 0, 0) });
        }
        _body.Controls.Add(col);
        Buttons("Готово", Close, null, null);
    }

    private void ShowStatus()
    {
        var state = SetupApp.ReadState(_plan);
        var running = _machine.IsServiceRunning(SetupPlan.ServiceName);
        Reset("Агент Topaz Center", $"Версия {AgentInfo.Version}. Служба {(running ? "работает" : "остановлена")}.");
        var col = Column();
        col.Controls.Add(Caption("АЗС"));
        col.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Text = string.IsNullOrEmpty(state.StationName) ? "—" : state.StationName });
        if (state.Connected) col.Controls.Add(Big("Подключена к серверу, данные передаются.", Good));
        else if (state.Code is { } code) AddCode(col, code);
        else col.Controls.Add(Big("Не подключена: заявка ещё не отправлена (нет связи с сервером).", Warn));
        if (!running) col.Controls.Add(new Label { AutoSize = true, ForeColor = Bad, Text = "Служба остановлена — данные не собираются. Нажмите «Изменить настройки» → «Обновить», чтобы перезапустить.", MaximumSize = new Size(540, 0), Margin = new Padding(0, 8, 0, 0) });
        col.Controls.Add(Caption("Сервер", top: 14));
        col.Controls.Add(Hint(state.ServerUrl ?? "—"));
        col.Controls.Add(Caption("База Топаза", top: 8));
        col.Controls.Add(Hint(state.Database ?? "—"));
        var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        var logs = new LinkLabel { Text = "Открыть журнал", AutoSize = true, LinkColor = Accent };
        logs.LinkClicked += (_, _) => Open(Path.Combine(_plan.StateDir, "logs"));
        links.Controls.Add(logs);
        col.Controls.Add(links);
        _body.Controls.Add(col);
        Buttons("Закрыть", Close, "Изменить настройки", () => ShowSettings(null));
        _extra.Text = "Удалить агент…";
        _extra.Visible = true;
        _extra.Click -= Uninstall;
        _extra.Click += Uninstall;
    }

    // ------------------------------------------------------------------ actions

    private async Task InstallAsync()
    {
        _plan.StationName = _name!.Text.Trim();
        _plan.ServerUrl = SetupPlan.NormalizeUrl(_server!.Text);
        _plan.DatabasePath = _db!.Text.Trim().Trim('"');
        _plan.FirebirdPassword = string.IsNullOrEmpty(_password!.Text) ? _plan.FirebirdPassword : _password.Text;
        if (_plan.Validate().ToList() is { Count: > 0 } problems)
        {
            _error!.Text = string.Join("\n", problems);
            if (problems.Any(p => p.Contains("сервер", StringComparison.OrdinalIgnoreCase))) ToggleAdvanced(true);
            return;
        }

        _busy = true;
        ShowProgress();
        SetupResult result;
        try
        {
            var progress = new Progress<StepReport>(Step);
            var plan = _plan;
            result = await Task.Run(() => new Installer(_machine, new SetupProbe(), SetupApp.Log).RunAsync(plan, progress, CancellationToken.None));
        }
        catch (Exception ex)
        {
            SetupApp.Log("Setup crashed: " + ex);
            result = new SetupResult(SetupOutcome.Failed, null, [], ex.Message);
        }
        finally { _busy = false; }

        switch (result.Outcome)
        {
            case SetupOutcome.NeedsPassword:
                ShowSettings(result.Error);
                ToggleAdvanced(true);
                ActiveControl = _password;
                break;
            case SetupOutcome.Failed:
                ShowSettings("Установка не выполнена: " + result.Error + $"\nПодробности: {SetupApp.LogPath}");
                break;
            default:
                ShowDone(result);
                break;
        }
    }

    private void Uninstall(object? sender, EventArgs e)
    {
        Hide();
        if (SetupApp.Uninstall([]) == 0) Close(); // removal continues in a separate process
        else Show();
    }

    private void PickDatabase()
    {
        using var dlg = new OpenFileDialog { Title = "База Топаза", Filter = "База Топаза (*.fdb)|*.fdb|Все файлы (*.*)|*.*", CheckFileExists = true };
        var current = _db?.Text;
        if (!string.IsNullOrEmpty(current) && Path.GetDirectoryName(current) is { } d && Directory.Exists(d)) dlg.InitialDirectory = d;
        if (dlg.ShowDialog(this) == DialogResult.OK && _db is not null) _db.Text = dlg.FileName;
    }

    private void UpdateDbStatus(string? path)
    {
        if (_dbStatus is null) return;
        if (string.IsNullOrWhiteSpace(path)) (_dbStatus.Text, _dbStatus.ForeColor) = ("⚠ не найдена в стандартной папке — укажите файл TopazAZS.fdb", Warn);
        else if (!_plan.LocalFirebird || !Path.IsPathFullyQualified(path)) (_dbStatus.Text, _dbStatus.ForeColor) = ("• " + path, Muted);
        else if (File.Exists(path)) (_dbStatus.Text, _dbStatus.ForeColor) = ("✔ " + path, Good);
        else (_dbStatus.Text, _dbStatus.ForeColor) = ("⚠ файл не найден: " + path, Warn);
    }

    private void ToggleAdvanced(bool show)
    {
        if (_advanced is null || _advancedToggle is null) return;
        _advanced.Visible = show;
        _advancedToggle.Text = show ? "Дополнительно ▾" : "Дополнительно ▸";
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false }); } catch (Exception) { /* nothing to do */ }
    }

    // ------------------------------------------------------------------ helpers

    private void AddCode(Control col, string code)
    {
        col.Controls.Add(Big("АЗС ожидает подключения на сервере.", Accent));
        col.Controls.Add(Caption("Код подтверждения", top: 12));
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
        var label = new Label { AutoSize = true, Text = code, Font = new Font("Consolas", 30f, FontStyle.Bold), ForeColor = Color.FromArgb(0x1f, 0x1f, 0x1f) };
        var copy = new Button { Text = "Скопировать", AutoSize = true, MinimumSize = new Size(110, 32), Margin = new Padding(16, 12, 0, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(code); copy.Text = "Скопировано"; } catch (Exception) { /* clipboard busy */ } };
        row.Controls.AddRange([label, copy]);
        col.Controls.Add(row);
        col.Controls.Add(Hint("Сообщите этот код администратору сервера Topaz Center (по телефону или в мессенджере). Он сверит код и подключит АЗС. До подключения данные копятся на этом компьютере и потом будут переданы полностью."));
    }

    private void Reset(string title, string subtitle)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
        _body.Controls.Clear();
        _extra.Visible = false;
    }

    private void Buttons(string? primary, Action? onPrimary, string? secondary, Action? onSecondary)
    {
        Rebind(_primary, primary, onPrimary);
        Rebind(_secondary, secondary, onSecondary);
    }

    private readonly Dictionary<Button, EventHandler> _handlers = new();

    private void Rebind(Button b, string? text, Action? action)
    {
        if (_handlers.Remove(b, out var old)) b.Click -= old;
        b.Visible = text is not null;
        b.Enabled = text is not null;
        if (text is null || action is null) return;
        b.Text = text;
        EventHandler h = (_, _) => action();
        b.Click += h;
        _handlers[b] = h;
    }

    private static FlowLayoutPanel Column() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
    private static Label Caption(string text, int top = 0) => new() { AutoSize = true, Text = text, Font = new Font("Segoe UI", 9.75f, FontStyle.Bold), Margin = new Padding(0, top, 0, 4) };
    private static Label Hint(string text) => new() { AutoSize = true, MaximumSize = new Size(540, 0), Text = text, ForeColor = Muted, Margin = new Padding(0, 2, 0, 0) };
    private static Label Big(string text, Color color) => new() { AutoSize = true, MaximumSize = new Size(540, 0), Text = text, ForeColor = color, Font = new Font("Segoe UI", 11.5f, FontStyle.Bold), Margin = new Padding(0, 4, 0, 4) };

    // ------------------------------------------------------------------ preview (CI screenshots)

    public static IEnumerable<(string Name, Page Page, SetupPlan Plan)> PreviewPages()
    {
        SetupPlan Sample(bool update, string? db) => new()
        {
            StationName = update ? "АЗС Старт" : "", DatabasePath = db, IsUpdate = update,
            InstallDir = @"C:\Program Files\TopazAgent", StateDir = Path.Combine(Path.GetTempPath(), "topaz-preview-state"), SourceExe = SetupApp.CurrentExe,
        };
        yield return ("1-settings", Page.Settings, Sample(false, @"C:\Program Files (x86)\TopazAZS-2\TopazAZS.fdb"));
        yield return ("2-settings-not-found-advanced", Page.Settings, Sample(false, null));
        yield return ("3-wrong-password", Page.Settings, Sample(true, @"C:\Program Files (x86)\TopazAZS-2\TopazAZS.fdb"));
        yield return ("4-progress", Page.Settings, Sample(false, @"C:\Program Files (x86)\TopazAZS-2\TopazAZS.fdb"));
        yield return ("5-done-code", Page.Settings, Sample(false, @"C:\Program Files (x86)\TopazAZS-2\TopazAZS.fdb"));
        yield return ("6-done-connected", Page.Settings, Sample(true, @"C:\Program Files (x86)\TopazAZS-2\TopazAZS.fdb"));
        yield return ("7-done-offline", Page.Settings, Sample(false, @"C:\Program Files (x86)\TopazAZS-2\TopazAZS.fdb"));
        yield return ("8-status", Page.Status, Sample(true, @"C:\Program Files (x86)\TopazAZS-2\TopazAZS.fdb"));
    }

    public void ApplyPreview(string name)
    {
        switch (name)
        {
            case "1-settings":
                _name!.Text = "АЗС-5 Северная";
                break;
            case "2-settings-not-found-advanced":
                ToggleAdvanced(true);
                _error!.Text = "Не найдена база Топаза: укажите путь к TopazAZS.fdb.";
                break;
            case "3-wrong-password":
                ShowSettings("Firebird не принял пароль. Укажите пароль Firebird (в «Дополнительно»).");
                ToggleAdvanced(true);
                break;
            case "4-progress":
                ShowProgress();
                Step(new StepReport("db", "Проверка базы Топаза", StepState.Done, "Firebird 2.5.9, последняя смена №82"));
                Step(new StepReport("files", "Копирование файлов", StepState.Done, @"C:\Program Files\TopazAgent"));
                Step(new StepReport("service", "Настройка службы", StepState.Done));
                Step(new StepReport("server", "Подключение к серверу", StepState.Running));
                break;
            case "5-done-code":
                ShowDone(new SetupResult(SetupOutcome.Installed, new SetupConnection(true, false, null, "K7Q4-9MXD", null), []));
                break;
            case "6-done-connected":
                ShowDone(new SetupResult(SetupOutcome.Installed, new SetupConnection(true, true, "АЗС Старт", null, null), []));
                break;
            case "7-done-offline":
                ShowDone(new SetupResult(SetupOutcome.InstalledWithWarnings, new SetupConnection(false, false, null, null, "сервер недоступен"),
                    ["сервер https://topaz.08u.ru недоступен (No such host is known); агент подключится, когда появится связь"]));
                break;
        }
    }
}
