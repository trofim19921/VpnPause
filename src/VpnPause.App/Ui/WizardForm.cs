using VpnPause.Core;

namespace VpnPause.App;

/// <summary>First run: language, VPN adapter, apps, check.</summary>
sealed class WizardForm : Form
{
    static readonly HashSet<string> DefaultPresets = new() { "chatgpt", "codex", "claude", "claudecode", "cursor" };

    readonly AppController c;
    readonly Panel host = new() { Dock = DockStyle.Fill, Padding = new Padding(16) };
    readonly Button back = new() { AutoSize = true };
    readonly Button next = new() { AutoSize = true };
    readonly ComboBox lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly ListBox adapters = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly CheckedListBox presetList = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly Label checkText = new() { Dock = DockStyle.Fill };
    readonly IReadOnlyList<PresetHit> hits = Presets.Resolve();
    readonly System.Windows.Forms.Timer checkTimer = new() { Interval = 1500 };
    int step;
    AppConfig draft;

    public WizardForm(AppController controller)
    {
        c = controller;
        draft = controller.Config.Clone();
        Icon = Icons.App;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 500);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(10) };
        bar.Controls.AddRange(new Control[] { next, back });
        Controls.Add(host);
        Controls.Add(bar);

        lang.Items.AddRange(new object[] { "Русский", "English" });
        lang.SelectedIndex = draft.Language == "en" ? 1 : 0;
        lang.SelectedIndexChanged += (_, _) =>
        {
            draft.Language = lang.SelectedIndex == 1 ? "en" : "ru";
            L.Lang = draft.Language;
            Show(step);
        };

        back.Click += (_, _) => Show(step - 1);
        next.Click += async (_, _) => await Next();
        checkTimer.Tick += async (_, _) =>
        {
            await c.RefreshStatusAsync();
            UpdateCheck();
        };
        FormClosed += (_, _) => checkTimer.Dispose();
        Show(0);
    }

    void Show(int s)
    {
        step = Math.Clamp(s, 0, 3);
        checkTimer.Enabled = step == 3;
        host.Controls.Clear();
        Text = $"{L.T("wiz.title")} · {step + 1}/4";
        back.Text = L.T("wiz.back");
        next.Text = step == 3 ? L.T("wiz.finish") : L.T("wiz.next");
        back.Enabled = step > 0;

        switch (step)
        {
            case 0:
                var t0 = Text0(L.T("wiz.step1") + (c.LegacyNote != null ? "\n\n" + c.LegacyNote : ""));
                var langRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
                langRow.Controls.Add(new Label { Text = L.T("set.lang"), AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
                langRow.Controls.Add(lang);
                host.Controls.Add(t0);
                host.Controls.Add(langRow);
                break;
            case 1:
                var refresh = new Button { Text = L.T("set.adapter.refresh"), AutoSize = true, Dock = DockStyle.Bottom };
                refresh.Click += (_, _) => FillAdapters();
                host.Controls.Add(adapters);
                host.Controls.Add(refresh);
                host.Controls.Add(Text0(L.T("wiz.step2")));
                FillAdapters();
                break;
            case 2:
                host.Controls.Add(presetList);
                host.Controls.Add(Text0(L.T("wiz.step3")));
                FillPresets();
                break;
            case 3:
                host.Controls.Add(checkText);
                host.Controls.Add(new Label { Text = L.T("wiz.step4"), Dock = DockStyle.Top, Font = new Font(Font, FontStyle.Bold), Height = 32 });
                UpdateCheck();
                break;
        }
    }

    static Label Text0(string text) => new() { Text = text, Dock = DockStyle.Top, AutoSize = false, Height = 110 };

    sealed record AdapterChoice(AdapterInfo? Info)
    {
        public override string ToString()
        {
            if (Info == null) return L.T("set.adapter.auto");
            var tags = new List<string> { Info.Up ? L.T("set.adapter.up") : L.T("set.adapter.down") };
            if (Info.LooksLikeVpn) tags.Insert(0, L.T("set.adapter.vpn"));
            return $"{Info} ({string.Join(", ", tags)})";
        }
    }

    void FillAdapters()
    {
        adapters.Items.Clear();
        adapters.Items.Add(new AdapterChoice(null));
        var all = Network.Adapters().OrderByDescending(a => a.LooksLikeVpn).ThenByDescending(a => a.Up).ToList();
        foreach (var a in all) adapters.Items.Add(new AdapterChoice(a));
        var current = draft.AutoAdapter ? null : Network.Find(draft.Adapter, all);
        adapters.SelectedIndex = current == null ? 0 : all.FindIndex(a => a.Id == current.Id) + 1;
    }

    void FillPresets()
    {
        presetList.Items.Clear();
        var existing = draft.Apps.Select(e => AppRule.From(e).Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hits.Where(h => h.Installed))
        {
            var key = AppRule.From(h.ToEntry()).Key;
            var check = existing.Contains(key) || (draft.Apps.Count == 0 && DefaultPresets.Contains(h.Preset.Id));
            presetList.Items.Add(new PresetChoice(h), check);
        }
    }

    sealed record PresetChoice(PresetHit Hit)
    {
        public override string ToString() => $"{Hit.Preset.Name}  —  {Hit.Path}";
    }

    async Task Next()
    {
        switch (step)
        {
            case 1:
                if (adapters.SelectedItem is AdapterChoice a)
                {
                    draft.AutoAdapter = a.Info == null;
                    if (a.Info != null) draft.Adapter = a.Info.ToRef();
                }
                break;
            case 2:
                var chosen = presetList.CheckedItems.Cast<PresetChoice>().Select(p => p.Hit.ToEntry()).ToList();
                var presetKeys = hits.Where(h => h.Installed).Select(h => AppRule.From(h.ToEntry()).Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                draft.Apps = draft.Apps.Where(e => !presetKeys.Contains(AppRule.From(e).Key)).ToList();
                foreach (var e in chosen)
                    if (!draft.Apps.Any(x => AppRule.From(x).Key == AppRule.From(e).Key)) draft.Apps.Add(e);
                draft.Enabled = true;
                var r = await c.SaveConfigAsync(draft);
                if (!r.Ok)
                {
                    MainForm.ShowFailure(this, r);
                    return;
                }
                draft = r.Config!.Clone();
                break;
            case 3:
                draft = c.Config.Clone();
                draft.FirstRunDone = true;
                await c.SaveConfigAsync(draft);
                DialogResult = DialogResult.OK;
                Close();
                return;
        }
        if (step == 0)
        {
            var r0 = await c.SaveConfigAsync(draft);
            if (r0.Ok && r0.Config != null) draft = r0.Config.Clone();
        }
        Show(step + 1);
    }

    void UpdateCheck()
    {
        var s = c.Status;
        var adapter = !string.IsNullOrEmpty(s?.AdapterName) ? s!.AdapterName
            : draft.AutoAdapter ? L.T("set.adapter.auto") : draft.Adapter?.Name ?? L.T("wiz.none");
        var lines = new List<string>
        {
            L.F("wiz.check.adapter", adapter),
            L.F("wiz.check.vpn", c.LocalVpnUp ? L.T("wiz.yes") : L.T("wiz.no")),
            L.F("wiz.check.filters", s?.FilterCount ?? 0),
            ""
        };
        if (!c.ServiceOk) lines.Add(L.T("status.service"));
        lines.Add(L.T("wiz.check.how"));
        checkText.Text = string.Join(Environment.NewLine, lines);
    }
}
