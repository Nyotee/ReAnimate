using System.Numerics;

namespace ReAnimate.Bake;

public readonly record struct BoneTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    public static readonly BoneTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

// Havok tracks are absolute local-space transforms, so the idle's passive motion is the
// per-frame rotation delta against frame 0, re-applied on top of the snapped pose.
public static class BakeMath
{
    // Rotation only: translation/scale stay vanilla so Customize+ style edits on the live
    // skeleton never get baked (they would double once C+ reapplies them at runtime).
    // withTranslation = also carry the snap's translation offset (vs the reference frame)
    // onto every frame; used for the one bone vanilla positions the body with.
    public static BoneTransform Bake(in BoneTransform snapped, in BoneTransform vanillaFrame0, in BoneTransform vanillaFrameF, bool withTranslation = false)
    {
        var delta = Quaternion.Normalize(Quaternion.Inverse(vanillaFrame0.Rotation) * vanillaFrameF.Rotation);
        var translation = withTranslation
            ? vanillaFrameF.Translation + (snapped.Translation - vanillaFrame0.Translation)
            : vanillaFrameF.Translation;
        return new BoneTransform(translation, Quaternion.Normalize(snapped.Rotation * delta), vanillaFrameF.Scale);
    }

    // Root-out ease schedule for _start transitions: bones settle parent first, child
    // after, overlapping like a real body does (shoulder, then elbow, then fingers; tail
    // base before tail tip). Each bone gets the same window length, started later the
    // deeper it sits; the deepest bones land exactly on the last frame, frame 0 is
    // untouched for everyone.
    public readonly struct StaggeredEase
    {
        private readonly int total;
        private readonly float window;
        private readonly float stagger;
        private readonly int minDepth;

        // the whole cascade is done by this fraction of the clip; the rest just plays
        private const float SettleFraction = 0.55f;

        public StaggeredEase(int frames, int minDepth, int maxDepth)
        {
            total = Math.Max(1, frames - 1);
            this.minDepth = minDepth;
            var settle = Math.Max(1f, total * SettleFraction);
            // a third of the settle span per bone (at least 6 frames), the rest spread over depth
            window = Math.Min(settle, Math.Max(6f, settle / 3f));
            var span = Math.Max(0, maxDepth - minDepth);
            stagger = span > 0 ? (settle - window) / span : 0f;
        }

        public float Weight(int depth, int frame)
        {
            if (frame >= total)
                return 1f;
            var start = (depth - minDepth) * stagger;
            var t = Math.Clamp((frame - start) / window, 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }

    // How far along its own motion a vanilla track is at each frame: cumulative rotation
    // arc / total arc, 0 at frame 0, 1 at the end, monotonic. A transition's choreography
    // already times every bone (fingers bend when vanilla bends them), so a correction
    // applied at this rate moves the bone once, on vanilla's schedule, never twice.
    // Null when the bone barely moves in vanilla (nothing to follow).
    public static float[]? ArcProgress(ReadOnlySpan<Quaternion> track)
    {
        const float MinTotalArc = 0.035f; // radians; under this vanilla barely moves the bone
        if (track.Length < 2)
            return null;

        var cumulative = new float[track.Length];
        for (var i = 1; i < track.Length; i++)
        {
            var dot = Math.Clamp(MathF.Abs(Quaternion.Dot(track[i - 1], track[i])), 0f, 1f);
            cumulative[i] = cumulative[i - 1] + 2f * MathF.Acos(dot);
        }

        var total = cumulative[^1];
        if (total < MinTotalArc)
            return null;

        for (var i = 0; i < cumulative.Length; i++)
            cumulative[i] = Math.Clamp(cumulative[i] / total, 0f, 1f);
        cumulative[^1] = 1f;
        return cumulative;
    }

    // Keeps quaternion sign continuous along a track so interpolation never goes the long way.
    public static Quaternion AlignSign(in Quaternion previous, in Quaternion current)
        => Quaternion.Dot(previous, current) < 0 ? -current : current;
}
