using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace KeyMouse;

sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly Engine engine;
    readonly Overlay overlay = new();
    readonly ToolStripMenuItem enabledItem;
    readonly Icon iconIdle, iconActive, iconOff;
    AppSettings settings;
    SettingsForm? form;

    public TrayApp()
    {
        _ = overlay.Handle; // UIスレッドへの受け渡し用にハンドルを作っておく

        settings = AppSettings.Load(out bool existed);
        engine = new Engine(settings);
        engine.ActiveChanged += OnActiveChanged;

        iconIdle = Icons.Make(Color.FromArgb(120, 120, 120), false);
        iconActive = Icons.Make(Color.FromArgb(30, 144, 255), false);
        iconOff = Icons.Make(Color.FromArgb(120, 120, 120), true);

        var menu = new ContextMenuStrip();
        enabledItem = new ToolStripMenuItem("マウス操作を有効にする") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += (_, _) => { engine.Enabled = enabledItem.Checked; UpdateIcon(); };
        menu.Items.Add(enabledItem);
        menu.Items.Add("設定...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        tray = new NotifyIcon { Icon = iconIdle, Visible = true, ContextMenuStrip = menu, Text = TrayText() };
        tray.DoubleClick += (_, _) => ShowSettings();

        try
        {
            engine.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("キーボードの監視を開始できませんでした: " + ex.Message, "KeyMouse");
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;

        if (!existed)
        {
            settings.Save();
            tray.ShowBalloonTip(5000, "KeyMouse を起動しました",
                $"{settings.ActivationKey?.DisplayName} を押しながら W/A/S/D の位置でマウス移動、J で左クリックです。" +
                "トレイアイコンをダブルクリックで設定を開きます。", ToolTipIcon.Info);
        }
    }

    string TrayText()
    {
        var t = $"KeyMouse（{settings.ActivationKey?.DisplayName}＋キーでマウス操作）";
        return t.Length > 63 ? t[..63] : t;
    }

    void UpdateIcon() =>
        tray.Icon = !engine.Enabled ? iconOff : engine.IsActive ? iconActive : iconIdle;

    void OnActiveChanged(bool active)
    {
        UpdateIcon();
        if (active && settings.ShowOverlay) overlay.ShowNearCursor();
        else overlay.Hide();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // 画面ロック中はキーを離した通知が届かないので、状態を全部リセットする
        if (overlay.IsHandleCreated) overlay.BeginInvoke(engine.ForceReset);
    }

    void ShowSettings()
    {
        if (form != null)
        {
            form.Activate();
            return;
        }
        form = new SettingsForm(engine, settings);
        form.Saved += s =>
        {
            settings = s;
            try { s.Save(); }
            catch (Exception ex) { MessageBox.Show("設定の保存に失敗しました: " + ex.Message, "KeyMouse"); }
            engine.ApplySettings(s);
            tray.Text = TrayText();
        };
        form.FormClosed += (_, _) => form = null;
        form.Show();
        form.Activate();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        engine.Dispose();
        form?.Close();
        tray.Visible = false;
        tray.Dispose();
        overlay.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>マウスモード中に表示する小さなラベル。クリックを素通りさせ、フォーカスも奪わない。</summary>
sealed class Overlay : Form
{
    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(30, 144, 255);
        Opacity = 0.85;
        AutoScaleMode = AutoScaleMode.Dpi;
        var label = new Label
        {
            Text = "マウスモード",
            ForeColor = Color.White,
            Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold),
            AutoSize = true,
            Padding = new Padding(10, 5, 10, 5),
        };
        Controls.Add(label);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ShowNearCursor()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        if (!Visible) Show();
    }
}

static class Icons
{
    public static Icon Make(Color body, bool off)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(body);
            using var pen = new Pen(Color.White, 2f);
            g.FillEllipse(brush, 6, 1, 20, 30);
            g.DrawLine(pen, 16, 2, 16, 12);
            g.DrawLine(pen, 7, 12, 25, 12);
            if (off)
            {
                using var red = new Pen(Color.FromArgb(220, 40, 40), 4f);
                g.DrawLine(red, 3, 29, 29, 3);
            }
        }
        IntPtr h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        Native.DestroyIcon(h);
        return icon;
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "KeyMouse";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, false);
    }
}
