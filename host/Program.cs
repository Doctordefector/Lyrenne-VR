using Microsoft.Win32;
using SkiaSharp;

namespace LyrenneVR;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--render") return RenderToFile(args);
        if (args.Contains("--selftest")) return SelfTest.Run();
        if (args.Contains("--steamvr-test")) return SteamVrTest();

        using var single = new Mutex(true, "LyrenneVR.Host.Single", out bool first);
        if (!first)
        {
            // Launched again (from SteamVR, or a second click): the running copy carries on.
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var settings = Settings.Load();
        if (args.Contains("--always")) settings.ForceShow = ShowMode.Always;
        using var host = new Host(settings);
        host.Start();
        Log.Write($"LyrenneVR started ({string.Join(' ', args)})");
        using var tray = new Tray(host);
        if (args.Contains("--preview")) tray.ShowPreview();
        Application.Run();
        settings.Save();
        return 0;
    }

    /// <summary>
    /// --steamvr-test: with SteamVR already running (its null driver is enough), connects the real
    /// overlay backend, shows the demo panel in front of the headset and checks SteamVR's view of it.
    /// </summary>
    static int SteamVrTest()
    {
        var settings = new Settings { Lyrics = false };
        var panel = new Panel(new Media(settings), new Volume(), new Lyrics(), settings);
        using var steam = new SteamVr(settings) { RegisterWithSteamVr = false };
        long until = Environment.TickCount64 + 30000;
        while (!steam.EnsureConnected() && Environment.TickCount64 < until) Thread.Sleep(500);
        if (!steam.Connected)
        {
            Console.WriteLine("FAIL could not connect to SteamVR (see host.log)");
            return 1;
        }
        Console.WriteLine("ok   connected to SteamVR as an overlay app");
        var frame = steam.Poll();
        Console.WriteLine($"{(frame?.Head != null ? "ok  " : "FAIL")} headset pose readable");
        Console.WriteLine($"     controllers: left grip {(frame?.LeftGrip != null ? "yes" : "no")}, right aim {(frame?.RightAim != null ? "yes" : "no")}");

        using var bmp = new SKBitmap(new SKImageInfo(Panel.Width, Panel.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp)) panel.Render(c, Demo.Track());
        var problems = steam.SelfCheck(bmp.GetPixelSpan());
        foreach (var p in problems) Console.WriteLine($"FAIL {p}");
        if (problems.Count == 0) Console.WriteLine("ok   overlay texture uploaded, attached and visible");

        for (int i = 0; i < 40; i++)
        {
            steam.Poll();
            Thread.Sleep(100);
        }
        return problems.Count == 0 && frame?.Head != null ? 0 : 1;
    }

    /// <summary>--render out.png [--demo] [--pointer x,y[,pressed]]: draws one frame without any VR.</summary>
    static int RenderToFile(string[] args)
    {
        var settings = new Settings();
        var media = new Media(settings);
        var lyrics = new Lyrics();
        var panel = new Panel(media, new Volume(), lyrics, settings);
        NowPlaying np;
        if (args.Contains("--demo"))
        {
            np = Demo.Track();
        }
        else
        {
            media.StartAsync().Wait(3000);
            Thread.Sleep(1500);
            np = media.Snapshot();
            if (settings.Lyrics)
            {
                lyrics.At(np);
                Thread.Sleep(2500);
            }
        }
        int pi = Array.IndexOf(args, "--pointer");
        if (pi >= 0 && pi + 1 < args.Length)
        {
            var parts = args[pi + 1].Split(',');
            var at = new SKPoint(float.Parse(parts[0]), float.Parse(parts[1]));
            bool pressed = parts.Length > 2 && parts[2] == "pressed";
            panel.Pointer(at, pressed ? 0 : 0.5f, false);
            if (pressed) panel.Pointer(at, 0, true);
        }
        var info = new SKImageInfo(Panel.Width, Panel.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (args.Contains("--demo")) Demo.Render(panel, surface.Canvas, np, lyrics);
        else panel.Render(surface.Canvas, np);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(args[1], data.ToArray());
        Console.WriteLine($"{args[1]}: {np.Title} / {np.Artist} ({np.Source})");
        return 0;
    }
}

/// <summary>A made-up track for screenshots, so the layout can be checked without a player running.</summary>
static class Demo
{
    public static NowPlaying Track()
    {
        var info = new SKImageInfo(512, 512);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        using (var p = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(512, 512),
                new[] { new SKColor(0xFF, 0x5E, 0x7E), new SKColor(0x6A, 0x3D, 0xE8) }, SKShaderTileMode.Clamp),
        })
            c.DrawRect(0, 0, 512, 512, p);
        using (var p = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(60) })
        {
            c.DrawCircle(360, 170, 120, p);
            c.DrawCircle(140, 400, 80, p);
        }
        return new NowPlaying("Midnight City", "M83", "Hurry Up, We're Dreaming", surface.Snapshot(), true,
            TimeSpan.FromSeconds(97), TimeSpan.FromSeconds(244), true, Repeat.All, true, "demo");
    }

    public static void Render(Panel panel, SKCanvas canvas, NowPlaying np, Lyrics lyrics) => panel.Render(canvas, np);
}

/// <summary>The tray icon: status, quick toggles, settings and quit.</summary>
sealed class Tray : IDisposable
{
    readonly Host _host;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _status = new("Starting") { Enabled = false };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    PreviewForm? _preview;
    SettingsForm? _settingsForm;

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public Tray(Host host)
    {
        _host = host;
        var s = host.Settings;
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Preview on desktop", null, (_, _) => ShowPreview());
        menu.Items.Add("Adjust position and size...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());

        var show = new ToolStripMenuItem("Show the player");
        foreach (var (mode, label) in new[] { (ShowMode.Glance, "When I look at my wrist"), (ShowMode.Always, "Always"), (ShowMode.Hidden, "Never") })
        {
            var item = new ToolStripMenuItem(label) { Checked = s.Show == mode, Tag = mode };
            item.Click += (_, _) =>
            {
                s.Show = mode;
                foreach (ToolStripMenuItem i in show.DropDownItems) i.Checked = (ShowMode)i.Tag! == mode;
                s.Save();
            };
            show.DropDownItems.Add(item);
        }
        menu.Items.Add(show);
        menu.Items.Add(Toggle("Wear on right wrist", () => s.WatchHand == Hand.Right, v => s.WatchHand = v ? Hand.Right : Hand.Left));
        menu.Items.Add(Toggle("Synced lyrics", () => s.Lyrics, v => s.Lyrics = v));
        menu.Items.Add(Toggle("Tap feedback (vibration)", () => s.Haptics, v => s.Haptics = v));
        menu.Items.Add(Toggle("Only follow Lyrenne", () => s.LyrenneOnly, v => s.LyrenneOnly = v));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Toggle("Start with Windows", StartsWithWindows, SetStartWithWindows));
        menu.Items.Add(Toggle("Start with SteamVR", () => s.SteamVrAutoLaunch, v =>
        {
            s.SteamVrAutoLaunch = v;
            _host.ApplySteamVrAutoLaunch();
        }));
        menu.Items.Add("Open log folder", null, (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Settings.Directory) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Application.ExitThread());

        _icon = new NotifyIcon { Icon = AppIcon.Create(), Text = "Lyrenne VR", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => ShowPreview();
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
        UpdateStatus();
    }

    ToolStripMenuItem Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(label) { Checked = get() };
        item.Click += (_, _) =>
        {
            set(!get());
            item.Checked = get();
            _host.Settings.Save();
        };
        return item;
    }

    void UpdateStatus()
    {
        string text = _host.Status;
        _status.Text = text;
        _icon.Text = ("Lyrenne VR: " + text).Length > 63 ? ("Lyrenne VR: " + text)[..63] : "Lyrenne VR: " + text;
    }

    static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("LyrenneVR") is string;
    }

    static void SetStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue("LyrenneVR", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("LyrenneVR", false);
    }

    public void ShowPreview()
    {
        if (_preview is { IsDisposed: false })
        {
            _preview.Activate();
            return;
        }
        _preview = new PreviewForm(_host);
        _preview.Show();
    }

    void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_host.Settings);
        _settingsForm.Show();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _preview?.Dispose();
        _settingsForm?.Dispose();
    }
}

/// <summary>The panel in a desktop window. The mouse stands in for the fingertip.</summary>
sealed class PreviewForm : Form
{
    readonly Host _host;
    readonly PictureBox _view = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 24, 28) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    bool _down;
    SKPoint? _mouse;

    public PreviewForm(Host host)
    {
        _host = host;
        Text = "Lyrenne VR preview";
        Icon = AppIcon.Create();
        ClientSize = new Size(Panel.Width / 2 + 40, Panel.Height / 2 + 40);
        Padding = new Padding(20);
        BackColor = _view.BackColor;
        Controls.Add(_view);
        _view.MouseMove += (_, e) => Feed(e.Location);
        _view.MouseDown += (_, e) => { _down = true; Feed(e.Location); };
        _view.MouseUp += (_, e) => { _down = false; Feed(e.Location); };
        _view.MouseLeave += (_, _) =>
        {
            _mouse = null;
            lock (_host.PanelLock) _host.Panel.Pointer(null, 1, false);
        };
        _timer.Tick += (_, _) => Redraw();
        _timer.Start();
    }

    void Feed(Point p)
    {
        // Map from the zoomed picture box back to panel pixels.
        float scale = Math.Min((float)_view.Width / Panel.Width, (float)_view.Height / Panel.Height);
        float ox = (_view.Width - Panel.Width * scale) / 2, oy = (_view.Height - Panel.Height * scale) / 2;
        _mouse = new SKPoint((p.X - ox) / scale, (p.Y - oy) / scale);
        lock (_host.PanelLock) _host.Panel.Pointer(_mouse, _down ? 0 : 0.4f, _down);
    }

    void Redraw()
    {
        using var image = _host.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var ms = new MemoryStream(data.ToArray());
        var old = _view.Image;
        _view.Image = Image.FromStream(ms);
        old?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _view.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Live tuning for where the panel sits on the wrist. Changes apply on the next frame.</summary>
sealed class SettingsForm : Form
{
    public SettingsForm(Settings s)
    {
        Text = "Lyrenne VR: position and size";
        Icon = AppIcon.Create();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var table = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, float min, float max, float step, Func<float> get, Action<float> set, string unit)
        {
            var value = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MinimumSize = new Size(70, 0) };
            var bar = new TrackBar
            {
                Minimum = (int)MathF.Round(min / step),
                Maximum = (int)MathF.Round(max / step),
                Value = Math.Clamp((int)MathF.Round(get() / step), (int)MathF.Round(min / step), (int)MathF.Round(max / step)),
                TickStyle = TickStyle.None,
                Width = 320,
            };
            void Show() => value.Text = $"{get():0.#} {unit}";
            bar.ValueChanged += (_, _) =>
            {
                set(bar.Value * step);
                Show();
            };
            Show();
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            table.Controls.Add(bar);
            table.Controls.Add(value);
        }

        Row("Panel width", 8, 30, 0.5f, () => s.PanelWidthCm, v => s.PanelWidthCm = v, "cm");
        Row("Out from the wrist", -6, 10, 0.25f, () => s.OffsetOutCm, v => s.OffsetOutCm = v, "cm");
        Row("Toward the thumb", -10, 10, 0.25f, () => s.OffsetThumbCm, v => s.OffsetThumbCm = v, "cm");
        Row("Toward the elbow", -10, 25, 0.25f, () => s.OffsetElbowCm, v => s.OffsetElbowCm = v, "cm");
        Row("Tilt toward face", -60, 60, 1, () => s.TiltDeg, v => s.TiltDeg = v, "deg");
        Row("Fingertip reach", -5, 14, 0.25f, () => s.FingertipCm, v => s.FingertipCm = v, "cm");
        Row("Glance angle", 15, 70, 1, () => s.GlanceAngleDeg, v => s.GlanceAngleDeg = v, "deg");

        var reset = new Button { Text = "Reset to defaults", AutoSize = true };
        reset.Click += (_, _) =>
        {
            var d = new Settings();
            s.PanelWidthCm = d.PanelWidthCm;
            s.OffsetOutCm = d.OffsetOutCm;
            s.OffsetThumbCm = d.OffsetThumbCm;
            s.OffsetElbowCm = d.OffsetElbowCm;
            s.TiltDeg = d.TiltDeg;
            s.FingertipCm = d.FingertipCm;
            s.GlanceAngleDeg = d.GlanceAngleDeg;
            s.Save();
            Close();
        };
        table.Controls.Add(reset);
        table.SetColumnSpan(reset, 3);
        Controls.Add(table);
        FormClosed += (_, _) => s.Save();
    }
}

static class AppIcon
{
    static Icon? _icon;

    /// <summary>A round accent badge with a note, drawn once at startup.</summary>
    public static Icon Create()
    {
        if (_icon != null) return _icon;
        using var bmp = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Transparent);
            using var bg = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(64, 64),
                    new[] { new SKColor(0x8A, 0xB4, 0xFF), new SKColor(0xB3, 0x88, 0xFF) }, SKShaderTileMode.Clamp),
            };
            c.DrawCircle(32, 32, 30, bg);
            c.Translate(14, 12);
            c.Scale(36 / 24f);
            using var fg = new SKPaint { IsAntialias = true, Color = new SKColor(16, 16, 24) };
            c.DrawPath(Icons.Note, fg);
        }
        using var gdi = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = gdi.LockBits(new Rectangle(0, 0, 64, 64), System.Drawing.Imaging.ImageLockMode.WriteOnly, gdi.PixelFormat);
        System.Runtime.InteropServices.Marshal.Copy(bmp.Bytes, 0, data.Scan0, bmp.Bytes.Length);
        gdi.UnlockBits(data);
        _icon = Icon.FromHandle(gdi.GetHicon());
        return _icon;
    }
}
