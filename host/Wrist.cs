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

    /// <summary>Panel pose relative to the watch hand's grip.</summary>
    public Pose PanelOffset => _settings.PanelOffset;

    public float PanelWidth => _settings.PanelWidthM;
    public float PanelHeight => _settings.PanelWidthM * Panel.Height / Panel.Width;

    public void Update(TrackingFrame f)
    {
        var watch = f.Grip(_settings.WatchHand);
        Pose? panel = watch is { } g ? g.Compose(PanelOffset) : null;

        UpdateVisibility(f.Head, panel);
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

    void UpdateTouch(TrackingFrame f, Pose? panel)
    {
        if (!_visible || panel is not { } p || f.Aim(_settings.PokeHand) is not { } aim)
        {
            Release();
            return;
        }
        float hw = PanelWidth / 2, hh = PanelHeight / 2;

        // Finger touch: the controller tip near the panel face.
        Vector3 tip = p.InverseTransform(aim.Transform(new Vector3(0, 0, -_settings.FingertipCm / 100f)));
        bool tipOver = MathF.Abs(tip.X) <= hw + 0.01f && MathF.Abs(tip.Y) <= hh + 0.01f;
        if (tipOver && tip.Z <= HoverDepth && tip.Z >= PassThrough && !_triggerDown)
        {
            Pointing = false;
            if (!_touching && tip.Z <= PressDepth) _touching = true;
            else if (_touching && tip.Z >= ReleaseDepth) _touching = false;
            _panel.Pointer(ToPixel(tip, hw, hh), Math.Clamp(tip.Z / HoverDepth, 0, 1), _touching);
            return;
        }
        _touching = false;

        // Laser: the poking hand's aim ray hits the panel, the trigger clicks.
        Vector3 origin = p.InverseTransform(aim.Position);
        Vector3 dir = Vector3.Transform(aim.Forward, Quaternion.Inverse(p.Rotation));
        bool hit = false;
        Vector3 at = default;
        if (origin.Z > 0 && dir.Z < -0.15f)
        {
            float t = -origin.Z / dir.Z;
            at = origin + dir * t;
            hit = t < 2f && MathF.Abs(at.X) <= hw + 0.02f && MathF.Abs(at.Y) <= hh + 0.02f;
        }
        // Keep a drag going when the ray slides off the edge with the trigger held.
        if (!hit && !(_triggerDown && origin.Z > 0 && dir.Z < 0))
        {
            Release();
            return;
        }

        float trigger = f.Trigger(_settings.PokeHand);
        if (!Pointing)
        {
            Pointing = true;
            // A trigger already held when the ray arrives (firing in the game) must be let go first.
            _armed = trigger < TriggerUp;
        }
        if (!_armed && trigger < TriggerUp) _armed = true;
        if (_armed && !_triggerDown && trigger >= TriggerDown) _triggerDown = true;
        else if (_triggerDown && trigger < TriggerUp) _triggerDown = false;
        _panel.Pointer(ToPixel(at, hw, hh), 0.5f, _triggerDown);
    }

    SKPoint ToPixel(Vector3 local, float hw, float hh) => new(
        Math.Clamp((local.X + hw) / PanelWidth, 0, 1) * Panel.Width,
        Math.Clamp((hh - local.Y) / PanelHeight, 0, 1) * Panel.Height);

    void Release()
    {
        _touching = false;
        _triggerDown = false;
        Pointing = false;
        _panel.Pointer(null, 1, false);
    }
}
