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
        Vector3 tip = aim.Transform(new Vector3(0, 0, -_settings.FingertipCm / 100f));
        Vector3 local = p.InverseTransform(tip);  // quad space: X right, Y up, Z out of the face, meters
        float hw = PanelWidth / 2, hh = PanelHeight / 2;
        float margin = 0.01f;
        bool over = MathF.Abs(local.X) <= hw + margin && MathF.Abs(local.Y) <= hh + margin;

        if (!over || local.Z > HoverDepth || local.Z < PassThrough)
        {
            Release();
            return;
        }

        if (!_touching && local.Z <= PressDepth) _touching = true;
        else if (_touching && local.Z >= ReleaseDepth) _touching = false;

        var px = new SKPoint(
            Math.Clamp((local.X + hw) / PanelWidth, 0, 1) * Panel.Width,
            Math.Clamp((hh - local.Y) / PanelHeight, 0, 1) * Panel.Height);
        _panel.Pointer(px, Math.Clamp(local.Z / HoverDepth, 0, 1), _touching);
    }

    void Release()
    {
        _touching = false;
        _panel.Pointer(null, 1, false);
    }
}
