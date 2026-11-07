using System.Numerics;
using SkiaSharp;

namespace LyrenneVR;

/// <summary>
/// LyrenneVR.exe --selftest: drives the wrist logic with synthetic poses (a head, a watch hand and a
/// poking hand) and checks glance-to-open, look-away-to-close, hover, press, release and pass-through.
/// </summary>
static class SelfTest
{
    static int _failures;

    static void Expect(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    public static int Run()
    {
        Log.Enabled = false;
        foreach (var hand in new[] { Hand.Left, Hand.Right }) RunFor(hand);
        Console.WriteLine(_failures == 0 ? "\nall checks passed" : $"\n{_failures} check(s) failed");
        return _failures == 0 ? 0 : 1;
    }

    static void RunFor(Hand watchHand)
    {
        Console.WriteLine($"-- watch on the {watchHand.ToString().ToLowerInvariant()} hand");
        var settings = new Settings { WatchHand = watchHand, Lyrics = false };
        var panel = new Panel(new Media(settings), new Volume(), new Lyrics(), settings);
        var wrist = new Wrist(settings, panel);
        int taps = 0, buzzes = 0;
        string last = "";
        panel.Clicked += c => { taps++; last = c; };
        panel.Haptic += (_, _) => buzzes++;
        var play = Panel.CenterOf("Play");
        var next = Panel.CenterOf("Next");

        // The watch hand somewhere in front of the chest, turned a bit, as in a real glance.
        var grip = new Pose(new Vector3(watchHand == Hand.Left ? -0.12f : 0.12f, 1.25f, -0.32f),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.6f, -0.4f, 0.9f)));
        var panelWorld = grip.Compose(wrist.PanelOffset);
        Vector3 normal = panelWorld.Rotate(Vector3.UnitZ);

        // Head 35 cm in front of the panel face, looking straight at it.
        var headPos = panelWorld.Position + normal * 0.35f;
        var lookAt = LookRotation(-normal);
        var head = new Pose(headPos, lookAt);
        var awayHead = new Pose(headPos, LookRotation(Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY))));
        var farPoker = new Pose(new Vector3(0, 0.8f, 0.2f), Quaternion.Identity);

        float trigger = 0;
        TrackingFrame Frame(Pose h, Pose poker) => watchHand == Hand.Left
            ? new TrackingFrame(h, grip, null, null, poker, 0, trigger)
            : new TrackingFrame(h, null, grip, poker, null, trigger, 0);

        void Run(Pose h, Pose poker, int ms)
        {
            long end = Environment.TickCount64 + ms;
            do
            {
                wrist.Update(Frame(h, poker));
                Thread.Sleep(10);
            } while (Environment.TickCount64 < end);
        }

        Run(awayHead, farPoker, 300);
        Expect(!wrist.Visible, "hidden while looking elsewhere");
        Run(head, farPoker, 60);
        Expect(!wrist.Visible, "a brief glance does not open it");
        Run(head, farPoker, 300);
        Expect(wrist.Visible, "opens when looking at the wrist");

        // Self-levelling: whatever the wrist's roll, the text ends up upright in the user's view.
        panelWorld = grip.Compose(wrist.PanelOffset);
        {
            Vector3 view = Vector3.Normalize(panelWorld.Position - head.Position);
            Vector3 Flat(Vector3 v) => Vector3.Normalize(v - view * Vector3.Dot(v, view));
            float tilt = MathF.Acos(Math.Clamp(Vector3.Dot(Flat(head.Up), Flat(panelWorld.Up)), -1, 1)) * 180 / MathF.PI;
            Expect(tilt < 3, $"text is upright in view (off by {tilt:0.0} deg)");
        }

        // Fingertip on the play button, at a given distance in front of the panel face.
        Pose Poker(float px, float py, float depth)
        {
            float w = settings.PanelWidthM, h = w * Panel.Height / Panel.Width;
            var local = new Vector3((px / Panel.Width - 0.5f) * w, (0.5f - py / Panel.Height) * h, depth);
            Vector3 tip = panelWorld.Transform(local);
            // The aim pose points straight into the panel; the fingertip sits ahead of its origin.
            var rot = LookRotation(-normal);
            return new Pose(tip - Vector3.Transform(new Vector3(0, 0, -settings.FingertipCm / 100f), rot), rot);
        }

        Run(head, Poker(play.X, play.Y, 0.03f), 50);
        Expect(panel.Interacting && taps == 0, "hovering 3 cm above play shows the cursor without tapping");
        Run(head, Poker(play.X, play.Y, -0.004f), 50);
        Expect(taps == 1, "touching play taps once");
        Run(head, Poker(play.X, play.Y, -0.01f), 50);
        Expect(taps == 1, "holding the finger in does not repeat");
        Run(head, Poker(play.X, play.Y, 0.004f), 50);
        Expect(taps == 1, "tiny lift inside the hysteresis does not re-tap");
        Run(head, Poker(play.X, play.Y, 0.03f), 50);
        Run(head, Poker(next.X, next.Y, -0.004f), 50);
        Expect(taps == 2, "lifting and touching next taps again");
        Run(head, Poker(next.X, next.Y, 0.03f), 50);
        Run(head, Poker(next.X, next.Y, -0.09f), 50);
        Expect(taps == 2, "a hand passing straight through does not tap");
        Run(head, Poker(1300, next.Y, -0.004f), 50);
        Expect(taps == 2, "touching beside the panel does nothing");

        // Laser: the poking controller 40 cm in front of the panel, aimed at a button.
        Pose Laser(float px, float py)
        {
            float w = settings.PanelWidthM, h = w * Panel.Height / Panel.Width;
            var target = panelWorld.Transform(new Vector3((px / Panel.Width - 0.5f) * w, (0.5f - py / Panel.Height) * h, 0));
            var from = target + normal * 0.4f + Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY)) * 0.1f;
            return new Pose(from, LookRotation(target - from));
        }
        trigger = 0;
        Run(head, Laser(play.X, play.Y), 50);
        Expect(wrist.Pointing && panel.Interacting && taps == 2, "pointing at play from 40 cm shows the cursor and asks for the trigger");
        trigger = 0.9f;
        Run(head, Laser(play.X, play.Y), 50);
        Expect(taps == 3, "pulling the trigger clicks");
        Run(head, Laser(play.X, play.Y), 100);
        Expect(taps == 3, "holding the trigger does not repeat");
        trigger = 0;
        Run(head, Laser(next.X, next.Y), 50);
        trigger = 0.9f;
        Run(head, Laser(next.X, next.Y), 50);
        Expect(taps == 4, "release and pull again clicks the next button");
        trigger = 0;
        Run(head, farPoker, 50);
        Expect(!wrist.Pointing, "pointing away gives the trigger back to the game");
        trigger = 0.9f;
        Run(head, Laser(play.X, play.Y), 80);
        Expect(taps == 4, "a trigger already held when the ray arrives does not click");
        trigger = 0;
        Run(head, Laser(play.X, play.Y), 30);
        trigger = 0.9f;
        Run(head, Laser(play.X, play.Y), 50);
        Expect(taps == 5, "after letting go it clicks normally");
        trigger = 0;
        Run(head, farPoker, 30);

        // Magnetism: a laser landing just outside the play button (in the gap below it) still clicks it.
        Run(head, Laser(play.X, play.Y + 92), 60);
        trigger = 0.9f;
        Run(head, Laser(play.X, play.Y + 92), 50);
        Expect(taps == 6 && last == "Play", $"a near miss below play still clicks play (got {last})");
        trigger = 0;
        Run(head, farPoker, 30);

        // Hover buzz: sweeping from play to next gives a light tick on arriving at next, no click.
        Run(head, Laser(play.X, play.Y), 60);
        int before = buzzes;
        Run(head, Laser(next.X, next.Y), 120);
        Expect(buzzes > before && taps == 6, "moving onto another button buzzes lightly without clicking");

        // Click drift: the squeeze yanks the laser 90 px sideways, but the click lands where it was.
        Run(head, Laser(play.X, play.Y), 200);
        trigger = 0.9f;
        Run(head, Laser(play.X + 140, play.Y), 12);
        Expect(taps == 7 && last == "Play", $"the click lands where the laser was before the squeeze (got {last})");
        trigger = 0;
        Run(head, farPoker, 30);

        // Slant: aim at play from 45 degrees off, sliding in from 30 cm to 9 cm away. The tip enters
        // the touch zone on the way, but the cursor must stay on the ray's crossing point.
        {
            float w = settings.PanelWidthM, h = w * Panel.Height / Panel.Width;
            var target = panelWorld.Transform(new Vector3((play.X / Panel.Width - 0.5f) * w, (0.5f - play.Y / Panel.Height) * h, 0));
            var side = Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY));
            var slant = Vector3.Normalize(normal + side);
            int clicksBefore = taps;
            var jumps = new List<float>();
            SKPoint? lastCursor = null;
            Action<SKPoint?> track = c => { if (lastCursor is { } l && c is { } n) jumps.Add(SKPoint.Distance(l, n)); lastCursor = c; };
            panel.CursorMoved += track;
            for (float d = 0.30f; d >= 0.09f; d -= 0.01f)
            {
                var from = target + slant * d;
                Run(head, new Pose(from, LookRotation(target - from)), 12);
            }
            panel.CursorMoved -= track;
            float worst = jumps.Count > 0 ? jumps.Max() : 0;
            Expect(worst < 25 && taps == clicksBefore, $"a slanted controller moving in keeps the cursor still (biggest jump {worst:0} px)");
            Run(head, farPoker, 30);
        }

        Run(awayHead, farPoker, 150);
        Expect(wrist.Visible, "stays open through a short look away");
        Run(awayHead, farPoker, 700);
        Expect(!wrist.Visible, "closes after looking away");

        settings.ForceShow = ShowMode.Always;
        Run(awayHead, farPoker, 30);
        Expect(wrist.Visible, "always mode shows it regardless of gaze");
    }

    /// <summary>Rotation whose -Z points along <paramref name="forward"/>, keeping +Y as close to world up as possible.</summary>
    static Quaternion LookRotation(Vector3 forward)
    {
        Vector3 z = -Vector3.Normalize(forward);
        Vector3 up = MathF.Abs(Vector3.Dot(z, Vector3.UnitY)) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 x = Vector3.Normalize(Vector3.Cross(up, z));
        Vector3 y = Vector3.Cross(z, x);
        return Pose.FromBasis(x, y, z);
    }
}
