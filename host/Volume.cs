using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LyrenneVR;

/// <summary>
/// Volume for Lyrenne's own audio session in the Windows mixer, so turning the music down in VR
/// never touches the game. Falls back to the default output device when no Lyrenne session exists.
/// </summary>
public sealed class Volume
{
    long _lastScan;
    ISimpleAudioVolume? _session;
    IAudioEndpointVolume? _endpoint;

    public bool IsAppVolume => _session != null;

    public float Get()
    {
        try
        {
            Refresh();
            if (_session != null) { _session.GetMasterVolume(out float v); return v; }
            if (_endpoint != null) { _endpoint.GetMasterVolumeLevelScalar(out float v); return v; }
        }
        catch (COMException)
        {
            _session = null;
        }
        return 0;
    }

    public void Set(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        try
        {
            Refresh();
            var ctx = Guid.Empty;
            if (_session != null) _session.SetMasterVolume(value, ref ctx);
            else _endpoint?.SetMasterVolumeLevelScalar(value, ref ctx);
        }
        catch (COMException)
        {
            _session = null;
        }
    }

    void Refresh()
    {
        if (Environment.TickCount64 - _lastScan < 3000 && (_session != null || _endpoint != null)) return;
        _lastScan = Environment.TickCount64;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device);
            var iidEndpoint = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iidEndpoint, 1, IntPtr.Zero, out var ep);
            _endpoint = (IAudioEndpointVolume)ep;

            var iidManager = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iidManager, 1, IntPtr.Zero, out var mgr);
            ((IAudioSessionManager2)mgr).GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);
            _session = null;
            for (int i = 0; i < count && _session == null; i++)
            {
                sessions.GetSession(i, out var control);
                if (control is not IAudioSessionControl2 c2) continue;
                c2.GetProcessId(out uint pid);
                if (pid != 0 && IsLyrenne((int)pid)) _session = (ISimpleAudioVolume)control;
            }
        }
        catch (Exception e)
        {
            Log.Write($"audio session scan failed: {e.Message}");
        }
    }

    static bool IsLyrenne(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.ProcessName.Contains("lyrenne", StringComparison.OrdinalIgnoreCase)) return true;
            // The JVM child of the Lyrenne launcher lives under the install folder.
            string? path = p.MainModule?.FileName;
            return path != null && path.Contains("lyrenne", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int NotImpl1();
        int NotImpl2();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int NotImpl0(); int NotImpl1(); int NotImpl2(); int NotImpl3(); int NotImpl4();
        int NotImpl5(); int NotImpl6(); int NotImpl7(); int NotImpl8();
        // IAudioSessionControl2
        int NotImpl9(); int NotImpl10();
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int NotImpl0(); int NotImpl1(); int NotImpl2(); int NotImpl3();
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int NotImpl6();
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }
}
