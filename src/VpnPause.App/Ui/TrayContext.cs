using VpnPause.Core;

namespace VpnPause.App;

sealed class TrayContext : ApplicationContext
{
    readonly AppController controller;
    readonly NotifyIcon tray;
    readonly MessageWindow messages;
    readonly System.Windows.Forms.Timer timer;
    readonly ToolStripMenuItem miOpen, miToggle, miRelease, miExit;
    MainForm? form;
    bool exiting;
    bool refreshing;

    public TrayContext(AppController controller, bool startHidden)
    {
        this.controller = controller;
        miOpen = new ToolStripMenuItem("", null, (_, _) => ShowMain());
        miToggle = new ToolStripMenuItem("", null, async (_, _) => await controller.SetEnabledAsync(!controller.Config.Enabled));
        miRelease = new ToolStripMenuItem("", null, async (_, _) => await controller.ReleaseAllAsync());
        miExit = new ToolStripMenuItem("", null, (_, _) => Exit());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { miOpen, miToggle, miRelease, new ToolStripSeparator(), miExit });
        tray = new NotifyIcon { Icon = Icons.Gray, Text = "VpnPause", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMain();
        };

        messages = new MessageWindow(this);
        controller.Changed += UpdateTray;
        controller.VpnChanged += OnVpnChanged;

        timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += async (_, _) =>
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                await controller.RefreshStatusAsync();
            }
            finally
            {
                refreshing = false;
            }
        };
        _ = StartAsync(startHidden);
    }

    async Task StartAsync(bool startHidden)
    {
        await controller.InitAsync();
        ApplyTexts();
        timer.Start();
        if (!controller.Config.FirstRunDone) ShowWizard();
        else if (!startHidden) ShowMain();
        UpdateTray();
    }

    void ApplyTexts()
    {
        miOpen.Text = L.T("tray.open");
        miRelease.Text = L.T("btn.release");
        miExit.Text = L.T("tray.exit");
        miToggle.Text = controller.Config.Enabled ? L.T("btn.disable") : L.T("btn.enable");
    }

    public void ShowMain()
    {
        if (form == null || form.IsDisposed)
        {
            form = new MainForm(controller, this);
            form.FormClosing += (_, e) =>
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    form.Hide();
                }
            };
        }
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    public void ShowWizard()
    {
        using var wiz = new WizardForm(controller);
        wiz.ShowDialog(form != null && form.Visible ? form : null);
        RebuildForm();
        ShowMain();
    }

    public void RebuildForm()
    {
        ApplyTexts();
        if (form != null && !form.IsDisposed)
        {
            var visible = form.Visible;
            exiting = true;
            form.Close();
            form.Dispose();
            exiting = false;
            form = null;
            if (visible) ShowMain();
        }
    }

    void UpdateTray()
    {
        ApplyTexts();
        var s = controller.Status;
        string text;
        if (!controller.ServiceOk || s == null)
        {
            tray.Icon = Icons.Gray;
            text = L.T("status.service");
        }
        else if (!s.Enabled)
        {
            tray.Icon = Icons.Gray;
            text = L.T("status.off");
        }
        else if (s.VpnUp)
        {
            tray.Icon = Icons.Green;
            text = L.T("status.on.up");
        }
        else
        {
            tray.Icon = controller.Freezer.HeldCount > 0 ? Icons.Orange : Icons.Red;
            text = s.AdapterFound ? L.T("status.on.down") : L.T("status.on.noadapter");
        }
        tray.Text = text.Length > 63 ? text[..63] : text;
        form?.RefreshStatus();
    }

    void OnVpnChanged(bool up)
    {
        if (!controller.Config.Notifications || !controller.Config.Enabled) return;
        tray.ShowBalloonTip(3000, "VpnPause", up ? L.T("notify.up") : L.T("notify.down"), up ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    public void Exit()
    {
        exiting = true;
        timer.Stop();
        controller.Dispose();
        tray.Visible = false;
        tray.Dispose();
        messages.DestroyHandle();
        form?.Close();
        ExitThread();
    }

    public async void HotkeyPressed() => await controller.ReleaseAllAsync();

    sealed class MessageWindow : NativeWindow
    {
        readonly TrayContext owner;

        public MessageWindow(TrayContext owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Caption = "VpnPause.Messages" });
            NativeUi.RegisterReleaseHotkey(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeUi.ShowMessage) owner.ShowMain();
            else if (m.Msg == NativeUi.WM_HOTKEY && (int)m.WParam == NativeUi.HotkeyId) owner.HotkeyPressed();
            base.WndProc(ref m);
        }

        public override void DestroyHandle()
        {
            NativeUi.UnregisterReleaseHotkey(Handle);
            base.DestroyHandle();
        }
    }
}
