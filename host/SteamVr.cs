using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LyrenneVR;

/// <summary>
/// SteamVR backend: a regular OpenVR overlay attached to the controller, for every game that runs
/// through SteamVR (OpenVR games and OpenXR games on the SteamVR runtime). Only connects while
/// SteamVR is already running, so it never launches SteamVR by itself.
/// </summary>
public sealed class SteamVr : IDisposable
{
    const string AppKey = "lyrenne.wristplayer";

    readonly Settings _settings;
    bool _connected;
    long _nextAttempt;
    ulong _overlay, _cursorOverlay;
    bool _cursorShown;
    ulong _set, _leftGrip, _rightGrip, _leftAim, _rightAim, _haptic, _leftSource, _rightSource, _leftTrigger, _rightTrigger;
    readonly TrackedDevicePose_t[] _devicePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    uint _watchDevice = OpenVR.k_unTrackedDeviceIndexInvalid;
    Pose _watchDeviceFromGrip = Pose.Identity;
    bool _shown;
    bool _stale;
    long _bindingReportAt;

    ID3D11Device? _device;
    ID3D11DeviceContext? _context;
    ID3D11Texture2D? _texture;
    byte[] _unpremul = Array.Empty<byte>();

    public SteamVr(Settings settings) => _settings = settings;

    public bool Connected => _connected;

    /// <summary>Off for tests, so a test run never adds itself to the user's SteamVR startup list.</summary>
    public bool RegisterWithSteamVr { get; init; } = true;

    static bool SteamVrRunning()
    {
        var p = Process.GetProcessesByName("vrserver");
        foreach (var x in p) x.Dispose();
        return p.Length > 0;
    }

    /// <summary>Connects when SteamVR is up. Cheap to call every tick.</summary>
    public bool EnsureConnected()
    {
        if (_connected) return true;
        long now = Environment.TickCount64;
        if (now < _nextAttempt) return false;
        _nextAttempt = now + 3000;
        if (!SteamVrRunning()) return false;

        var error = EVRInitError.None;
        OpenVR.Init(ref error, EVRApplicationType.VRApplication_Overlay);
        if (error != EVRInitError.None)
        {
            Log.Write($"SteamVR init failed: {error}");
            return false;
        }

        try
        {
            string dir = AppContext.BaseDirectory;
            var inputErr = OpenVR.Input.SetActionManifestPath(Path.Combine(dir, "steamvr", "actions.json"));
            if (inputErr != EVRInputError.None) Log.Write($"SteamVR action manifest: {inputErr}");
            OpenVR.Input.GetActionSetHandle("/actions/lyrenne", ref _set);
            OpenVR.Input.GetActionHandle("/actions/lyrenne/in/left_grip", ref _leftGrip);
            OpenVR.Input.GetActionHandle("/actions/lyrenne/in/right_grip", ref _rightGrip);
            OpenVR.Input.GetActionHandle("/actions/lyrenne/in/left_aim", ref _leftAim);
            OpenVR.Input.GetActionHandle("/actions/lyrenne/in/right_aim", ref _rightAim);
            OpenVR.Input.GetActionHandle("/actions/lyrenne/out/haptic", ref _haptic);
            OpenVR.Input.GetActionHandle("/actions/lyrenne/in/left_trigger", ref _leftTrigger);
            OpenVR.Input.GetActionHandle("/actions/lyrenne/in/right_trigger", ref _rightTrigger);
            OpenVR.Input.GetInputSourceHandle("/user/hand/left", ref _leftSource);
            OpenVR.Input.GetInputSourceHandle("/user/hand/right", ref _rightSource);

            var err = OpenVR.Overlay.CreateOverlay(AppKey, "Lyrenne", ref _overlay);
            if (err != EVROverlayError.None)
            {
                Log.Write($"SteamVR overlay creation failed: {err}");
                Disconnect();
                return false;
            }
            OpenVR.Overlay.SetOverlayWidthInMeters(_overlay, _settings.PanelWidthM);
            OpenVR.Overlay.SetOverlaySortOrder(_overlay, 100);
            CreateCursorOverlay();

            if (RegisterWithSteamVr) RegisterManifest(dir);
            _connected = true;
            _shown = false;
            _bindingReportAt = Environment.TickCount64 + 5000;
            Log.Write("connected to SteamVR");
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"SteamVR setup failed: {e.Message}");
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// The cursor is its own small overlay so moving it is a transform change, not a re-upload of
    /// the whole panel texture. Its 64 px ring is uploaded once.
    /// </summary>
    void CreateCursorOverlay()
    {
        if (OpenVR.Overlay.CreateOverlay(AppKey + ".cursor", "Lyrenne cursor", ref _cursorOverlay) != EVROverlayError.None)
        {
            _cursorOverlay = 0;
            return;
        }
        OpenVR.Overlay.SetOverlaySortOrder(_cursorOverlay, 101);
        const int n = 64;
        var px = new byte[n * n * 4];
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float ring = Math.Clamp(1 - MathF.Abs(d - 25.5f) / 3.5f, 0, 1) * 1.6f;
                float dot = Math.Clamp(6.5f - d, 0, 1);
                float white = Math.Clamp(MathF.Max(ring, dot), 0, 1);
                float shadow = Math.Clamp(1 - MathF.Abs(d - 25.5f) / 6f, 0, 1) * 0.45f;
                float a = Math.Clamp(white + shadow * (1 - white), 0, 1);
                // Straight alpha (OpenVR does not premultiply): the color is white over its own alpha.
                byte v = a > 0 ? (byte)Math.Clamp(white / a * 255, 0, 255) : (byte)0;
                int i = (y * n + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = (byte)(a * 255);
            }
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            OpenVR.Overlay.SetOverlayRaw(_cursorOverlay, handle.AddrOfPinnedObject(), n, n, 4);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Lets SteamVR start the player with itself, the way OVR Toolkit and XSOverlay do.</summary>
    void RegisterManifest(string dir)
    {
        try
        {
            string manifest = Path.Combine(Settings.Directory, "lyrenne_vr.vrmanifest");
            Directory.CreateDirectory(Settings.Directory);
            string exe = Environment.ProcessPath ?? Path.Combine(dir, "LyrenneVR.exe");
            File.WriteAllText(manifest, $$"""
                {
                  "source": "builtin",
                  "applications": [{
                    "app_key": "{{AppKey}}",
                    "launch_type": "binary",
                    "binary_path_windows": {{System.Text.Json.JsonSerializer.Serialize(exe)}},
                    "arguments": "--from-steamvr",
                    "is_dashboard_overlay": true,
                    "image_path": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "app.png"))}},
                    "strings": { "en_us": { "name": "Lyrenne Wrist Player", "description": "Control Lyrenne from your wrist in any game" } }
                  }]
                }
                """);
            OpenVR.Applications.AddApplicationManifest(manifest, false);
            OpenVR.Applications.SetApplicationAutoLaunch(AppKey, _settings.SteamVrAutoLaunch);
        }
        catch (Exception e)
        {
            Log.Write($"SteamVR manifest registration failed: {e.Message}");
        }
    }

    public void ApplyAutoLaunch()
    {
        if (_connected) OpenVR.Applications.SetApplicationAutoLaunch(AppKey, _settings.SteamVrAutoLaunch);
    }

    /// <summary>Handles SteamVR events and reads this tick's poses, or null when not connected.</summary>
    public TrackingFrame? Poll()
    {
        if (!_connected) return null;
        var ev = new VREvent_t();
        uint size = (uint)Marshal.SizeOf<VREvent_t>();
        while (OpenVR.System.PollNextEvent(ref ev, size))
        {
            if (ev.eventType == (uint)EVREventType.VREvent_Quit)
            {
                Log.Write("SteamVR is quitting");
                OpenVR.System.AcknowledgeQuit_Exiting();
                Disconnect();
                return null;
            }
        }

        var sets = new[] { new VRActiveActionSet_t { ulActionSet = _set } };
        OpenVR.Input.UpdateActionState(sets, (uint)Marshal.SizeOf<VRActiveActionSet_t>());
        if (_bindingReportAt != 0 && Environment.TickCount64 > _bindingReportAt)
        {
            _bindingReportAt = 0;
            ReportBindings();
        }
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, _devicePoses);

        Pose? head = _devicePoses[0].bPoseIsValid ? ToPose(_devicePoses[0].mDeviceToAbsoluteTracking) : null;
        var (lg, lgDev) = ActionPose(_leftGrip);
        var (rg, rgDev) = ActionPose(_rightGrip);
        var (la, _) = ActionPose(_leftAim);
        var (ra, _) = ActionPose(_rightAim);

        // The overlay is attached to the watch controller's raw device pose, so work out where the
        // grip sits relative to that device. This keeps the panel glued to the hand with no lag.
        var (grip, dev) = _settings.WatchHand == Hand.Left ? (lg, lgDev) : (rg, rgDev);
        if (grip is { } g && dev != OpenVR.k_unTrackedDeviceIndexInvalid && _devicePoses[dev].bPoseIsValid)
        {
            _watchDevice = dev;
            _watchDeviceFromGrip = ToPose(_devicePoses[dev].mDeviceToAbsoluteTracking).Inverse().Compose(g);
        }
        else
        {
            _watchDevice = OpenVR.k_unTrackedDeviceIndexInvalid;
        }
        return new TrackingFrame(head, lg, rg, la, ra, Analog(_leftTrigger), Analog(_rightTrigger));
    }

    /// <summary>
    /// Logs which physical inputs SteamVR bound each action to, once per connection. An action with
    /// no origins is not bound on the connected controllers, which is what broke the trigger on
    /// Virtual Desktop's controller type.
    /// </summary>
    void ReportBindings()
    {
        foreach (var (name, action) in new[] { ("left grip", _leftGrip), ("right aim", _rightAim), ("left trigger", _leftTrigger), ("right trigger", _rightTrigger) })
        {
            var origins = new ulong[8];
            OpenVR.Input.GetActionOrigins(_set, action, origins);
            var names = new List<string>();
            foreach (var o in origins)
            {
                if (o == 0) continue;
                var sb = new System.Text.StringBuilder(256);
                OpenVR.Input.GetOriginLocalizedName(o, sb, 256, -1);
                names.Add(sb.ToString());
            }
            Log.Write($"SteamVR {name}: {(names.Count == 0 ? "NOT BOUND" : string.Join(", ", names))}");
        }
    }

    static float Analog(ulong action)
    {
        var data = new InputAnalogActionData_t();
        var err = OpenVR.Input.GetAnalogActionData(action, ref data, (uint)Marshal.SizeOf<InputAnalogActionData_t>(), OpenVR.k_ulInvalidInputValueHandle);
        return err == EVRInputError.None && data.bActive ? data.x : 0;
    }

    (Pose?, uint) ActionPose(ulong action)
    {
        var data = new InputPoseActionData_t();
        var err = OpenVR.Input.GetPoseActionDataRelativeToNow(action, ETrackingUniverseOrigin.TrackingUniverseStanding, 0, ref data,
            (uint)Marshal.SizeOf<InputPoseActionData_t>(), OpenVR.k_ulInvalidInputValueHandle);
        if (err != EVRInputError.None || !data.bActive || !data.pose.bPoseIsValid) return (null, OpenVR.k_unTrackedDeviceIndexInvalid);
        var info = new InputOriginInfo_t();
        uint device = OpenVR.k_unTrackedDeviceIndexInvalid;
        if (OpenVR.Input.GetOriginTrackedDeviceInfo(data.activeOrigin, ref info, (uint)Marshal.SizeOf<InputOriginInfo_t>()) == EVRInputError.None)
            device = info.trackedDeviceIndex;
        return (ToPose(data.pose.mDeviceToAbsoluteTracking), device);
    }

    public void Present(bool visible, Pose panelOffset, float widthMeters, ReadOnlySpan<byte> premulBgra, bool frameChanged,
        (float U, float V, float Size)? cursor = null)
    {
        if (!_connected) return;
        bool show = visible && _watchDevice != OpenVR.k_unTrackedDeviceIndexInvalid;
        // Frames drawn while hidden are uploaded the moment the panel shows.
        if (frameChanged) _stale = true;
        if (show)
        {
            if (_stale || _texture == null)
            {
                Upload(premulBgra);
                _stale = false;
            }
            var deviceToPanel = _watchDeviceFromGrip.Compose(panelOffset);
            var m = ToMatrix(deviceToPanel);
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_overlay, _watchDevice, ref m);
            OpenVR.Overlay.SetOverlayWidthInMeters(_overlay, widthMeters);
        }
        if (show != _shown)
        {
            if (show) OpenVR.Overlay.ShowOverlay(_overlay);
            else OpenVR.Overlay.HideOverlay(_overlay);
            _shown = show;
        }

        bool showCursor = show && cursor != null && _cursorOverlay != 0;
        if (showCursor && cursor is { } c)
        {
            float h = widthMeters * Panel.Height / Panel.Width;
            var onPanel = new Pose(new Vector3((c.U - 0.5f) * widthMeters, (0.5f - c.V) * h, 0.002f), Quaternion.Identity);
            var m = ToMatrix(_watchDeviceFromGrip.Compose(panelOffset).Compose(onPanel));
            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_cursorOverlay, _watchDevice, ref m);
            OpenVR.Overlay.SetOverlayWidthInMeters(_cursorOverlay, c.Size);
        }
        if (showCursor != _cursorShown && _cursorOverlay != 0)
        {
            if (showCursor) OpenVR.Overlay.ShowOverlay(_cursorOverlay);
            else OpenVR.Overlay.HideOverlay(_cursorOverlay);
            _cursorShown = showCursor;
        }
    }

    void Upload(ReadOnlySpan<byte> premul)
    {
        if (_device == null)
        {
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 }, out _device, out _context);
            if (result.Failure || _device == null)
            {
                Log.Write($"D3D11 device for the SteamVR overlay failed: {result}");
                return;
            }
            _texture = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, Panel.Width, Panel.Height, 1, 1,
                BindFlags.ShaderResource));
        }

        // OpenVR overlays blend with straight alpha.
        if (_unpremul.Length != premul.Length) _unpremul = new byte[premul.Length];
        for (int i = 0; i < premul.Length; i += 4)
        {
            byte a = premul[i + 3];
            if (a == 0)
            {
                _unpremul[i] = _unpremul[i + 1] = _unpremul[i + 2] = _unpremul[i + 3] = 0;
                continue;
            }
            _unpremul[i] = (byte)Math.Min(255, premul[i] * 255 / a);
            _unpremul[i + 1] = (byte)Math.Min(255, premul[i + 1] * 255 / a);
            _unpremul[i + 2] = (byte)Math.Min(255, premul[i + 2] * 255 / a);
            _unpremul[i + 3] = a;
        }
        _context!.UpdateSubresource(_unpremul, _texture!, 0, Panel.Width * 4, 0);
        _context.Flush();

        var tex = new Texture_t { handle = _texture!.NativePointer, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Gamma };
        var err = OpenVR.Overlay.SetOverlayTexture(_overlay, ref tex);
        if (err != EVROverlayError.None) Log.Write($"SetOverlayTexture: {err}");
    }

    /// <summary>
    /// Test hook for --steamvr-test: shows a frame 60 cm in front of the headset and reports what
    /// SteamVR says about the overlay. Works with no controllers (SteamVR's null driver).
    /// </summary>
    public List<string> SelfCheck(ReadOnlySpan<byte> premulBgra)
    {
        var problems = new List<string>();
        Upload(premulBgra);
        var inFront = new Pose(new Vector3(0, 0, -0.6f), Quaternion.Identity);
        var m = ToMatrix(inFront);
        var err = OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_overlay, OpenVR.k_unTrackedDeviceIndex_Hmd, ref m);
        if (err != EVROverlayError.None) problems.Add($"transform: {err}");
        OpenVR.Overlay.SetOverlayWidthInMeters(_overlay, 0.5f);
        err = OpenVR.Overlay.ShowOverlay(_overlay);
        if (err != EVROverlayError.None) problems.Add($"show: {err}");
        uint w = 0, h = 0;
        err = OpenVR.Overlay.GetOverlayTextureSize(_overlay, ref w, ref h);
        if (err != EVROverlayError.None || w != Panel.Width || h != Panel.Height) problems.Add($"texture size {w}x{h} ({err})");
        if (!OpenVR.Overlay.IsOverlayVisible(_overlay)) problems.Add("overlay not visible");
        return problems;
    }

    public void Haptic(Hand hand, float amplitude, float seconds)
    {
        if (!_connected) return;
        OpenVR.Input.TriggerHapticVibrationAction(_haptic, 0, seconds, 160, amplitude, hand == Hand.Left ? _leftSource : _rightSource);
    }

    void Disconnect()
    {
        try
        {
            if (_overlay != 0) OpenVR.Overlay?.DestroyOverlay(_overlay);
            if (_cursorOverlay != 0) OpenVR.Overlay?.DestroyOverlay(_cursorOverlay);
        }
        catch
        {
            // SteamVR may already be gone.
        }
        _overlay = 0;
        _cursorOverlay = 0;
        _cursorShown = false;
        _connected = false;
        _shown = false;
        OpenVR.Shutdown();
        _nextAttempt = Environment.TickCount64 + 5000;
    }

    static Pose ToPose(HmdMatrix34_t m) => Pose.FromMatrix(new Matrix4x4(
        m.m0, m.m4, m.m8, 0,
        m.m1, m.m5, m.m9, 0,
        m.m2, m.m6, m.m10, 0,
        m.m3, m.m7, m.m11, 1));

    static HmdMatrix34_t ToMatrix(Pose p)
    {
        var n = p.ToMatrix();
        return new HmdMatrix34_t
        {
            m0 = n.M11, m1 = n.M21, m2 = n.M31, m3 = n.M41,
            m4 = n.M12, m5 = n.M22, m6 = n.M32, m7 = n.M42,
            m8 = n.M13, m9 = n.M23, m10 = n.M33, m11 = n.M43,
        };
    }

    public void Dispose()
    {
        if (_connected) Disconnect();
        _texture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
