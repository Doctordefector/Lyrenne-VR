using SkiaSharp;

namespace LyrenneVR;

/// <summary>
/// Draws the wrist player and turns pointer input (a poking fingertip in VR, the mouse in preview)
/// into player commands. Pure 2D: it knows nothing about poses.
/// </summary>
public sealed class Panel
{
    public const int Width = 1024;
    public const int Height = 512;

    enum Control { None, Shuffle, Previous, Play, Next, Repeat, VolumeDown, Seek, Volume, VolumeUp }

    readonly record struct Target(Control Id, SKRect Rect, bool Drag);

    // Sized for a laser at arm's length: every button is at least 96 px (1.5 cm on a 16 cm panel),
    // the seek bar's grab zone is 78 px tall, and Hit() lets a near miss count.
    static readonly Target[] Targets =
    {
        new(Control.Seek, new SKRect(24, 266, 1000, 344), true),
        new(Control.Shuffle, new SKRect(16, 364, 112, 492), false),
        new(Control.Previous, new SKRect(116, 364, 228, 492), false),
        new(Control.Play, new SKRect(232, 358, 376, 498), false),
        new(Control.Next, new SKRect(380, 364, 492, 492), false),
        new(Control.Repeat, new SKRect(496, 364, 592, 492), false),
        new(Control.VolumeDown, new SKRect(604, 372, 694, 484), false),
        new(Control.Volume, new SKRect(696, 372, 902, 484), true),
        new(Control.VolumeUp, new SKRect(904, 372, 1008, 484), false),
    };

    /// <summary>Where a control sits on the panel, for tests and anything that aims at one.</summary>
    public static SKPoint CenterOf(string control)
    {
        var t = Targets.First(x => x.Id.ToString() == control);
        return new SKPoint(t.Rect.MidX, t.Rect.MidY);
    }

    // A miss by up to this many pixels still lands on the nearest control: at arm's length a laser
    // wobbles by about that much, and the empty space between buttons does nothing useful anyway.
    const float Magnet = 34;

    static Target Hit(SKPoint p)
    {
        Target best = default;
        float bestDist = Magnet;
        foreach (var t in Targets)
        {
            if (t.Rect.Contains(p)) return t;
            float dx = Math.Max(Math.Max(t.Rect.Left - p.X, 0), p.X - t.Rect.Right);
            float dy = Math.Max(Math.Max(t.Rect.Top - p.Y, 0), p.Y - t.Rect.Bottom);
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < bestDist)
            {
                bestDist = d;
                best = t;
            }
        }
        return best;
    }

    readonly Media _media;
    readonly Volume _volume;
    readonly Lyrics _lyrics;
    readonly Settings _settings;

    // Pointer state in panel pixels.
    SKPoint? _hover;
    float _hoverDepth = 1;
    bool _pressed;
    Control _active = Control.None;
    Control _hot = Control.None;  // what a press right now would hit
    float _dragValue;

    float _volumeCache = -1;
    long _volumeReadAt;

    // Artwork derived resources, rebuilt when the track changes.
    SKImage? _artFor;
    SKImage? _blurred;
    SKColor _accent = new(0x8A, 0xB4, 0xFF);

    public event Action<float, float>? Haptic;  // amplitude, seconds

    /// <summary>A control was pressed (its name). Separate from Haptic, which also fires on hover.</summary>
    public event Action<string>? Clicked;

    public Panel(Media media, Volume volume, Lyrics lyrics, Settings settings)
    {
        _media = media;
        _volume = volume;
        _lyrics = lyrics;
        _settings = settings;
    }

    public bool Interacting => _pressed || _hover != null;

    /// <summary>What is driving the pointer, for the log: "touch", "trigger" or "mouse".</summary>
    public string InputKind { get; set; } = "mouse";

    /// <summary>
    /// Feeds the pointer. <paramref name="at"/> is null when nothing hovers. <paramref name="depth"/> is
    /// 0 when touching and 1 at the edge of the hover zone; it only shapes the cursor.
    /// </summary>
    /// <summary>The cursor moved (null when it left the panel). Used by the self-test.</summary>
    public event Action<SKPoint?>? CursorMoved;

    public void Pointer(SKPoint? at, float depth, bool pressed)
    {
        if (at != _hover) CursorMoved?.Invoke(at);
        _hover = at;
        _hoverDepth = Math.Clamp(depth, 0, 1);

        // A light tick whenever the pointer moves onto a different control, so a button can be felt
        // before it is pressed. Not while dragging: the drag owns the pointer then.
        var hot = at is { } a && !_pressed ? Hit(a).Id : _pressed ? _active : Control.None;
        if (hot != _hot && hot != Control.None && !_pressed && InputKind != "mouse") Haptic?.Invoke(0.12f, 0.008f);
        _hot = hot;

        if (pressed && !_pressed && at is { } down)
        {
            var hit = Hit(down);
            _active = hit.Id;
            if (hit.Id != Control.None)
            {
                Haptic?.Invoke(0.35f, 0.02f);
                Log.Write($"click {hit.Id} by {InputKind}");
                Clicked?.Invoke(hit.Id.ToString());
                if (!hit.Drag) Fire(hit.Id);
                else
                {
                    _dragValue = DragValue(hit, down);
                    if (hit.Id == Control.Volume) SetVolume(_dragValue);
                }
            }
        }
        else if (pressed && _pressed && at is { } move && _active is Control.Seek or Control.Volume)
        {
            var t = Targets.First(x => x.Id == _active);
            float before = _dragValue;
            _dragValue = DragValue(t, move);
            if (_active == Control.Volume)
            {
                SetVolume(_dragValue);
                // Detents every 10 %, felt rather than seen.
                if ((int)(before * 10) != (int)(_dragValue * 10)) Haptic?.Invoke(0.1f, 0.006f);
            }
        }
        else if (!pressed && _pressed)
        {
            if (_active == Control.Seek)
            {
                var np = _media.Snapshot();
                if (np.CanSeek) _media.Seek(np.Duration * _dragValue);
                Haptic?.Invoke(0.25f, 0.015f);
            }
            _active = Control.None;
        }
        _pressed = pressed;
    }

    static float DragValue(Target t, SKPoint p)
    {
        // Dead zones at both ends so 0 and 100 % are easy to reach with a wobbly pointer.
        float inset = t.Id == Control.Seek ? 40 : 16;
        return Math.Clamp((p.X - t.Rect.Left - inset) / (t.Rect.Width - inset * 2), 0, 1);
    }

    void Fire(Control c)
    {
        switch (c)
        {
            case Control.Play: _media.TogglePlay(); break;
            case Control.Next: _media.Next(); break;
            case Control.Previous: _media.Previous(); break;
            case Control.Shuffle: _media.ToggleShuffle(); break;
            case Control.Repeat: _media.CycleRepeat(); break;
            // Whole steps of 10 %, snapping an odd level onto the grid first.
            case Control.VolumeDown: SetVolume(MathF.Ceiling(ReadVolume() * 10 - 0.05f) / 10 - 0.1f); break;
            case Control.VolumeUp: SetVolume(MathF.Floor(ReadVolume() * 10 + 0.05f) / 10 + 0.1f); break;
        }
    }

    void SetVolume(float v)
    {
        v = Math.Clamp(v, 0, 1);
        _volume.Set(v);
        _volumeCache = v;
        _volumeReadAt = Environment.TickCount64;
    }

    float ReadVolume()
    {
        if (_volumeCache < 0 || Environment.TickCount64 - _volumeReadAt > 1000)
        {
            _volumeCache = _volume.Get();
            _volumeReadAt = Environment.TickCount64;
        }
        return _volumeCache;
    }

    /// <summary>A string that changes whenever the picture would, so the host only renders when needed.</summary>
    public string Signature(NowPlaying np)
    {
        var (line, _) = _settings.Lyrics ? _lyrics.At(np) : (null, null);
        int progress = np.Duration > TimeSpan.Zero ? (int)(np.Position.TotalMilliseconds / np.Duration.TotalMilliseconds * 960) : 0;
        // The cursor is not part of the picture in VR (it is its own quad), so only what it is over
        // matters here. Keying on its position re-rendered the whole panel on every hand tremor.
        string hover = _hover is { } h && CursorInTexture ? $"{(int)h.X / 3},{(int)h.Y / 3},{(int)(_hoverDepth * 12)}"
            : _hover != null ? "on" : "-";
        return string.Join('|', np.Title, np.Artist, np.Playing, np.Shuffle, np.Repeat, (int)np.Position.TotalSeconds, progress,
            np.Art?.UniqueId ?? 0, line, hover, _pressed, _active, _hot, (int)(_dragValue * 400), (int)(ReadVolume() * 200));
    }

    /// <summary>Draw the cursor into the panel picture (an older layer that cannot draw it itself).</summary>
    public bool CursorInTexture { get; set; }

    /// <summary>The cursor in panel pixels and how close the fingertip is (0 touching, 1 far), or null.</summary>
    public (SKPoint At, float Depth, bool Pressed)? Cursor => _hover is { } h ? (h, _hoverDepth, _pressed) : null;

    /// <param name="drawCursor">Only for the desktop preview. In VR the cursor is a separate quad.</param>
    public void Render(SKCanvas canvas, NowPlaying np, bool drawCursor = false)
    {
        canvas.Clear(SKColors.Transparent);
        UpdateArtwork(np.Art);

        var card = new SKRoundRect(new SKRect(0, 0, Width, Height), 48);
        canvas.Save();
        canvas.ClipRoundRect(card, antialias: true);
        // Backdrop and artwork only change with the track, and they are most of a redraw's cost
        // (a full-panel image draw plus the art), so they are drawn once into a cached image.
        if (_backdrop == null || !ReferenceEquals(_backdropArt, np.Art))
        {
            _backdrop?.Dispose();
            _backdropArt = np.Art;
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            DrawBackground(surface.Canvas);
            DrawArt(surface.Canvas, np);
            _backdrop = surface.Snapshot();
        }
        canvas.DrawImage(_backdrop, 0, 0);
        DrawHeader(canvas, np);
        DrawProgress(canvas, np);
        DrawControls(canvas, np);
        if (drawCursor || CursorInTexture) DrawCursor(canvas);
        canvas.Restore();

        using var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.White.WithAlpha(34) };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(1, 1, Width - 1, Height - 1), 47), border);
    }

    void UpdateArtwork(SKImage? art)
    {
        if (ReferenceEquals(art, _artFor)) return;
        _artFor = art;
        _blurred?.Dispose();
        _blurred = null;
        _accent = new SKColor(0x8A, 0xB4, 0xFF);
        if (art == null) return;

        // Blurred, cover-scaled copy for the backdrop.
        var info = new SKImageInfo(Width / 4, Height / 4, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        float scale = Math.Max((float)info.Width / art.Width, (float)info.Height / art.Height) * 1.15f;
        var dst = SKRect.Create((info.Width - art.Width * scale) / 2, (info.Height - art.Height * scale) / 2, art.Width * scale, art.Height * scale);
        using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(10, 10), FilterQuality = SKFilterQuality.Medium })
            c.DrawImage(art, dst, blur);
        _blurred = surface.Snapshot();
        _accent = PickAccent(art);
    }

    /// <summary>The most vivid reasonably bright color in the artwork, nudged to read well on dark.</summary>
    static SKColor PickAccent(SKImage art)
    {
        using var small = new SKBitmap(new SKImageInfo(24, 24, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(small))
        using (var p = new SKPaint { FilterQuality = SKFilterQuality.Low })
            c.DrawImage(art, new SKRect(0, 0, 24, 24), p);
        float bestScore = -1, bestH = 220, bestS = 0.5f;
        foreach (var px in small.Pixels)
        {
            px.ToHsv(out float h, out float s, out float v);
            float score = s / 100f * (0.4f + v / 100f) - (v < 25 ? 1 : 0);
            if (score > bestScore) { bestScore = score; bestH = h; bestS = s; }
        }
        return SKColor.FromHsv(bestH, Math.Clamp(bestS, 30, 60), 100);
    }

    void DrawBackground(SKCanvas canvas)
    {
        var full = new SKRect(0, 0, Width, Height);
        // Solid base so the game never shows through behind the text, artwork or not.
        using (var basePaint = new SKPaint { Color = new SKColor(14, 15, 20) }) canvas.DrawRect(full, basePaint);
        if (_blurred != null)
        {
            using var p = new SKPaint { FilterQuality = SKFilterQuality.High };
            canvas.DrawImage(_blurred, full, p);
        }
        using var shade = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Height),
                new[] { new SKColor(10, 11, 16, 200), new SKColor(10, 11, 16, 236) }, SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(full, shade);
    }

    SKImage? _backdrop;
    SKImage? _backdropArt;

    void DrawArt(SKCanvas canvas, NowPlaying np)
    {
        var artRect = new SKRect(36, 36, 260, 260);
        var artRound = new SKRoundRect(artRect, 26);
        if (np.Art != null)
        {
            canvas.Save();
            canvas.ClipRoundRect(artRound, antialias: true);
            using var p = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            float side = Math.Min(np.Art.Width, np.Art.Height);
            var src = SKRect.Create((np.Art.Width - side) / 2, (np.Art.Height - side) / 2, side, side);
            canvas.DrawImage(np.Art, src, artRect, p);
            canvas.Restore();
        }
        else
        {
            canvas.Save();
            canvas.Translate(artRect.Left, artRect.Top);
            Logo.Draw(canvas, artRect.Width, ring: true);
            canvas.Restore();
        }

    }

    void DrawHeader(SKCanvas canvas, NowPlaying np)
    {
        float x = 292, maxW = Width - x - 40;
        var (line, nextLine) = _settings.Lyrics ? _lyrics.At(np) : (null, null);

        Text.Draw(canvas, np.Title, x, 96, 46, SKFontStyleWeight.Bold, SKColors.White, maxW);
        Text.Draw(canvas, np.Artist, x, 142, 30, SKFontStyleWeight.Medium, SKColors.White.WithAlpha(180), maxW);

        if (line != null || nextLine != null)
        {
            if (!string.IsNullOrWhiteSpace(line)) Text.Draw(canvas, line!, x, 206, 32, SKFontStyleWeight.SemiBold, _accent, maxW);
            else DrawIcon(canvas, Icons.Note, new SKPoint(x + 16, 196), 32, _accent);
            if (!string.IsNullOrWhiteSpace(nextLine)) Text.Draw(canvas, nextLine!, x, 246, 24, SKFontStyleWeight.Normal, SKColors.White.WithAlpha(120), maxW);
        }
        else if (!string.IsNullOrEmpty(np.Album))
        {
            Text.Draw(canvas, np.Album, x, 200, 26, SKFontStyleWeight.Normal, SKColors.White.WithAlpha(130), maxW);
        }
    }

    void DrawProgress(SKCanvas canvas, NowPlaying np)
    {
        var track = Targets.First(t => t.Id == Control.Seek).Rect;
        float left = track.Left + 40, right = track.Right - 40, y = 296;
        bool dragging = _active == Control.Seek && _pressed;
        float frac = dragging ? _dragValue
            : np.Duration > TimeSpan.Zero ? (float)Math.Clamp(np.Position.TotalMilliseconds / np.Duration.TotalMilliseconds, 0, 1) : 0;
        bool hot = dragging || _hot == Control.Seek;
        float thick = hot ? 18 : 12;

        using var bg = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(46) };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(left, y - thick / 2, right, y + thick / 2), thick / 2), bg);
        using var fg = new SKPaint { IsAntialias = true, Color = _accent };
        float px = left + (right - left) * frac;
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(left, y - thick / 2, Math.Max(px, left + thick), y + thick / 2), thick / 2), fg);
        if (hot || np.CanSeek) canvas.DrawCircle(px, y, hot ? 18 : 12, new SKPaint { IsAntialias = true, Color = SKColors.White });

        var shown = dragging ? np.Duration * frac : np.Position;
        Text.Draw(canvas, Format(shown), left, y + 40, 24, SKFontStyleWeight.Medium, SKColors.White.WithAlpha(170), 200);
        if (np.Duration > TimeSpan.Zero)
            Text.Draw(canvas, Format(np.Duration), right, y + 40, 24, SKFontStyleWeight.Medium, SKColors.White.WithAlpha(170), 200, alignRight: true);
        if (dragging)
        {
            // Where the song will jump to, in a bubble above the thumb so it can be read mid-drag.
            var bubble = new SKRoundRect(SKRect.Create(Math.Clamp(px - 64, 8, Width - 136), y - 76, 128, 48), 24);
            using var bp = new SKPaint { IsAntialias = true, Color = SKColors.White };
            canvas.DrawRoundRect(bubble, bp);
            Text.Draw(canvas, Format(shown), bubble.Rect.MidX, bubble.Rect.MidY + 11, 30, SKFontStyleWeight.Bold, new SKColor(12, 12, 18), 120, center: true);
        }
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void DrawControls(SKCanvas canvas, NowPlaying np)
    {
        foreach (var t in Targets)
        {
            if (t.Drag) continue;
            bool hover = _hot == t.Id && !_pressed;
            bool down = _pressed && _active == t.Id;
            var center = new SKPoint(t.Rect.MidX, t.Rect.MidY);

            if (t.Id == Control.Play)
            {
                using var circle = new SKPaint { IsAntialias = true, Color = down ? _accent.WithAlpha(210) : _accent };
                canvas.DrawCircle(center, down ? 58 : hover ? 68 : 64, circle);
                DrawIcon(canvas, np.Playing ? Icons.Pause : Icons.Play, center, 76, new SKColor(12, 12, 18));
                continue;
            }

            if (hover || down)
            {
                using var halo = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(down ? 70 : 36)) };
                canvas.DrawCircle(center, Math.Min(t.Rect.Width, t.Rect.Height) / 2, halo);
            }

            if (t.Id is Control.VolumeDown or Control.VolumeUp)
            {
                using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = SKColors.White.WithAlpha(150) };
                canvas.DrawCircle(center, 34, ring);
                using var bar = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7, StrokeCap = SKStrokeCap.Round, Color = SKColors.White };
                canvas.DrawLine(center.X - 15, center.Y, center.X + 15, center.Y, bar);
                if (t.Id == Control.VolumeUp) canvas.DrawLine(center.X, center.Y - 15, center.X, center.Y + 15, bar);
                continue;
            }

            var (path, on) = t.Id switch
            {
                Control.Shuffle => (Icons.Shuffle, np.Shuffle),
                Control.Previous => (Icons.Previous, true),
                Control.Next => (Icons.Next, true),
                Control.Repeat => (np.Repeat == Repeat.One ? Icons.RepeatOne : Icons.Repeat, np.Repeat != Repeat.Off),
                _ => (null, false),
            };
            if (path == null) continue;
            bool toggle = t.Id is Control.Shuffle or Control.Repeat;
            var color = toggle ? (on ? _accent : SKColors.White.WithAlpha(140)) : SKColors.White;
            float size = t.Id is Control.Previous or Control.Next ? 76 : 56;
            DrawIcon(canvas, path, center, size, color);
            if (toggle && on) canvas.DrawCircle(center.X, t.Rect.Bottom - 10, 5, new SKPaint { IsAntialias = true, Color = _accent });
        }

        // Volume: a chunky bar you can grab anywhere, with the level always shown above it.
        var vt = Targets.First(t => t.Id == Control.Volume);
        float vl = vt.Rect.Left + 16, vr = vt.Rect.Right - 16, vy = vt.Rect.MidY + 12;
        float vol = _active == Control.Volume && _pressed ? _dragValue : ReadVolume();
        bool vhot = _hot == Control.Volume || (_active == Control.Volume && _pressed);
        float th = vhot ? 22 : 16;
        using var vbg = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(46) };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(vl, vy - th / 2, vr, vy + th / 2), th / 2), vbg);
        using var vfg = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(230) };
        float vx = vl + (vr - vl) * vol;
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(vl, vy - th / 2, Math.Max(vx, vl + th), vy + th / 2), th / 2), vfg);
        canvas.DrawCircle(vx, vy, vhot ? 20 : 14, new SKPaint { IsAntialias = true, Color = SKColors.White });
        Text.Draw(canvas, $"{(int)Math.Round(vol * 100)}%", (vl + vr) / 2, vy - 30, vhot ? 30 : 24, SKFontStyleWeight.SemiBold,
            SKColors.White.WithAlpha((byte)(vhot ? 240 : 170)), 160, center: true);
    }

    void DrawCursor(SKCanvas canvas)
    {
        if (_hover is not { } h) return;
        // A ring that tightens as the fingertip approaches, then fills on contact.
        float r = 10 + 22 * _hoverDepth;
        if (_pressed)
        {
            using var dot = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(220) };
            canvas.DrawCircle(h, 10, dot);
            return;
        }
        using var ring = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4,
            Color = SKColors.White.WithAlpha((byte)(230 - 140 * _hoverDepth)),
        };
        canvas.DrawCircle(h, r, ring);
        using var center = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(200) };
        canvas.DrawCircle(h, 4, center);
    }

    static void DrawIcon(SKCanvas canvas, SKPath path, SKPoint center, float size, SKColor color)
    {
        canvas.Save();
        canvas.Translate(center.X - size / 2, center.Y - size / 2);
        canvas.Scale(size / 24f);
        using var p = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawPath(path, p);
        canvas.Restore();
    }
}

/// <summary>Material icon outlines on a 24 unit grid (Apache 2.0).</summary>
static class Icons
{
    public static readonly SKPath Play = SKPath.ParseSvgPathData("M8 5v14l11-7z");
    public static readonly SKPath Pause = SKPath.ParseSvgPathData("M6 19h4V5H6v14zm8-14v14h4V5h-4z");
    public static readonly SKPath Next = SKPath.ParseSvgPathData("M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z");
    public static readonly SKPath Previous = SKPath.ParseSvgPathData("M6 6h2v12H6zm3.5 6l8.5 6V6z");
    public static readonly SKPath Shuffle = SKPath.ParseSvgPathData(
        "M10.59 9.17L5.41 4 4 5.41l5.17 5.17 1.42-1.41zM14.5 4l2.04 2.04L4 18.59 5.41 20 17.96 7.46 20 9.5V4h-5.5zm.33 9.41l-1.41 1.41 3.13 3.13L14.5 20H20v-5.5l-2.04 2.04-3.13-3.13z");
    public static readonly SKPath Repeat = SKPath.ParseSvgPathData("M7 7h10v3l4-4-4-4v3H5v6h2V7zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2v4z");
    public static readonly SKPath RepeatOne = SKPath.ParseSvgPathData(
        "M7 7h10v3l4-4-4-4v3H5v6h2V7zm10 10H7v-3l-4 4 4 4v-3h12v-6h-2v4zm-4-2V9h-1l-2 1v1h1.5v4H13z");
    public static readonly SKPath VolumeUp = SKPath.ParseSvgPathData(
        "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");
    public static readonly SKPath VolumeOff = SKPath.ParseSvgPathData(
        "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z");
    public static readonly SKPath Note = SKPath.ParseSvgPathData("M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z");
}

/// <summary>Text with per-character font fallback (CJK, emoji) and ellipsizing.</summary>
static class Text
{
    static readonly Dictionary<(SKFontStyleWeight, int), SKTypeface> Primary = new();
    static readonly Dictionary<(int, SKFontStyleWeight), SKTypeface?> Fallback = new();

    static SKTypeface Face(SKFontStyleWeight weight)
    {
        var key = (weight, 0);
        if (!Primary.TryGetValue(key, out var tf))
        {
            var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            tf = SKTypeface.FromFamilyName("Segoe UI Variable Display", style);
            if (tf == null || !tf.FamilyName.StartsWith("Segoe UI Variable", StringComparison.Ordinal))
                tf = SKTypeface.FromFamilyName("Segoe UI", style) ?? SKTypeface.Default;
            Primary[key] = tf;
        }
        return tf;
    }

    static SKTypeface FaceFor(int codepoint, SKFontStyleWeight weight)
    {
        var primary = Face(weight);
        if (primary.ContainsGlyph(codepoint)) return primary;
        var key = (codepoint, weight);
        if (!Fallback.TryGetValue(key, out var tf))
        {
            tf = SKFontManager.Default.MatchCharacter(null, new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright),
                null, codepoint);
            Fallback[key] = tf;
        }
        return tf ?? primary;
    }

    readonly record struct Run(string Text, SKTypeface Face);

    static List<Run> Runs(string s, SKFontStyleWeight weight)
    {
        var runs = new List<Run>();
        var sb = new System.Text.StringBuilder();
        SKTypeface? current = null;
        for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
        {
            int cp = char.ConvertToUtf32(s, i);
            var face = FaceFor(cp, weight);
            if (current != null && face != current)
            {
                runs.Add(new Run(sb.ToString(), current));
                sb.Clear();
            }
            current = face;
            sb.Append(char.ConvertFromUtf32(cp));
        }
        if (current != null && sb.Length > 0) runs.Add(new Run(sb.ToString(), current));
        return runs;
    }

    static float Measure(List<Run> runs, float size)
    {
        float w = 0;
        foreach (var r in runs)
        {
            using var paint = new SKPaint { Typeface = r.Face, TextSize = size };
            w += paint.MeasureText(r.Text);
        }
        return w;
    }

    public static void Draw(SKCanvas canvas, string text, float x, float baseline, float size, SKFontStyleWeight weight, SKColor color,
        float maxWidth, bool alignRight = false, bool center = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (Primary)
        {
            var runs = Runs(text, weight);
            if (Measure(runs, size) > maxWidth)
            {
                // Trim by text elements so we never split a surrogate pair or a combining sequence.
                var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
                var parts = new List<string>();
                while (elements.MoveNext()) parts.Add((string)elements.Current);
                int lo = 0, hi = parts.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    var candidate = Runs(string.Concat(parts.Take(mid)).TrimEnd() + "…", weight);
                    if (Measure(candidate, size) <= maxWidth) lo = mid; else hi = mid - 1;
                }
                runs = Runs(string.Concat(parts.Take(lo)).TrimEnd() + "…", weight);
            }
            float width = Measure(runs, size);
            float cx = alignRight ? x - width : center ? x - width / 2 : x;
            foreach (var r in runs)
            {
                using var paint = new SKPaint { IsAntialias = true, SubpixelText = true, LcdRenderText = false, Color = color, Typeface = r.Face, TextSize = size };
                canvas.DrawText(r.Text, cx, baseline, paint);
                cx += paint.MeasureText(r.Text);
            }
        }
    }
}
