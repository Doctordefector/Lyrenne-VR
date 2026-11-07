using System.Numerics;
using SkiaSharp;

namespace LyrenneVR;

/// <summary>
/// The VR half of the player: decides when the panel shows (glance at your wrist) and turns the
/// other hand's fingertip into touches on it. Backend agnostic; fed one TrackingFrame per tick.
/// </summary>
public sealed class Wrist
{
    readonly Settings _settings;
    readonly Panel _panel;

    bool _visible;
    long _glanceSince = -1, _awaySince = -1;
    bool _touching;
    bool _triggerDown, _armed;
    const float TriggerDown = 0.55f, TriggerUp = 0.35f;

    /// <summary>The aim ray is on the panel: the layer should route the trigger to us, not the game.</summary>
    public bool Pointing { get; private set; }

    // Hover zone in front of the panel and how far a press may go through it before it stops counting.
    const float HoverDepth = 0.06f;
    const float PressDepth = 0.0f;
    const float ReleaseDepth = 0.012f;
    const float PassThrough = -0.05f;

    public Wrist(Settings settings, Panel panel)
    {
        _settings = settings;
        _panel = panel;
    }

    public bool Visible => _visible;

    // Spin of the panel in its own plane that keeps the text level to the eyes, radians.
    float _autoRoll;

    /// <summary>Panel pose relative to the watch hand's grip, including the self-levelling spin.</summary>
    public Pose PanelOffset
    {
        get
        {
            var o = _settings.PanelOffset;
            if (!_settings.AutoUpright) return o;
            return new Pose(o.Position, Quaternion.Normalize(o.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _autoRoll)));
        }
    }

    public float PanelWidth => _settings.PanelWidthM;
    public float PanelHeight => _settings.PanelWidthM * Panel.Height / Panel.Width;

    public void Update(TrackingFrame f)
    {
        var watch = f.Grip(_settings.WatchHand);
        if (watch is { } w && f.Head is { } h && _settings.AutoUpright) Level(h, w);
        Pose? panel = watch is { } g ? g.Compose(PanelOffset) : null;

        UpdateVisibility(f.Head, panel);
        TrackLegibility(f.Head, panel);
        UpdateTouch(f, panel);
    }

    void UpdateVisibility(Pose? head, Pose? panel)
    {
        long now = Environment.TickCount64;
        bool want;
        switch (_settings.ForceShow ?? _settings.Show)
        {
            case ShowMode.Always:
                want = panel != null;
                break;
            case ShowMode.Hidden:
                want = false;
                break;
            default:
                if (head is not { } h || panel is not { } p)
                {
                    want = false;
                    break;
                }
                bool glancing = Glancing(h, p, _settings.GlanceAngleDeg, 32f);
                // Wider limits to stay open than to open, so the panel does not flicker at the edge.
                bool stillThere = Glancing(h, p, _settings.GlanceAngleDeg + 25f, 50f);
                if (!_visible)
                {
                    _glanceSince = glancing ? (_glanceSince < 0 ? now : _glanceSince) : -1;
                    want = _glanceSince >= 0 && now - _glanceSince > 180;
                }
                else
                {
                    bool keep = stillThere || _panel.Interacting;
                    _awaySince = keep ? -1 : (_awaySince < 0 ? now : _awaySince);
                    want = _awaySince < 0 || now - _awaySince < 450;
                }
                break;
        }
        if (want != _visible)
        {
            if (!want && _shownSince >= 0)
            {
                float avg = MathF.Atan2(_tiltSin, _tiltCos) * 180 / MathF.PI;
                Log.Write($"panel shown {(now - _shownSince) / 1000f:0.0} s, text tilt in view {avg:+0;-0} deg (0 = upright, + = counterclockwise)");
            }
            _shownSince = want ? now : -1;
            _tiltSin = _tiltCos = 0;
            _visible = want;
            _glanceSince = _awaySince = -1;
            if (!want) Release();
        }
    }

    /// <summary>The panel faces the eyes, is close, and the user is looking roughly at it.</summary>
    static bool Glancing(Pose head, Pose panel, float faceDeg, float lookDeg)
    {
        Vector3 toHead = head.Position - panel.Position;
        float dist = toHead.Length();
        if (dist < 0.08f || dist > 0.75f) return false;
        toHead /= dist;
        Vector3 normal = panel.Rotate(Vector3.UnitZ);
        if (Vector3.Dot(normal, toHead) < MathF.Cos(faceDeg * MathF.PI / 180f)) return false;
        return Vector3.Dot(head.Forward, -toHead) >= MathF.Cos(lookDeg * MathF.PI / 180f);
    }

    /// <summary>
    /// Turns the panel in its plane until its text is upright as the user sees it, so it reads like a
    /// watch however the arm is held. Eased, and frozen while the panel is edge-on or being used.
    /// </summary>
    void Level(Pose head, Pose grip)
    {
        var panel = grip.Compose(PanelOffset);
        Vector3 toHead = head.Position - panel.Position;
        if (toHead.LengthSquared() < 1e-4f) return;
        Vector3 view = Vector3.Normalize(-toHead);
        if (Vector3.Dot(panel.Rotate(Vector3.UnitZ), -view) < 0.4f) return;
        if (_panel.Interacting && _visible) return;  // never move a button out from under a finger
        float tilt = TextTilt(head, panel, view);
        if (float.IsNaN(tilt)) return;
        // While hidden, snap so the panel opens already level; while shown, ease to avoid swimming.
        _autoRoll -= _visible ? tilt * 0.25f : tilt;
        _autoRoll = MathF.IEEERemainder(_autoRoll, MathF.Tau);
    }

    /// <summary>Angle of the panel's text from upright in the user's view, radians, + = counterclockwise.</summary>
    static float TextTilt(Pose head, Pose panel, Vector3 view)
    {
        Vector3 Flat(Vector3 v) => v - view * Vector3.Dot(v, view);
        Vector3 headUp = Flat(head.Up), panelUp = Flat(panel.Up);
        if (headUp.LengthSquared() < 1e-6f || panelUp.LengthSquared() < 1e-6f) return float.NaN;
        headUp = Vector3.Normalize(headUp);
        panelUp = Vector3.Normalize(panelUp);
        return MathF.Atan2(Vector3.Dot(Vector3.Cross(headUp, panelUp), -view), Vector3.Dot(headUp, panelUp));
    }

    long _lastDiagnosis;

    /// <summary>Once a second while the panel is open: what the input side sees, for the log.</summary>
    void Diagnose(string what)
    {
        long now = Environment.TickCount64;
        if (now - _lastDiagnosis < 1000) return;
        _lastDiagnosis = now;
        Log.Write("input: " + what);
    }

    long _shownSince = -1;
    float _tiltSin, _tiltCos;

    /// <summary>
    /// Accumulates how far the panel's text is rotated as the user sees it, logged when the panel
    /// closes. A field check on the wrist orientation that needs nothing from the user.
    /// </summary>
    void TrackLegibility(Pose? head, Pose? panel)
    {
        if (!_visible || head is not { } h || panel is not { } p) return;
        float angle = TextTilt(h, p, Vector3.Normalize(p.Position - h.Position));
        if (float.IsNaN(angle)) return;
        _tiltSin += MathF.Sin(angle);
        _tiltCos += MathF.Cos(angle);
    }

    void UpdateTouch(TrackingFrame f, Pose? panel)
    {
        if (!_visible || panel is not { } p || f.Aim(_settings.PokeHand) is not { } aim)
        {
            if (_visible) Diagnose($"no {_settings.PokeHand.ToString().ToLowerInvariant()} hand aim pose");
            Release();
            return;
        }
        float hw = PanelWidth / 2, hh = PanelHeight / 2;
        Vector3 tip = p.InverseTransform(aim.Transform(new Vector3(0, 0, -_settings.FingertipCm / 100f)));
        Vector3 origin = p.InverseTransform(aim.Position);
        Vector3 dir = Vector3.Transform(aim.Forward, Quaternion.Inverse(p.Rotation));

        // One cursor, always where the aim ray crosses the panel. Touch only decides when a press
        // happens. (It used to move the cursor to the tip's straight-down projection, which sits
        // centimetres away from the ray's crossing whenever the controller is at a slant, so the
        // cursor jumped every time the tip crossed into the touch zone.)
        bool tipNear = MathF.Abs(tip.X) <= hw + 0.01f && MathF.Abs(tip.Y) <= hh + 0.01f && tip.Z <= HoverDepth && tip.Z >= PassThrough;
        bool aimedAt = origin.Z > -0.02f && dir.Z < -0.15f;
        Vector3 at = aimedAt ? origin + dir * (-origin.Z / dir.Z) : default;
        bool onPanel = aimedAt && -origin.Z / dir.Z < 2f && MathF.Abs(at.X) <= hw + 0.02f && MathF.Abs(at.Y) <= hh + 0.02f;
        if (!onPanel && tipNear)
        {
            // Poking side-on with the controller pointing past the panel: fall back to the tip.
            at = tip with { Z = 0 };
            onPanel = true;
        }
        // Keep a drag going when the ray slides off the edge with the button held.
        bool holding = (_triggerDown || _touching) && origin.Z > -0.05f && dir.Z < 0;
        if (!onPanel && holding) at = origin + dir * (-origin.Z / dir.Z);
        if (!onPanel && !holding)
        {
            Diagnose($"laser off panel (aim {origin.Z * 100:0} cm in front, pointing {(dir.Z < -0.15f ? "at" : "away from")} it, " +
                     $"hit at {at.X * 100:0.0},{at.Y * 100:0.0} cm), fingertip {tip.Z * 100:0.0} cm from face, trigger {f.Trigger(_settings.PokeHand):0.00}");
            Release();
            return;
        }

        float trigger = f.Trigger(_settings.PokeHand);
        Diagnose($"laser on panel, trigger {trigger:0.00}, fingertip {tip.Z * 100:0.0} cm from face");
        long now = Environment.TickCount64;
        var raw = ToPixel(at, hw, hh, clamp: false);
        if (!Pointing)
        {
            Pointing = true;
            // A trigger already held when the ray arrives (firing in the game) must be let go first.
            _armed = trigger < TriggerUp;
            _filterX.Reset();
            _filterY.Reset();
            _trail.Clear();
        }
        // Hand tremor is magnified by the lever of a laser: steady it, but stay quick for real moves.
        var steady = new SKPoint(_filterX.Filter(raw.X, now), _filterY.Filter(raw.Y, now));
        _trail.Enqueue((now, steady));
        while (_trail.Count > 0 && now - _trail.Peek().At > 250) _trail.Dequeue();

        // Two ways to press: pull the trigger, or push the controller tip through the panel face.
        bool wasDown = _triggerDown || _touching;
        if (!_armed && trigger < TriggerUp) _armed = true;
        if (_armed && !_triggerDown && trigger >= TriggerDown) _triggerDown = true;
        else if (_triggerDown && trigger < TriggerUp) _triggerDown = false;
        if (!tipNear) _touching = false;
        else if (!_touching && tip.Z <= PressDepth) _touching = true;
        else if (_touching && tip.Z >= ReleaseDepth) _touching = false;
        bool down = _triggerDown || _touching;
        _panel.InputKind = _touching ? "touch" : "trigger";

        // Squeezing a trigger rolls the controller a little, which drags the laser off what it was
        // on. Press where the pointer was just before the squeeze started.
        var point = steady;
        if (down && !wasDown)
            foreach (var (t, earlier) in _trail)
                if (now - t <= ClickLookbackMs) { point = earlier; break; }
        float depth = tipNear ? Math.Clamp(tip.Z / HoverDepth, 0, 1) : 0.5f;
        _panel.Pointer(Clamp(point), depth, down);
    }

    const long ClickLookbackMs = 120;
    readonly OneEuro _filterX = new(), _filterY = new();
    readonly Queue<(long At, SKPoint P)> _trail = new();

    static SKPoint Clamp(SKPoint p) => new(Math.Clamp(p.X, 0, Panel.Width), Math.Clamp(p.Y, 0, Panel.Height));

    /// <summary>
    /// The 1 Euro filter (Casiez et al.): heavy smoothing while the pointer is nearly still, almost
    /// none while it moves fast, which is what a laser needs to feel both steady and responsive.
    /// Values are panel pixels.
    /// </summary>
    sealed class OneEuro
    {
        // Tuned for responsiveness after "laggy" feedback: 1 Hz at rest trailed the hand visibly.
        const float MinCutoff = 2.5f;   // Hz, at rest
        const float Beta = 0.03f;       // how fast the cutoff opens with speed (per px/s)
        const float DerivCutoff = 1.0f;
        float _x, _dx;
        long _at = -1;

        public void Reset() => _at = -1;

        public float Filter(float x, long nowMs)
        {
            if (_at < 0)
            {
                _at = nowMs;
                _x = x;
                _dx = 0;
                return x;
            }
            float dt = Math.Max((nowMs - _at) / 1000f, 0.001f);
            _at = nowMs;
            float dx = (x - _x) / dt;
            _dx += Alpha(DerivCutoff, dt) * (dx - _dx);
            float cutoff = MinCutoff + Beta * MathF.Abs(_dx);
            _x += Alpha(cutoff, dt) * (x - _x);
            return _x;
        }

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2 * MathF.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }

    SKPoint ToPixel(Vector3 local, float hw, float hh, bool clamp = true)
    {
        var p = new SKPoint((local.X + hw) / PanelWidth * Panel.Width, (hh - local.Y) / PanelHeight * Panel.Height);
        return clamp ? Clamp(p) : p;
    }

    void Release()
    {
        _touching = false;
        _triggerDown = false;
        Pointing = false;
        _panel.Pointer(null, 1, false);
    }
}
