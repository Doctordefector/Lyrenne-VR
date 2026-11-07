using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LyrenneVR;

public enum ShowMode { Glance, Always, Hidden }

/// <summary>User settings in %LOCALAPPDATA%\LyrenneVR\settings.json. Every field has a working default.</summary>
public sealed class Settings
{
    /// <summary>The hand that wears the player. The other hand pokes it.</summary>
    public Hand WatchHand { get; set; } = Hand.Left;
    public ShowMode Show { get; set; } = ShowMode.Glance;

    public float PanelWidthCm { get; set; } = 16f;
    /// <summary>Panel center in the watch hand's grip space, in cm, mirrored for the right hand:
    /// X out of the back of the hand, Y toward the thumb, Z toward the elbow.</summary>
    public float OffsetOutCm { get; set; } = 3.5f;
    public float OffsetThumbCm { get; set; } = 0f;
    public float OffsetElbowCm { get; set; } = 12f;
    /// <summary>Rolls the panel around the forearm, positive turns it toward the face in the usual glance pose.</summary>
    public float TiltDeg { get; set; } = 15f;
    /// <summary>Extra spin of the panel in its own plane, counterclockwise as you look at it.</summary>
    public float RollDeg { get; set; } = 0f;

    /// <summary>How far ahead of the poking controller's aim origin the virtual fingertip sits, in cm.
    /// Measured on Quest controllers under VDXR: the aim origin sits about 6 cm behind the tip
    /// people actually touch with.</summary>
    public float FingertipCm { get; set; } = 6f;

    /// <summary>Bumped when a default changes in a way saved settings should pick up.</summary>
    public int Version { get; set; } = CurrentVersion;
    const int CurrentVersion = 2;

    public float GlanceAngleDeg { get; set; } = 40f;
    public bool Haptics { get; set; } = true;
    public bool Lyrics { get; set; } = true;
    /// <summary>Only follow Lyrenne's media session instead of whatever Windows considers current.</summary>
    public bool LyrenneOnly { get; set; } = false;
    public bool SteamVrAutoLaunch { get; set; } = true;

    /// <summary>Overrides <see cref="Show"/> for this run only (--always on the command line, used by tests).</summary>
    [JsonIgnore] public ShowMode? ForceShow { get; set; }

    [JsonIgnore] public Hand PokeHand => WatchHand == Hand.Left ? Hand.Right : Hand.Left;

    /// <summary>Panel pose relative to the watch hand's OpenXR grip pose.</summary>
    [JsonIgnore]
    public Pose PanelOffset
    {
        get
        {
            // OpenXR grip: +X right for both hands, -Z along the pointing finger, +Y up the thumb,
            // so the back of the left hand is -X and the forearm runs along +Z. In the check-the-watch
            // pose the text reads across the wrist with its top toward the elbow (confirmed in a
            // headset: reading along the forearm came out 90 degrees off).
            float side = WatchHand == Hand.Left ? -1f : 1f;
            Vector3 normal = new(side, 0, 0);
            Vector3 up = Vector3.UnitZ;
            Vector3 right = Vector3.Cross(up, normal);
            var basis = Pose.FromBasis(right, up, normal);
            var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, TiltDeg * MathF.PI / 180f);
            var roll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, RollDeg * MathF.PI / 180f);
            var position = new Vector3(side * OffsetOutCm, OffsetThumbCm, OffsetElbowCm) / 100f;
            return new Pose(position, Quaternion.Normalize(tilt * basis * roll));
        }
    }

    [JsonIgnore] public float PanelWidthM => Math.Clamp(PanelWidthCm, 6f, 40f) / 100f;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyrenneVR");
    static string FilePath => Path.Combine(Directory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
                if (loaded.Version < 2) loaded.FingertipCm = new Settings().FingertipCm;
                loaded.Version = CurrentVersion;
                return loaded;
            }
        }
        catch (Exception e)
        {
            Log.Write($"settings unreadable, using defaults: {e.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            Log.Write($"could not save settings: {e.Message}");
        }
    }

    public Settings Clone() => (Settings)MemberwiseClone();
}

public static class Log
{
    static readonly object Lock = new();

    public static void Write(string message)
    {
        lock (Lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Settings.Directory);
                string path = Path.Combine(Settings.Directory, "host.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1 << 20) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never take the host down.
            }
        }
    }
}
