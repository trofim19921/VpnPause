using VpnPause.Core;

namespace VpnPause.App;

abstract class PickerBase : Form
{
    protected readonly ListView List = new() { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
    protected readonly TextBox Filter = new() { Dock = DockStyle.Fill, PlaceholderText = L.T("pick.filter") };

    public List<AppEntry> Selected { get; } = new();

    protected PickerBase(string title, string hint)
    {
        Text = title;
        Icon = Icons.App;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 480);
        MinimizeBox = MaximizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = hint, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        layout.Controls.Add(Filter);
        layout.Controls.Add(List);
        var ok = new Button { Text = L.T("pick.ok"), AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = L.T("pick.cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft };
        bar.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(bar);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Collect();
        Filter.TextChanged += (_, _) => Fill();
    }

    protected abstract void Fill();

    protected abstract void Collect();
}

sealed class PresetPicker : PickerBase
{
    readonly IReadOnlyList<PresetHit> hits = Presets.Resolve();
    readonly HashSet<string> already;
    readonly HashSet<string> checkedIds = new();

    public PresetPicker(IEnumerable<AppEntry> existing) : base(L.T("pick.presets.title"), L.T("pick.presets.hint"))
    {
        already = existing.Select(e => AppRule.From(e).Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List.Columns.Add(L.T("apps.col.name"), 170);
        List.Columns.Add(L.T("apps.col.path"), 540);
        List.ItemChecked += (_, e) =>
        {
            if (e.Item.Tag is not PresetHit h) return;
            if (!h.Installed && e.Item.Checked) e.Item.Checked = false;
            else if (e.Item.Checked) checkedIds.Add(h.Preset.Id);
            else checkedIds.Remove(h.Preset.Id);
        };
        Fill();
    }

    protected override void Fill()
    {
        List.BeginUpdate();
        List.Items.Clear();
        var f = Filter.Text.Trim();
        foreach (var h in hits)
        {
            if (f.Length > 0 && !h.Preset.Name.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem(h.Preset.Name) { Tag = h };
            item.SubItems.Add(h.Path ?? L.T("pick.notfound"));
            if (!h.Installed) item.ForeColor = Color.Gray;
            else if (already.Contains(AppRule.From(h.ToEntry()).Key)) item.ForeColor = Color.SeaGreen;
            List.Items.Add(item);
            item.Checked = checkedIds.Contains(h.Preset.Id);
        }
        List.EndUpdate();
    }

    protected override void Collect()
    {
        Selected.Clear();
        Selected.AddRange(hits.Where(h => h.Installed && checkedIds.Contains(h.Preset.Id)).Select(h => h.ToEntry()));
    }
}

sealed class RunningPicker : PickerBase
{
    readonly List<(string name, string path)> procs;
    readonly HashSet<string> checkedPaths = new(StringComparer.OrdinalIgnoreCase);

    public RunningPicker() : base(L.T("pick.running.title"), L.T("pick.running.hint"))
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        var self = Environment.ProcessPath ?? "";
        procs = ProcessScanner.Snapshot()
            .Select(p => p.Path)
            .Where(p => !p.StartsWith(win, StringComparison.OrdinalIgnoreCase) && !string.Equals(p, self, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (AppRule.TitleOf(p), p))
            .OrderBy(t => t.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List.Columns.Add(L.T("apps.col.name"), 170);
        List.Columns.Add(L.T("apps.col.path"), 540);
        List.ItemChecked += (_, e) =>
        {
            if (e.Item.Tag is not string p) return;
            if (e.Item.Checked) checkedPaths.Add(p);
            else checkedPaths.Remove(p);
        };
        Fill();
    }

    protected override void Fill()
    {
        List.BeginUpdate();
        List.Items.Clear();
        var f = Filter.Text.Trim();
        foreach (var (name, path) in procs)
        {
            if (f.Length > 0 && !name.Contains(f, StringComparison.OrdinalIgnoreCase) && !path.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem(name) { Tag = path, ToolTipText = path };
            item.SubItems.Add(path);
            List.Items.Add(item);
            item.Checked = checkedPaths.Contains(path);
        }
        List.EndUpdate();
    }

    protected override void Collect()
    {
        Selected.Clear();
        Selected.AddRange(checkedPaths.Select(p => new AppEntry { Name = AppRule.TitleOf(p), Path = p, Scope = MatchScope.Folder }));
    }
}
