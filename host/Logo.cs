using SkiaSharp;

namespace LyrenneVR;

/// <summary>
/// The Lyrenne mark (docs/images/logo.svg in the Lyrenne repo, same paths) with a VR twist: the lyre
/// wears a headset, a dark visor with a gold rim and two lit lenses strapped across its strings.
/// Drawn on a 512 unit grid and scaled, so the tray, the window icon and the panel share it.
/// </summary>
public static class Logo
{
    static readonly SKColor Tile = new(0x0a, 0x0a, 0x0a);
    static readonly SKColor GoldLight = new(0xe8, 0xcd, 0x9c);
    static readonly SKColor Gold = new(0xA3, 0x7C, 0x43);
    static readonly SKColor GoldDark = new(0x6d, 0x50, 0x29);
    static readonly SKColor Ivory = SKColors.White;
    static readonly SKColor IvoryDark = new(0xec, 0xe8, 0xe1);

    // The lyre body from the real logo: two arms, the yoke and the soundbox.
    static readonly SKPath[] Body =
    {
        SKPath.ParseSvgPathData("M203 352 C 130 306 122 210 164 134 L 187 145 C 152 214 160 298 222 340 Z"),
        SKPath.ParseSvgPathData("M309 352 C 382 306 390 210 348 134 L 325 145 C 360 214 352 298 290 340 Z"),
        SKPath.ParseSvgPathData("M147 133 L365 133 L373 146 L365 161 L147 161 L139 146 Z"),
        SKPath.ParseSvgPathData("M196 330 C 220 344 292 344 316 330 C 314 384 292 404 256 404 C 220 404 198 384 196 330 Z"),
    };

    /// <summary>Draws the mark into a size x size square at the canvas origin.</summary>
    /// <param name="ring">The gold ring. Off for small sizes, where it is most of the pixels
    /// and the icon reads as a gold box (the same rule the Lyrenne app icons follow).</param>
    public static void Draw(SKCanvas canvas, float size, bool ring = true)
    {
        canvas.Save();
        canvas.Scale(size / 512f);

        var edge = SKShader.CreateLinearGradient(new SKPoint(60, 40), new SKPoint(452, 472),
            new[] { GoldLight, Gold, GoldDark }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp);
        var ivory = SKShader.CreateLinearGradient(new SKPoint(0, 128), new SKPoint(0, 408),
            new[] { Ivory, IvoryDark }, SKShaderTileMode.Clamp);

        using (var tile = new SKPaint { IsAntialias = true, Color = Tile })
            canvas.DrawRoundRect(new SKRoundRect(ring ? new SKRect(10, 10, 502, 502) : new SKRect(0, 0, 512, 512), ring ? 104 : 112), tile);
        if (ring)
        {
            using var r = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 9, Shader = edge };
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(15, 15, 497, 497), 99), r);
        }

        canvas.Save();
        if (!ring)
        {
            // Without the ring there is room to spare: use it, as icon-small.svg does for the lyre.
            canvas.Translate(256, 256);
            canvas.Scale(1.12f);
            canvas.Translate(-256, -256);
        }
        DrawHeadsetMark(canvas, edge, ivory);
        canvas.Restore();
        canvas.Restore();
        edge.Dispose();
        ivory.Dispose();
    }

    // A front-on VR headset: ivory shell with a dip for the nose. Wide and short on purpose, so at
    // tray size it reads as a headset and cannot be mistaken for the tall Lyrenne lyre.
    static readonly SKPath Shell = SKPath.ParseSvgPathData(
        "M164 168 L348 168 C381 168 408 195 408 228 L408 284 C408 317 381 344 348 344 L302 344 " +
        "C290 344 282 333 276 323 C270 312 264 304 256 304 C248 304 242 312 236 323 C230 333 222 344 210 344 " +
        "L164 344 C131 344 104 317 104 284 L104 228 C104 195 131 168 164 168 Z");

    /// <summary>The Lyrenne VR mark: a headset whose dark faceplate carries the Lyrenne lyre in gold.</summary>
    static void DrawHeadsetMark(SKCanvas canvas, SKShader edge, SKShader ivory)
    {
        // Strap, behind the shell, out to both sides.
        using (var strap = new SKPaint { IsAntialias = true, Shader = edge })
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(60, 230, 452, 268), 19), strap);

        // Shell: gold rim first, the ivory body over it, so gold only shows around the outside
        // (the same one-silhouette trick the Lyrenne logo uses).
        using (var rim = new SKPaint { IsAntialias = true, Shader = edge, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 14, StrokeJoin = SKStrokeJoin.Round })
            canvas.DrawPath(Shell, rim);
        using (var body = new SKPaint { IsAntialias = true, Shader = ivory })
            canvas.DrawPath(Shell, body);

        // Faceplate: the dark glass front, framed by the shell.
        var plate = new SKRoundRect(new SKRect(130, 190, 382, 290), 38);
        using (var glass = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 190), new SKPoint(0, 290),
                new[] { new SKColor(0x2a, 0x26, 0x22), Tile }, SKShaderTileMode.Clamp),
        })
            canvas.DrawRoundRect(plate, glass);

        // The Lyrenne lyre on the faceplate, in gold: the same paths as the app logo, scaled down.
        canvas.Save();
        canvas.Translate(256, 240);
        canvas.Scale(0.3f);
        canvas.Translate(-256, -268);
        using (var gold = new SKPaint { IsAntialias = true, Shader = edge })
        {
            for (int x = 216; x <= 296; x += 20) canvas.DrawRect(x, 146, 8, 206, gold);
            foreach (var path in Body) canvas.DrawPath(path, gold);
        }
        canvas.Restore();

        // A soft reflection across the glass so it reads as a lens at a glance.
        canvas.Save();
        canvas.ClipRoundRect(plate, antialias: true);
        using (var shine = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(130, 190), new SKPoint(230, 290),
                new[] { SKColors.White.WithAlpha(60), SKColors.White.WithAlpha(0) }, SKShaderTileMode.Clamp),
        })
            canvas.DrawPath(SKPath.ParseSvgPathData("M130 190 L250 190 L160 290 L130 290 Z"), shine);
        canvas.Restore();
    }

    public static SKImage Render(int size, bool ring)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        Draw(surface.Canvas, size, ring);
        return surface.Snapshot();
    }
}
