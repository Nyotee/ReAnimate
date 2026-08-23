using System.Runtime.InteropServices;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace ReAnimate.Havok;

// FFXIVClientStructs wraps most of havok but not the interleaved animation subclass, so it
// is hand-rolled here (layout per hk2014 hkaInterleavedUncompressedAnimation.h): the
// hkaAnimation base followed by the transforms and floats arrays.
[StructLayout(LayoutKind.Sequential)]
public struct HkaInterleavedUncompressedAnimation
{
    public hkaAnimation Animation;
    public hkArray<hkQsTransformf> Transforms;
    public hkArray<float> Floats;
}

// vfptr stamping helper: a havok object is just a struct whose first pointer is the vtable.
[StructLayout(LayoutKind.Explicit, Size = 0x08)]
public unsafe struct HkBaseObject
{
    [FieldOffset(0x00)] public void* Vfptr;
}
