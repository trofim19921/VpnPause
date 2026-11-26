using VpnPause.Core;

namespace VpnPause.App;

sealed class MainForm : Form
{
    readonly AppController c;
    readonly TrayContext tray;
    readonly ToolTip tip = new() { AutoPopDelay = 15000, InitialDelay = 350, ShowAlways = true };

    readonly Label dot = new() { AutoSize = false, Size = new Size(18, 18), Margin = new Padding(0, 8, 8, 0) };
    readonly Label statusText = new() { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Margin = new Padding(0, 4, 0, 0) };
    readonly Label statusDetails = new() { AutoSize = true, ForeColor = Color.DimGray };
    readonly Label statusWarn = new() { AutoSize = true, ForeColor = Color.FromArgb(170, 90, 0), MaximumSize = new Size(820, 0) };
    readonly Button btnToggle = new() { AutoSize = true };
    readonly Button btnRelease = new() { AutoSize = true };
    readonly Button btnWizard = new() { AutoSize = true };
    readonly Button btnService = new() { AutoSize = true, Visible = false };

    readonly ListView apps = new() { View = View.Details, FullRowSelect = true, GridLines = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
    readonly ComboBox adapterBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    readonly CheckBox chkStrict = new() { AutoSize = true };
    readonly CheckBox chkLan = new() { AutoSize = true };
    readonly CheckBox chkNotify = new() { AutoSize = true };
    readonly CheckBox chkAutostart = new() { AutoSize = true };
    readonly CheckBox chkFreeze = new() { AutoSize = true };
    readonly CheckBox chkFreezeWindows = new() { AutoSize = true };
    readonly NumericUpDown numDelay = new() { Minimum = 0, Maximum = 60, Width = 70 };
    readonly NumericUpDown numMax = new() { Minimum = 10, Maximum = 3600, Width = 90, Increment = 10 };
    readonly ComboBox langBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    readonly TextBox exclusions = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 520, Height = 70 };
    readonly Label savedNote = new() { AutoSize = true, ForeColor = Color.SeaGreen };
    readonly ComboBox logSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly ListBox logBox = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), HorizontalScrollbar = true, IntegralHeight = false };
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer slowTimer = new() { Interval = 3000 };
    readonly ProcessScanner.Cache processCache = new();

    public MainForm(AppController controller, TrayContext tray)
    {
        c = controller;
        this.tray = tray;
        Text = L.T("app.title");
        Icon = Icons.App;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(720, 520);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var head = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Top };
        var headText = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        headText.Controls.AddRange(new Control[] { statusText, statusDetails, statusWarn });
        head.Controls.AddRange(new Control[] { dot, headText });
        root.Controls.Add(head);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 6) };
        buttons.Controls.AddRange(new Control[] { btnToggle, btnRelease, btnWizard, btnService });
        root.Controls.Add(buttons);
        root.Controls.Add(tabs);

        btnToggle.Click += async (_, _) =>
        {
            var r = await c.SetEnabledAsync(!c.Config.Enabled);
            if (!r.Ok) ShowFailure(this, r);
        };
        btnRelease.Click += async (_, _) => await c.ReleaseAllAsync();
        btnWizard.Click += (_, _) => tray.ShowWizard();
        btnService.Click += (_, _) => NativeUi.StartServiceElevated();
        btnRelease.Text = L.T("btn.release");
        btnWizard.Text = L.T("btn.wizard");
        btnService.Text = L.T("btn.startService");
        tip.SetToolTip(btnRelease, L.T("tip.release"));

        BuildAppsTab();
        BuildSettingsTab();
        BuildLogTab();

        slowTimer.Tick += (_, _) =>
        {
            if (!Visible) return;
            if (tabs.SelectedIndex == 0) UpdateAppStates();
            else if (tabs.SelectedIndex == 2) LoadLog();
        };
        slowTimer.Start();
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedIndex == 2) LoadLog();
        };

        LoadApps();
        LoadSettings();
        RefreshStatus();
        if (c.LegacyNote != null) statusWarn.Text = c.LegacyNote;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            slowTimer.Dispose();
            tip.Dispose();
        }
        base.Dispose(disposing);
    }

    public void RefreshStatus()
    {
        if (IsDisposed) return;
        var s = c.Status;
        string warn = "";
        btnService.Visible = !c.ServiceOk;
        if (!c.ServiceOk || s == null)
        {
            dot.BackColor = Color.Gray;
            statusText.Text = L.T("status.service");
            statusDetails.Text = c.ServiceError ?? "";
        }
        else
        {
            if (!s.Enabled)
            {
                dot.BackColor = Color.Gray;
                statusText.Text = L.T("status.off");
            }
            else if (!c.Config.AutoAdapter && c.Config.Adapter == null)
            {
                dot.BackColor = Color.FromArgb(210, 70, 60);
                statusText.Text = L.T("status.noadapter");
            }
            else if (s.VpnUp)
            {
                dot.BackColor = Color.FromArgb(46, 160, 67);
                statusText.Text = L.T("status.on.up");
            }
            else
            {
                dot.BackColor = c.Freezer.HeldCount > 0 ? Color.FromArgb(230, 150, 30) : Color.FromArgb(210, 70, 60);
                statusText.Text = s.AdapterFound ? L.T("status.on.down") : L.T("status.on.noadapter");
            }
            statusDetails.Text = L.F("status.details", string.IsNullOrEmpty(s.AdapterName) ? L.T("wiz.none") : s.AdapterName, s.FilterCount, c.Freezer.HeldCount);
            if (s.ConfigError != null) warn = L.F("status.config", s.ConfigError);
            else if (s.LastError != null) warn = L.F("status.error", s.LastError);
            else if (s.DnsOutside) warn = L.T("status.dns");
            else if (c.Freezer.HeldCount > 0) warn = L.F("status.frozen", c.Freezer.HeldCount, c.Config.Freeze.MaxSeconds);
        }
        if (warn.Length > 0 || c.LegacyNote == null) statusWarn.Text = warn;
        btnToggle.Text = c.Config.Enabled ? L.T("btn.disable") : L.T("btn.enable");
        tip.SetToolTip(btnToggle, c.Config.Enabled ? L.T("tip.disable") : L.T("tip.enable"));
        btnToggle.Enabled = c.ServiceOk;
    }

    // ---- Apps tab ----

    void BuildAppsTab()
    {
        var page = new TabPage(L.T("tab.apps")) { Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        var bPreset = new Button { Text = L.T("apps.add.preset"), AutoSize = true };
        var bRunning = new Button { Text = L.T("apps.add.running"), AutoSize = true };
        var bBrowse = new Button { Text = L.T("apps.add.browse"), AutoSize = true };
        var bRemove = new Button { Text = L.T("apps.remove"), AutoSize = true };
        bar.Controls.AddRange(new Control[] { bPreset, bRunning, bBrowse, bRemove });
        tip.SetToolTip(bPreset, L.T("apps.tip.preset"));
        tip.SetToolTip(bRunning, L.T("apps.tip.running"));
        tip.SetToolTip(bBrowse, L.T("apps.tip.browse"));
        tip.SetToolTip(apps, L.T("apps.tip.list"));

        apps.Columns.Add(L.T("apps.col.name"), 150);
        apps.Columns.Add(L.T("apps.col.path"), 470);
        apps.Columns.Add(L.T("apps.col.scope"), 110);
        apps.Columns.Add(L.T("apps.col.state"), 110);

        bPreset.Click += async (_, _) =>
        {
            using var dlg = new PresetPicker(c.Config.Apps);
            if (dlg.ShowDialog(this) == DialogResult.OK) await AddEntries(dlg.Selected);
        };
        bRunning.Click += async (_, _) =>
        {
            using var dlg = new RunningPicker();
            if (dlg.ShowDialog(this) == DialogResult.OK) await AddEntries(dlg.Selected);
        };
        bBrowse.Click += async (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = L.T("apps.browse.filter"), Multiselect = true, Title = L.T("apps.browse.title") };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                await AddEntries(dlg.FileNames.Select(f => new AppEntry { Name = AppRule.TitleOf(f), Path = f, Scope = MatchScope.Folder }).ToList());
        };
        bRemove.Click += async (_, _) => await RemoveSelected();
        apps.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) await RemoveSelected();
        };

        layout.Controls.Add(bar);
        layout.Controls.Add(apps);
        page.Controls.Add(layout);
        tabs.TabPages.Add(page);
    }

    void LoadApps()
    {
        apps.BeginUpdate();
        apps.Items.Clear();
        foreach (var e in c.Config.Apps)
        {
            var rule = AppRule.From(e);
            var scope = rule.Kind switch
            {
                RuleKind.StorePackage when e.Scope == MatchScope.Folder => L.T("apps.scope.package"),
                RuleKind.Folder => L.T("apps.scope.folder"),
                _ => L.T("apps.scope.exe")
            };
            var item = new ListViewItem(e.Name) { Tag = e, ToolTipText = e.Path };
            item.SubItems.Add(e.Path);
            item.SubItems.Add(scope);
            item.SubItems.Add("…");
            apps.Items.Add(item);
        }
        apps.EndUpdate();
        UpdateAppStates();
    }

    void UpdateAppStates()
    {
        var procs = processCache.Snapshot();
        foreach (ListViewItem item in apps.Items)
        {
            var rule = AppRule.From((AppEntry)item.Tag!);
            var mine = procs.Where(p => rule.Matches(p.Path, c.Config.Exclusions)).ToList();
            var state = mine.Count == 0 ? L.T("apps.state.stopped")
                : mine.Any(p => c.Freezer.IsHeld(p.Pid)) ? L.T("apps.state.frozen")
                : L.T("apps.state.running");
            if (item.SubItems[3].Text != state) item.SubItems[3].Text = state;
        }
    }

    async Task AddEntries(List<AppEntry> entries)
    {
        if (entries.Count == 0) return;
        var cfg = c.Config.Clone();
        foreach (var e in entries)
            if (!cfg.Apps.Any(a => string.Equals(a.Path, e.Path, StringComparison.OrdinalIgnoreCase))) cfg.Apps.Add(e);
        await Save(cfg);
        LoadApps();
        if (entries.Any(e => e.Preset is "opera" or "operagx" || e.Path.EndsWith("opera.exe", StringComparison.OrdinalIgnoreCase)))
            MessageBox.Show(this, L.T("apps.opera"), "VpnPause", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    async Task RemoveSelected()
    {
        var drop = apps.SelectedItems.Cast<ListViewItem>().Select(i => (AppEntry)i.Tag!).ToHashSet();
        if (drop.Count == 0) return;
        var cfg = c.Config.Clone();
        cfg.Apps = cfg.Apps.Where(a => !drop.Any(d => string.Equals(d.Path, a.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        c.Freezer.ReleaseAll();
        await Save(cfg);
        LoadApps();
    }

    async Task<bool> Save(AppConfig cfg)
    {
        var r = await c.SaveConfigAsync(cfg);
        if (!r.Ok)
        {
            ShowFailure(this, r);
            return false;
        }
        if (r.Rejected is { Count: > 0 })
            MessageBox.Show(this, L.F("apps.rejected", string.Join("\n", r.Rejected)), "VpnPause", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return true;
    }

    public static void ShowFailure(IWin32Window? owner, Response r)
    {
        if (r.Error == Errors.Cancelled)
            MessageBox.Show(owner, L.T("err.cancelled"), "VpnPause", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show(owner, L.F("err.service", r.Error ?? "?"), "VpnPause", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ---- Settings tab ----

    void BuildSettingsTab()
    {
        var page = new TabPage(L.T("tab.settings")) { Padding = new Padding(8), AutoScroll = true };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        var adapterLabel = new Label { Text = L.T("set.adapter"), AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        var adapterRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var refresh = new Button { Text = L.T("set.adapter.refresh"), AutoSize = true };
        refresh.Click += (_, _) => FillAdapters(c.Config);
        adapterRow.Controls.AddRange(new Control[] { adapterBox, refresh });
        tip.SetToolTip(adapterBox, L.T("set.adapter.tip"));
        tip.SetToolTip(adapterLabel, L.T("set.adapter.tip"));

        chkStrict.Text = L.T("set.strict");
        tip.SetToolTip(chkStrict, L.T("set.strict.tip"));
        chkLan.Text = L.T("set.lan");
        tip.SetToolTip(chkLan, L.T("set.lan.tip"));
        chkNotify.Text = L.T("set.notify");
        chkAutostart.Text = L.T("set.autostart");

        var freezeBox = new GroupBox { Text = L.T("set.freeze"), AutoSize = true, Padding = new Padding(10), MinimumSize = new Size(600, 0) };
        var freezeFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        chkFreeze.Text = L.T("set.freeze.on");
        tip.SetToolTip(chkFreeze, L.T("set.freeze.tip"));
        var delayRow = Row(new Label { Text = L.T("set.freeze.delay"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, numDelay,
            new Label { Text = L.T("set.freeze.max"), AutoSize = true, Margin = new Padding(16, 6, 6, 0) }, numMax);
        chkFreezeWindows.Text = L.T("set.freeze.windows");
        freezeFlow.Controls.AddRange(new Control[] { chkFreeze, delayRow, chkFreezeWindows });
        freezeBox.Controls.Add(freezeFlow);
        chkFreeze.CheckedChanged += (_, _) => numDelay.Enabled = numMax.Enabled = chkFreezeWindows.Enabled = chkFreeze.Checked;

        langBox.Items.AddRange(new object[] { "Русский", "English" });
        var langRow = Row(new Label { Text = L.T("set.lang"), AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, langBox);

        var exLabel = new Label { Text = L.T("set.exclusions"), AutoSize = true };
        tip.SetToolTip(exLabel, L.T("set.exclusions.tip"));
        tip.SetToolTip(exclusions, L.T("set.exclusions.tip"));

        var save = new Button { Text = L.T("set.save"), AutoSize = true };
        var clear = new Button { Text = L.T("set.clear"), AutoSize = true, ForeColor = Color.DarkRed, Margin = new Padding(24, 3, 3, 3) };
        tip.SetToolTip(clear, L.T("set.clear.tip"));
        save.Click += async (_, _) => await SaveSettings();
        clear.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, L.T("set.clear.confirm"), "VpnPause", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var r = await c.ClearFiltersAsync();
            if (!r.Ok) ShowFailure(this, r);
            LoadSettings();
        };
        var saveRow = Row(save, savedNote, clear);

        flow.Controls.AddRange(new Control[]
        {
            adapterLabel, adapterRow, Spacer(), chkStrict, chkLan, chkNotify, chkAutostart, Spacer(), freezeBox, Spacer(),
            langRow, exLabel, exclusions, Spacer(), saveRow
        });
        page.Controls.Add(flow);
        tabs.TabPages.Add(page);
    }

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange(controls);
        return row;
    }

    static Control Spacer() => new Panel { Height = 8, Width = 10 };

    sealed record AdapterItem(AdapterInfo? Info, AdapterRef? Ref)
    {
        public bool Auto => Ref == null;

        public override string ToString()
        {
            if (Auto) return L.T("set.adapter.auto");
            if (Info == null) return $"{Ref!.Name} — {L.T("pick.notfound")}";
            var tags = new List<string> { Info.Up ? L.T("set.adapter.up") : L.T("set.adapter.down") };
            if (Info.LooksLikeVpn) tags.Insert(0, L.T("set.adapter.vpn"));
            return $"{Info} ({string.Join(", ", tags)})";
        }
    }

    void FillAdapters(AppConfig cfg)
    {
        adapterBox.Items.Clear();
        var auto = new AdapterItem(null, null);
        adapterBox.Items.Add(auto);
        var all = Network.Adapters().OrderByDescending(a => a.LooksLikeVpn).ThenByDescending(a => a.Up).ToList();
        AdapterItem? selected = null;
        var current = cfg.Adapter;
        foreach (var a in all)
        {
            var item = new AdapterItem(a, a.ToRef());
            adapterBox.Items.Add(item);
            if (current != null && Network.Find(current, new[] { a }) != null) selected ??= item;
        }
        if (current != null && selected == null)
        {
            selected = new AdapterItem(null, current);
            adapterBox.Items.Insert(1, selected);
        }
        adapterBox.SelectedItem = cfg.AutoAdapter || selected == null ? auto : selected;
    }

    void LoadSettings()
    {
        var cfg = c.Config;
        FillAdapters(cfg);
        chkStrict.Checked = cfg.StrictMode;
        chkLan.Checked = cfg.AllowLan;
        chkNotify.Checked = cfg.Notifications;
        chkAutostart.Checked = NativeUi.AutostartEnabled;
        chkFreeze.Checked = cfg.Freeze.Enabled;
        numDelay.Value = Math.Clamp(cfg.Freeze.DelaySeconds, 0, 60);
        numMax.Value = Math.Clamp(cfg.Freeze.MaxSeconds, 10, 3600);
        chkFreezeWindows.Checked = cfg.Freeze.IncludeWindows;
        numDelay.Enabled = numMax.Enabled = chkFreezeWindows.Enabled = chkFreeze.Checked;
        langBox.SelectedIndex = cfg.Language == "en" ? 1 : 0;
        exclusions.Text = string.Join(Environment.NewLine, cfg.Exclusions);
        savedNote.Text = "";
    }

    async Task SaveSettings()
    {
        var cfg = c.Config.Clone();
        if (adapterBox.SelectedItem is AdapterItem a)
        {
            cfg.AutoAdapter = a.Auto;
            if (!a.Auto) cfg.Adapter = a.Ref;
        }
        cfg.StrictMode = chkStrict.Checked;
        cfg.AllowLan = chkLan.Checked;
        cfg.Notifications = chkNotify.Checked;
        cfg.Freeze.Enabled = chkFreeze.Checked;
        cfg.Freeze.DelaySeconds = (int)numDelay.Value;
        cfg.Freeze.MaxSeconds = (int)numMax.Value;
        cfg.Freeze.IncludeWindows = chkFreezeWindows.Checked;
        var oldLang = cfg.Language;
        cfg.Language = langBox.SelectedIndex == 1 ? "en" : "ru";
        cfg.Exclusions = exclusions.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        try
        {
            NativeUi.AutostartEnabled = chkAutostart.Checked;
        }
        catch (Exception)
        {
        }
        if (!await Save(cfg)) return;
        savedNote.Text = L.T("set.saved");
        if (oldLang != cfg.Language) BeginInvoke(() => tray.RebuildForm());
    }

    // ---- Log tab ----

    void BuildLogTab()
    {
        var page = new TabPage(L.T("tab.log")) { Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logSource.Items.AddRange(new object[] { L.T("log.service"), L.T("log.app") });
        logSource.SelectedIndex = 0;
        logSource.SelectedIndexChanged += (_, _) => LoadLog();
        layout.Controls.Add(logSource);
        layout.Controls.Add(logBox);
        page.Controls.Add(layout);
        tabs.TabPages.Add(page);
    }

    void LoadLog()
    {
        var log = logSource.SelectedIndex == 0 ? new RotatingLog(AppPaths.ServiceLogDir, "service") : c.Log;
        var lines = log.Tail(400);
        if (logBox.Items.Count == lines.Count && lines.Count > 0 && (string)logBox.Items[^1] == lines[^1]) return;
        logBox.BeginUpdate();
        logBox.Items.Clear();
        foreach (var l in lines) logBox.Items.Add(l);
        if (logBox.Items.Count > 0) logBox.TopIndex = logBox.Items.Count - 1;
        logBox.EndUpdate();
    }
}
