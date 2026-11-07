using System.Runtime.InteropServices;
using SkiaSharp;

namespace LyrenneVR;

public enum Source { None, OpenXr, SteamVr }

/// <summary>
/// The tick loop. Each tick it picks the live backend (a game with the OpenXR layer loaded wins over
/// SteamVR, since that is what is on screen), runs the wrist logic, renders when something changed
/// and hands the frame to whichever backend is showing it.
/// </summary>
public sealed class Host : IDisposable
{
    public Settings Settings { get; }
    public Media Media { get; }
    public Panel Panel { get; }
    public object PanelLock { get; } = new();

    readonly Lyrics _lyrics = new();
    readonly Volume _volume = new();
    readonly Wrist _wrist;
    readonly Bridge _bridge;
    readonly SteamVr _steam;
    readonly SKBitmap _bitmap = new(new SKImageInfo(Panel.Width, Panel.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
    readonly Thread _thread;
    volatile bool _running = true;
    string _lastSignature = "";
    long _lastRender;

    public Source Source { get; private set; }
    public string Status { get; private set; } = "Waiting for a VR game";

    public Host(Settings settings)
    {
        Settings = settings;
        Media = new Media(settings);
        Panel = new Panel(Media, _volume, _lyrics, settings);
        _wrist = new Wrist(settings, Panel);
        _bridge = new Bridge();
        _steam = new SteamVr(settings);
        Panel.Haptic += OnHaptic;
        _thread = new Thread(Loop) { IsBackground = true, Name = "LyrenneVR tick" };
    }

    public void Start()
    {
        _ = Media.StartAsync();
        _thread.Start();
    }

    public void ApplySteamVrAutoLaunch() => _steam.ApplyAutoLaunch();

    /// <summary>A faint tick on the watch hand as the player appears, so it can be felt opening.</summary>
    void OnOpened()
    {
        if (!Settings.Haptics) return;
        if (Source == Source.OpenXr) _bridge.Haptic(Settings.WatchHand, 0.15f, 0.012f);
        else if (Source == Source.SteamVr) _steam.Haptic(Settings.WatchHand, 0.15f, 0.012f);
    }

    void OnHaptic(float amplitude, float seconds)
    {
        if (!Settings.Haptics) return;
        if (Source == Source.OpenXr) _bridge.Haptic(Settings.PokeHand, amplitude, seconds);
        else if (Source == Source.SteamVr) _steam.Haptic(Settings.PokeHand, amplitude, seconds);
    }

    void Loop()
    {
        timeBeginPeriod(1);
        try
        {
            while (_running)
            {
                try
                {
                    Tick();
                }
                catch (Exception e)
                {
                    Log.Write($"tick failed: {e}");
                    Thread.Sleep(500);
                }
                // Poses arrive once per game frame (11 ms at 90 Hz); a 3 ms tick picks each one up
                // within a few ms instead of up to a whole frame late.
                Thread.Sleep(Source == Source.None ? 200 : 3);
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    void Tick()
    {
        _bridge.Heartbeat();
        _steam.EnsureConnected();
        var steamFrame = _steam.Poll();  // always drain SteamVR events, even when the layer is in charge

        TrackingFrame? frame;
        if (_bridge.ClientAlive)
        {
            Source = Source.OpenXr;
            frame = _bridge.ReadPoses();
            Status = $"OpenXR: {_bridge.AppName} on {_bridge.RuntimeName}";
        }
        else if (steamFrame != null)
        {
            Source = Source.SteamVr;
            frame = steamFrame;
            Status = "SteamVR overlay";
        }
        else
        {
            Source = Source.None;
            frame = null;
            Status = "Waiting for a VR game";
        }

        bool visible;
        bool changed = false;
        lock (PanelLock)
        {
            Panel.CursorInTexture = Source == Source.OpenXr && !_bridge.LayerDrawsCursor;
            if (frame != null) _wrist.Update(frame);
            visible = frame != null && _wrist.Visible;
            // Keep the picture current while hidden too (at most once a second), so a glance shows
            // the song that is playing now instead of drawing it first.
            if (frame != null) changed = RenderIfNeeded(hidden: !visible);
            if (visible && !_wasVisible) OnOpened();
        }

        var anchor = Settings.WatchHand == Hand.Left ? Anchor.LeftGrip : Anchor.RightGrip;
        if (changed) _bridge.PublishFrame(_bitmap.GetPixelSpan(), Panel.Width, Panel.Height);
        _bridge.SetPanel(visible && Source == Source.OpenXr, anchor, _wrist.PanelOffset, _wrist.PanelWidth);
        _bridge.SetCapture((visible && _wrist.Pointing) || Settings.ForceCapture);
        _steam.Capture = Source == Source.SteamVr && ((visible && _wrist.Pointing) || Settings.ForceCapture);
        var cursor = visible ? Panel.Cursor : null;
        float cursorSize = cursor is { } cur ? (cur.Pressed ? 0.008f : 0.009f + 0.011f * cur.Depth) * (_wrist.PanelWidth / 0.16f) : 0;
        var at = cursor?.At ?? default;
        _bridge.SetCursor(cursor != null && Source == Source.OpenXr, at.X / Panel.Width, at.Y / Panel.Height, cursorSize);
        _steam.Present(visible && Source == Source.SteamVr, _wrist.PanelOffset, _wrist.PanelWidth, _bitmap.GetPixelSpan(), changed,
            cursor is { } c2 ? (c2.At.X / Panel.Width, c2.At.Y / Panel.Height, cursorSize) : null);
        TrackRenderStats(visible);
    }

    bool RenderIfNeeded(bool hidden)
    {
        var np = Media.Snapshot();
        string sig = Panel.Signature(np);
        long now = Environment.TickCount64;
        if (sig == _lastSignature && now - _lastRender < 1000) return false;
        if (hidden && now - _lastRender < 1000) return false;
        _lastSignature = sig;
        _lastRender = now;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using var canvas = new SKCanvas(_bitmap);
        Panel.Render(canvas, np);
        canvas.Flush();
        _renders++;
        _renderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return true;
    }

    int _renders;
    long _renderTicks, _shownAt;
    bool _wasVisible;

    /// <summary>One log line per glance: how often the panel was redrawn and what each redraw cost.</summary>
    void TrackRenderStats(bool visible)
    {
        if (visible && !_wasVisible)
        {
            _shownAt = Environment.TickCount64;
            _renders = 0;
            _renderTicks = 0;
        }
        else if (!visible && _wasVisible && _renders > 0)
        {
            double secs = Math.Max((Environment.TickCount64 - _shownAt) / 1000.0, 0.001);
            double ms = _renderTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _renders;
            Log.Write($"panel redrawn {_renders} times in {secs:0.0} s ({_renders / secs:0.0}/s), {ms:0.0} ms each");
        }
        _wasVisible = visible;
    }

    /// <summary>Renders the current panel to a new image, for the desktop preview and screenshots.</summary>
    public SKImage Snapshot()
    {
        lock (PanelLock)
        {
            var info = new SKImageInfo(Panel.Width, Panel.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            Panel.Render(surface.Canvas, Media.Snapshot(), drawCursor: true);
            return surface.Snapshot();
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1000);
        _bridge.Dispose();
        _steam.Dispose();
        Media.Dispose();
        _bitmap.Dispose();
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
}
