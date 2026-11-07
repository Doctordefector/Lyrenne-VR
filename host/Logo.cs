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
            // The small variant scales the lyre up into the space the ring used, as icon-small.svg does.
            canvas.Translate(256, 262);
            canvas.Scale(1.16f);
            canvas.Translate(-256, -262);
        }

        // Strings first so the yoke and soundbox close over their ends.
        using (var strings = new SKPaint { IsAntialias = true, Shader = ivory })
            for (int x = 216; x <= 296; x += 20) canvas.DrawRect(x, 146, 8, 206, strings);

        // Gold rim: the body fattened by a stroke, then covered by the unstroked white body.
        using (var rim = new SKPaint { IsAntialias = true, Shader = edge, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 11, StrokeJoin = SKStrokeJoin.Round })
            foreach (var p in Body) canvas.DrawPath(p, rim);
        using (var fill = new SKPaint { IsAntialias = true, Shader = ivory })
            foreach (var p in Body) canvas.DrawPath(p, fill);

        DrawHeadset(canvas, edge);
        canvas.Restore();
        canvas.Restore();
        edge.Dispose();
        ivory.Dispose();
    }

    /// <summary>The VR twist: a visor across the strings, strapped to both arms.</summary>
    static void DrawHeadset(SKCanvas canvas, SKShader edge)
    {
        // Strap: a thin gold band from arm to arm behind the visor.
        using (var strap = new SKPaint { IsAntialias = true, Shader = edge })
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(150, 214, 362, 226), 4), strap);

        // Visor: a slim dark band, gold rimmed, with a small dip for the nose.
        var visor = SKPath.ParseSvgPathData(
            "M190 202 C 190 194 196 190 204 190 L 308 190 C 316 190 322 194 322 202 L 322 238 " +
            "C 322 246 316 250 308 250 L 278 250 C 270 250 266 242 256 238 C 246 242 242 250 234 250 " +
            "L 204 250 C 196 250 190 246 190 238 Z");
        using (var rim = new SKPaint { IsAntialias = true, Shader = edge, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 9, StrokeJoin = SKStrokeJoin.Round })
            canvas.DrawPath(visor, rim);
        using (var body = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 190), new SKPoint(0, 250),
                new[] { new SKColor(0x26, 0x23, 0x1f), Tile }, SKShaderTileMode.Clamp),
        })
            canvas.DrawPath(visor, body);

        // Two lenses lit in the warm gold, like the screens glowing inside.
        foreach (float cx in new[] { 224f, 288f })
        {
            var oval = new SKRect(cx - 21, 205, cx + 21, 233);
            using var glow = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(cx - 5, 215), 26,
                    new[] { GoldLight, Gold, GoldDark }, new[] { 0f, 0.6f, 1f }, SKShaderTileMode.Clamp),
            };
            canvas.DrawOval(oval, glow);
        }
        // A glint along the top edge so it reads as glass.
        using var glint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, StrokeCap = SKStrokeCap.Round, Color = SKColors.White.WithAlpha(120) };
        canvas.DrawLine(206, 198, 240, 198, glint);
    }

    public static SKImage Render(int size, bool ring)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        Draw(surface.Canvas, size, ring);
        return surface.Snapshot();
    }
}
