using Dalamud.Plugin.Services;

namespace ReAnimate.Havok;

// The one game address we need beyond FFXIVClientStructs: the vtable of
// hkaInterleavedUncompressedAnimation, so freshly built animation objects present the right
// virtuals to the serializer. Sig from VFXEditor: matches the `mov [rdi], rax` that stamps
// the vfptr; -4 walks back onto the disp32 of the preceding rip-relative lea.
public static class HavokVtables
{
    private const string InterleavedVtblSig = "48 89 07 48 8B CD 48 89 77 38";

    private static nint interleaved;

    public static unsafe nint InterleavedAnimation(ISigScanner scanner)
    {
        if (interleaved != 0)
            return interleaved;

        var lea = scanner.ScanText(InterleavedVtblSig) - 4;
        var vtbl = lea + 4 + *(int*)lea;
        if (vtbl == 0 || *(nint*)vtbl == 0)
            throw new InvalidOperationException("resolved an invalid interleaved-animation vtable");
        return interleaved = vtbl;
    }
}
