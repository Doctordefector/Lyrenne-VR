using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace LyrenneVR;

/// <summary>
/// Host side of the shared memory the OpenXR layer reads and writes. Layout mirrors layer/bridge.h.
/// </summary>
public sealed unsafe class Bridge : IDisposable
{
    public const string Name = "Local\\LyrenneVR.Bridge.v1";
    const uint Magic = 0x3152564C;
    const uint Version = 1;
    public const int MaxWidth = 1024;
    public const int MaxHeight = 512;
    const int PixelOffset = 512;
    const int Size = PixelOffset + MaxWidth * MaxHeight * 4;

    // Offsets, see bridge.h.
    const int OffHostHeartbeat = 8, OffFrameSeq = 16, OffWidth = 20, OffHeight = 24, OffVisible = 28, OffAnchor = 32;
    const int OffPanelWidth = 36, OffPanelPose = 40, OffHapticSeq = 68, OffHapticHand = 72, OffHapticAmp = 76, OffHapticDur = 80;
    const int OffClientHeartbeat = 88, OffClientPid = 96, OffPoseFlags = 100, OffHead = 104, OffLeftGrip = 132, OffRightGrip = 160;
    const int OffLeftAim = 188, OffRightAim = 216, OffPoseSeq = 244, OffRuntime = 248, OffApp = 312;

    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _base;
    uint _frameSeq;
    uint _hapticSeq;

    public Bridge()
    {
        _file = MemoryMappedFile.CreateOrOpen(Name, Size, MemoryMappedFileAccess.ReadWrite);
        _view = _file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;
        new Span<byte>(_base, PixelOffset).Clear();
        W<uint>(0, Magic);
        W<uint>(4, Version);
        Heartbeat();
    }

    T R<T>(int off) where T : unmanaged => *(T*)(_base + off);
    void W<T>(int off, T v) where T : unmanaged => *(T*)(_base + off) = v;

    public void Heartbeat() => W(OffHostHeartbeat, (long)Environment.TickCount64);

    /// <summary>A game with the layer loaded has reported in within the last half second.</summary>
    public bool ClientAlive => Environment.TickCount64 - R<long>(OffClientHeartbeat) < 500;

    public string RuntimeName => ReadString(OffRuntime);
    public string AppName => ReadString(OffApp);

    string ReadString(int off)
    {
        var span = new ReadOnlySpan<byte>(_base + off, 64);
        int end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    public uint PoseSeq => Volatile.Read(ref *(uint*)(_base + OffPoseSeq));

    public TrackingFrame ReadPoses()
    {
        uint flags = R<uint>(OffPoseFlags);
        Pose? Get(int off, uint bit) => (flags & bit) != 0 ? ReadPose(off) : null;
        return new TrackingFrame(
            Get(OffHead, 1), Get(OffLeftGrip, 2), Get(OffRightGrip, 4), Get(OffLeftAim, 8), Get(OffRightAim, 16));
    }

    Pose ReadPose(int off)
    {
        var f = (float*)(_base + off);
        return new Pose(new Vector3(f[4], f[5], f[6]), new Quaternion(f[0], f[1], f[2], f[3]));
    }

    public void SetPanel(bool visible, Anchor anchor, Pose pose, float widthMeters)
    {
        W<uint>(OffAnchor, (uint)anchor);
        W(OffPanelWidth, widthMeters);
        var f = (float*)(_base + OffPanelPose);
        f[0] = pose.Rotation.X; f[1] = pose.Rotation.Y; f[2] = pose.Rotation.Z; f[3] = pose.Rotation.W;
        f[4] = pose.Position.X; f[5] = pose.Position.Y; f[6] = pose.Position.Z;
        W<uint>(OffVisible, visible ? 1u : 0u);
    }

    /// <summary>Publishes a premultiplied BGRA frame under the seqlock the layer reads with.</summary>
    public void PublishFrame(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width > MaxWidth || height > MaxHeight || bgra.Length < width * height * 4) throw new ArgumentException("frame too large");
        ref uint seq = ref *(uint*)(_base + OffFrameSeq);
        _frameSeq = (_frameSeq + 1) | 1;
        Volatile.Write(ref seq, _frameSeq);
        Thread.MemoryBarrier();
        W<uint>(OffWidth, (uint)width);
        W<uint>(OffHeight, (uint)height);
        bgra[..(width * height * 4)].CopyTo(new Span<byte>(_base + PixelOffset, width * height * 4));
        Thread.MemoryBarrier();
        _frameSeq++;
        Volatile.Write(ref seq, _frameSeq);
    }

    public void Haptic(Hand hand, float amplitude, float seconds)
    {
        W<uint>(OffHapticHand, hand == Hand.Left ? 0u : 1u);
        W(OffHapticAmp, amplitude);
        W(OffHapticDur, seconds);
        Thread.MemoryBarrier();
        W(OffHapticSeq, ++_hapticSeq);
    }

    public void Dispose()
    {
        W<uint>(OffVisible, 0);
        W(OffHostHeartbeat, 0L);
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}

public enum Anchor : uint { LeftGrip = 0, RightGrip = 1, World = 2 }

public enum Hand { Left, Right }
