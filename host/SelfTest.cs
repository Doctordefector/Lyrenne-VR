using System.Numerics;

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
        int taps = 0;
        panel.Haptic += (_, _) => taps++;

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

        Run(head, Poker(370, 414, 0.03f), 50);
        Expect(panel.Interacting && taps == 0, "hovering 3 cm above play shows the cursor without tapping");
        Run(head, Poker(370, 414, -0.004f), 50);
        Expect(taps == 1, "touching play taps once");
        Run(head, Poker(370, 414, -0.01f), 50);
        Expect(taps == 1, "holding the finger in does not repeat");
        Run(head, Poker(370, 414, 0.004f), 50);
        Expect(taps == 1, "tiny lift inside the hysteresis does not re-tap");
        Run(head, Poker(370, 414, 0.03f), 50);
        Run(head, Poker(508, 418, -0.004f), 50);
        Expect(taps == 2, "lifting and touching next taps again");
        Run(head, Poker(508, 418, 0.03f), 50);
        Run(head, Poker(508, 418, -0.09f), 50);
        Expect(taps == 2, "a hand passing straight through does not tap");
        Run(head, Poker(1300, 418, -0.004f), 50);
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
        Run(head, Laser(370, 414), 50);
        Expect(wrist.Pointing && panel.Interacting && taps == 2, "pointing at play from 40 cm shows the cursor and asks for the trigger");
        trigger = 0.9f;
        Run(head, Laser(370, 414), 50);
        Expect(taps == 3, "pulling the trigger clicks");
        Run(head, Laser(370, 414), 100);
        Expect(taps == 3, "holding the trigger does not repeat");
        trigger = 0;
        Run(head, Laser(508, 418), 50);
        trigger = 0.9f;
        Run(head, Laser(508, 418), 50);
        Expect(taps == 4, "release and pull again clicks the next button");
        trigger = 0;
        Run(head, farPoker, 50);
        Expect(!wrist.Pointing, "pointing away gives the trigger back to the game");
        trigger = 0.9f;
        Run(head, Laser(370, 414), 80);
        Expect(taps == 4, "a trigger already held when the ray arrives does not click");
        trigger = 0;
        Run(head, Laser(370, 414), 30);
        trigger = 0.9f;
        Run(head, Laser(370, 414), 50);
        Expect(taps == 5, "after letting go it clicks normally");
        trigger = 0;
        Run(head, farPoker, 30);

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
