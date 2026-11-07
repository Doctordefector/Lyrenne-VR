using System.Numerics;

namespace LyrenneVR;

/// <summary>A rigid transform. Both backends report poses in OpenXR conventions: +Y up, -Z forward, meters.</summary>
public readonly record struct Pose(Vector3 Position, Quaternion Rotation)
{
    public static readonly Pose Identity = new(Vector3.Zero, Quaternion.Identity);

    public Vector3 Transform(Vector3 local) => Position + Vector3.Transform(local, Rotation);
    public Vector3 Rotate(Vector3 dir) => Vector3.Transform(dir, Rotation);

    /// <summary>this * child: child expressed in this pose's space, returned in the parent space.</summary>
    public Pose Compose(Pose child) => new(Transform(child.Position), Quaternion.Normalize(Rotation * child.Rotation));

    public Pose Inverse()
    {
        var inv = Quaternion.Inverse(Rotation);
        return new Pose(Vector3.Transform(-Position, inv), inv);
    }

    public Vector3 InverseTransform(Vector3 world) => Vector3.Transform(world - Position, Quaternion.Inverse(Rotation));

    public Vector3 Forward => Rotate(-Vector3.UnitZ);
    public Vector3 Up => Rotate(Vector3.UnitY);

    /// <summary>Rotation whose local X, Y and Z axes map to the given orthonormal vectors.</summary>
    public static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    public static Pose FromMatrix(Matrix4x4 m) =>
        new(m.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m)));

    public Matrix4x4 ToMatrix() => Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
}

/// <summary>One sample of everything the wrist player needs to know about the user. Missing poses are null.</summary>
public sealed record TrackingFrame(Pose? Head, Pose? LeftGrip, Pose? RightGrip, Pose? LeftAim, Pose? RightAim,
    float LeftTrigger = 0, float RightTrigger = 0)
{
    public float Trigger(Hand h) => h == Hand.Left ? LeftTrigger : RightTrigger;
    public Pose? Grip(Hand h) => h == Hand.Left ? LeftGrip : RightGrip;
    public Pose? Aim(Hand h) => h == Hand.Left ? LeftAim : RightAim;
}
