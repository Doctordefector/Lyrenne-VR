using System.Diagnostics;
using SkiaSharp;

namespace LyrenneVR;

/// <summary>
/// LyrenneVR.exe --bench: sweeps a pointer across the panel for two seconds at the host's tick
/// rate, the way a hand moves a laser, and reports how many full redraws (each one also a 2 MB
/// upload in the game) that costs and how long a redraw takes.
/// </summary>
static class Bench
{
    public static int Run()
    {
        Log.Enabled = false;
        var settings = new Settings { Lyrics = false };
        var panel = new Panel(new Media(settings), new Volume(), new Lyrics(), settings);
        var np = Demo.Track();
        using var bmp = new SKBitmap(new SKImageInfo(Panel.Width, Panel.Height, SKColorType.Bgra8888, SKAlphaType.Premul));

        const int ticks = 2 * 333;  // two seconds of 3 ms ticks
        int redraws = 0;
        string last = "";
        double renderMs = 0;
        for (int i = 0; i < ticks; i++)
        {
            // A slow sweep across the control row and back, with a little tremor on top.
            float t = i / (float)ticks;
            float x = 60 + 900 * (0.5f - 0.5f * MathF.Cos(t * MathF.Tau));
            float y = 428 + 4 * MathF.Sin(i * 0.9f);
            panel.Pointer(new SKPoint(x, y), 0.5f, false);
            string sig = panel.Signature(np);
            if (sig == last) continue;
            last = sig;
            var sw = Stopwatch.StartNew();
            using (var c = new SKCanvas(bmp)) panel.Render(c, np);
            renderMs += sw.Elapsed.TotalMilliseconds;
            redraws++;
        }
        Console.WriteLine($"redraws in 2 s of cursor movement: {redraws} ({redraws / 2.0:0}/s)");
        Console.WriteLine($"average redraw: {renderMs / Math.Max(redraws, 1):0.00} ms, total {renderMs:0} ms of the 2000");
        Console.WriteLine($"panel data pushed to the game: {redraws * Panel.Width * Panel.Height * 4 / 1048576.0:0} MB");
        return 0;
    }
}
