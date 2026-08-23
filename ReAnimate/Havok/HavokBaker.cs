using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Playback;
using FFXIVClientStructs.Havok.Animation.Playback.Control;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Object;
using FFXIVClientStructs.Havok.Common.Base.System.IO.OStream;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using ReAnimate.Bake;
using ReAnimate.Formats;
using ReAnimate.Game;
using hkRootLevelContainer = FFXIVClientStructs.Havok.Common.Serialize.Util.hkRootLevelContainer;
using hkBuiltinTypeRegistry = FFXIVClientStructs.Havok.Common.Serialize.Util.hkBuiltinTypeRegistry;
using hkSerializeUtil = FFXIVClientStructs.Havok.Common.Serialize.Util.hkSerializeUtil;

namespace ReAnimate.Havok;

// The whole bake runs through the game's own statically-linked havok (VFXEditor's
// technique): sample vanilla frames, rewrite tracks over the snapped pose, save back.
// Must run on the framework thread.
public static unsafe class HavokBaker
{
    private const float Fps = 30f;

    // |quat dot| below this = the bone was actually posed away from its baseline.
    private const float PosedEpsilon = 1f - 1e-5f;

    // n_root would shift the whole actor; n_throw breaks poses (Brio skips it too).
    private static readonly string[] SkipBones = ["n_root", "n_throw"];

    // The one bone vanilla positions the body with: every stance (stand, sit, ground sit,
    // doze) is n_hara's translation, every other bone's translation is a fixed bone length.
    // So n_hara is the one bone whose position comes from the pose.
    private static readonly string[] TranslatedBones = ["n_hara"];

    private readonly record struct ExtraTrack(short PlayerIndex, int Depth, Quaternion From, Quaternion Target, BoneTransform Rest);

    // Planted = leg chains (j_asi_a_* and everything below): their end effector stands on
    // the ground, so they correct together with the pelvis instead of root-out, which
    // would walk the foot around and back.
    private sealed record BoneRig(Dictionary<string, (short Index, BoneTransform Reference)> Bones, int[] Depths, bool[] Planted);

    private static readonly string[] PlantedRoots = ["j_asi_a_l", "j_asi_a_r"];

    // Frame-0 rotations of the most recent loop bake, so the _start can aim at exactly
    // what the loop begins with (which is the snap only when the snap was at frame 0).
    public static readonly Dictionary<string, BoneTransform> LoopFrame0 = new(StringComparer.Ordinal);

    // Names of the extra bones appended by the most recent bake, for the chat summary.
    public static readonly HashSet<string> ExtraBoneNames = new(StringComparer.Ordinal);

    // The animation references bones the sampling skeleton does not have (a modded idle
    // needing a skeleton mod the player lacks). Sampling it would corrupt the heap.
    public sealed class SkeletonMismatchException() : Exception("animation needs bones this skeleton does not have");

    // Loop bake: frame 0 = the pose, vanilla per-frame deltas on top. Start bake: the vanilla
    // choreography plays under a correction that lands its last frame on the pose (frame 0 pure vanilla).
    public static byte[] Bake(byte[] papBytes, SkeletonSource skeletonSource, CapturedPose pose, bool asStart, string tempDir, nint interleavedVtbl)
    {
        var pap = PapFile.Read(papBytes);
        if (pap.HavokData.Length <= 8)
            throw new InvalidDataException("pap has no havok animation data");
        ExtraBoneNames.Clear();

        var allocs = new List<nint>();
        hkResource* papRes = null;
        hkResource* sklbRes = null;
        try
        {
            var papContainer = LoadContainer(pap.HavokData, allocs, out papRes);
            var anims = (hkaAnimationContainer*)papContainer->findObjectByType("hkaAnimationContainer", null);
            if (anims == null)
                throw new InvalidDataException("pap havok has no animation container");

            // the character's live skeleton when available (modded bones included), else
            // the vanilla sklb of the target race
            hkaSkeleton* skeleton;
            if (skeletonSource.Live != 0)
            {
                skeleton = (hkaSkeleton*)skeletonSource.Live;
            }
            else
            {
                var sklbContainer = LoadContainer(skeletonSource.Sklb!, allocs, out sklbRes);
                var bones = (hkaAnimationContainer*)sklbContainer->findObjectByType("hkaAnimationContainer", null);
                skeleton = bones == null || bones->Skeletons.Length == 0 ? null : bones->Skeletons[0].ptr;
                if (skeleton == null)
                    throw new InvalidDataException("sklb contains no skeleton");
            }

            var rig = RigFromSkeleton(skeleton);
            for (var i = 0; i < anims->Bindings.Length; i++)
                BakeAnimation(anims, i, skeleton, pose, rig, asStart, interleavedVtbl, allocs);

            var newHavok = SaveContainer(papContainer, tempDir);

            // preserve the timeline section's 4-byte phase
            var phasePad = ((pap.HavokData.Length % 4) - (newHavok.Length % 4) + 4) % 4;
            if (phasePad != 0)
                Array.Resize(ref newHavok, newHavok.Length + phasePad);

            pap.HavokData = newHavok;
            return pap.Write();
        }
        finally
        {
            if (papRes != null)
                ((hkReferencedObject*)papRes)->RemoveReference();
            if (sklbRes != null)
                ((hkReferencedObject*)sklbRes)->RemoveReference();
            foreach (var ptr in allocs)
                Marshal.FreeHGlobal(ptr);
        }
    }

    private static hkRootLevelContainer* LoadContainer(byte[] tagfile, List<nint> allocs, out hkResource* resource)
    {
        var buffer = Alloc(tagfile.Length, allocs);
        Marshal.Copy(tagfile, 0, buffer, tagfile.Length);

        var options = stackalloc hkSerializeUtil.LoadOptions[1];
        options->TypeInfoRegistry = hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry();
        options->ClassNameRegistry = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry();
        options->Flags = new() { Storage = (int)hkSerializeUtil.LoadOptionBits.Default };

        resource = hkSerializeUtil.LoadFromBuffer((void*)buffer, tagfile.Length, null, options);
        if (resource == null)
            throw new InvalidDataException("the game's havok runtime could not read the tagfile");

        var container = (hkRootLevelContainer*)resource->GetContentsPointer(
            "hkRootLevelContainer", hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry());
        if (container == null)
            throw new InvalidDataException("tagfile has no hkRootLevelContainer");
        return container;
    }

    private static void BakeAnimation(
        hkaAnimationContainer* anims,
        int index,
        hkaSkeleton* skeleton,
        CapturedPose pose,
        BoneRig rig,
        bool asStart,
        nint interleavedVtbl,
        List<nint> allocs)
    {
        var binding = anims->Bindings[index].ptr;
        var anim = binding == null ? null : binding->Animation.ptr;
        if (anim == null)
            return;

        // Additive layers (e.g. idle.pap's cbna_add_dmg_f damage flinch) hold deltas, not
        // poses. Baking absolute values into one gets double-applied by the game.
        if (binding->BlendHint.Storage != 0)
        {
            Plugin.Log.Debug($"skipping additive animation {index} (blend hint {binding->BlendHint.Storage})");
            return;
        }

        var numBones = skeleton->Bones.Length;
        var numFloatSlots = skeleton->FloatSlots.Length;

        // Out-of-range track indices make sampleAndCombineAnimations corrupt the heap
        // (VFXEditor's hard-won lesson) - never sample without the guard.
        for (var t = 0; t < binding->TransformTrackToBoneIndices.Length; t++)
        {
            if (binding->TransformTrackToBoneIndices[t] >= numBones)
                throw new SkeletonMismatchException();
        }

        if (binding->TransformTrackToBoneIndices.Length == 0 && anim->NumberOfTransformTracks > numBones)
            throw new SkeletonMismatchException();

        var trackCount = anim->NumberOfTransformTracks;
        var floatTrackCount = anim->NumberOfFloatTracks;
        var duration = anim->Duration;
        var frames = Math.Max(1, (int)MathF.Round(duration * Fps) + 1);
        var frameTime = frames > 1 ? duration / (frames - 1) : 0f;

        // sample every frame; output is indexed by BONE (the binding scatter is applied)
        var animated = (hkaAnimatedSkeleton*)Alloc(sizeof(hkaAnimatedSkeleton), allocs);
        var control = (hkaAnimationControl*)Alloc(sizeof(hkaAnimationControl), allocs);
        control->Ctor1(binding);
        animated->Ctor1(skeleton);
        animated->addAnimationControl(control);
        control->Weight = 1f;

        var floatStride = Math.Max(1, numFloatSlots);
        var boneBuf = (hkQsTransformf*)Alloc(numBones * sizeof(hkQsTransformf), allocs);
        var floatBuf = (float*)Alloc(floatStride * sizeof(float), allocs);

        var samples = new hkQsTransformf[frames * numBones];
        var floatSamples = floatTrackCount > 0 ? new float[frames * floatStride] : null;
        for (var f = 0; f < frames; f++)
        {
            control->LocalTime = f * frameTime;
            animated->sampleAndCombineAnimations(boneBuf, floatBuf);
            for (var b = 0; b < numBones; b++)
                samples[f * numBones + b] = boneBuf[b];
            if (floatSamples is not null)
            {
                for (var s = 0; s < numFloatSlots; s++)
                    floatSamples[f * floatStride + s] = floatBuf[s];
            }
        }

        // the reference frame: where the actor's clip was when the pose was snapped, if
        // this is that clip (same duration); else frame 0. Makes re-saves idempotent.
        var reference = new hkQsTransformf[numBones];
        var referenceTime = !asStart && pose.PlaybackDuration > 0 && Math.Abs(duration - pose.PlaybackDuration) < 0.02f
            ? Math.Clamp(pose.PlaybackTime, 0f, duration)
            : 0f;
        control->LocalTime = referenceTime;
        animated->sampleAndCombineAnimations(boneBuf, floatBuf);
        for (var b = 0; b < numBones; b++)
            reference[b] = boneBuf[b];

        animated->removeAnimationControl(control);

        // track -> bone mapping (empty mapping array means track i drives bone i)
        var mapLength = binding->TransformTrackToBoneIndices.Length;
        var trackBone = new short[trackCount];
        for (var t = 0; t < trackCount; t++)
            trackBone[t] = mapLength == 0 ? (short)t : t < mapLength ? binding->TransformTrackToBoneIndices[t] : (short)-1;

        var trackedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in trackBone)
        {
            if (bone >= 0 && skeleton->Bones.Data[bone].Name.String is { } n)
                trackedNames.Add(n);
        }

        var extras = ExtraTracks(pose, rig, trackedNames);
        var totalTracks = trackCount + extras.Count;

        // which tracks get the pose, and the depth range they span (for the root-out ease)
        var bakeFor = new BoneTransform?[trackCount];
        var minDepth = int.MaxValue;
        var maxDepth = int.MinValue;
        for (var t = 0; t < trackCount; t++)
        {
            var bone = trackBone[t];
            var name = bone >= 0 ? skeleton->Bones.Data[bone].Name.String : null;
            if (name is null || SkipBones.Contains(name) || !pose.Locals.TryGetValue(name, out var snap))
                continue;
            bakeFor[t] = snap;
            if (rig.Planted[bone])
                continue;
            minDepth = Math.Min(minDepth, rig.Depths[bone]);
            maxDepth = Math.Max(maxDepth, rig.Depths[bone]);
        }

        foreach (var extra in extras)
        {
            if (rig.Planted[extra.PlayerIndex])
                continue;
            minDepth = Math.Min(minDepth, extra.Depth);
            maxDepth = Math.Max(maxDepth, extra.Depth);
        }

        if (minDepth == int.MaxValue)
            (minDepth, maxDepth) = (0, 0);
        var ease = new BakeMath.StaggeredEase(frames, minDepth, maxDepth);
        int ScheduleDepth(int boneIndex, int depth) => rig.Planted[boneIndex] ? minDepth : depth;
        if (!asStart)
            LoopFrame0.Clear();

        var transforms = (hkQsTransformf*)Alloc(frames * totalTracks * sizeof(hkQsTransformf), allocs);
        for (var t = 0; t < trackCount; t++)
        {
            var bone = trackBone[t];
            if (bakeFor[t] is not { } bake || bone < 0)
            {
                for (var f = 0; f < frames; f++)
                    transforms[f * totalTracks + t] = bone < 0 ? ToHk(BoneTransform.Identity) : samples[f * numBones + bone];
                continue;
            }

            var name = skeleton->Bones.Data[bone].Name.String ?? "";
            var withTranslation = TranslatedBones.Contains(name);
            if (asStart)
                WriteStartTrack(transforms, t, totalTracks, frames, samples, numBones, bone, bake, withTranslation, ease, ScheduleDepth(bone, rig.Depths[bone]));
            else
                LoopFrame0[name] = WriteLoopTrack(transforms, t, totalTracks, frames, samples, reference, numBones, bone, bake, withTranslation);
        }

        for (var e = 0; e < extras.Count; e++)
            WriteExtraTrack(transforms, trackCount + e, totalTracks, frames, extras[e] with { Depth = ScheduleDepth(extras[e].PlayerIndex, extras[e].Depth) }, asStart, ease);

        // float tracks pass through unchanged (rare on idles, usually zero)
        float* floats = null;
        if (floatTrackCount > 0 && floatSamples is not null)
        {
            floats = (float*)Alloc(frames * floatTrackCount * sizeof(float), allocs);
            var explicitFloats = binding->FloatTrackToFloatSlotIndices.Length > 0;
            for (var ft = 0; ft < floatTrackCount; ft++)
            {
                var slot = explicitFloats ? binding->FloatTrackToFloatSlotIndices[ft] : (short)ft;
                for (var f = 0; f < frames; f++)
                {
                    floats[f * floatTrackCount + ft] = slot >= 0 && slot < numFloatSlots
                        ? floatSamples[f * floatStride + slot]
                        : 0f;
                }
            }
        }

        // extend the binding's mapping with the extra bones (player-rig indices: the pap
        // plays on the player's skeleton, modded bones included)
        if (extras.Count > 0)
        {
            var map = (short*)Alloc((trackCount + extras.Count) * sizeof(short), allocs);
            for (var t = 0; t < trackCount; t++)
                map[t] = trackBone[t];
            for (var e = 0; e < extras.Count; e++)
                map[trackCount + e] = extras[e].PlayerIndex;
            binding->TransformTrackToBoneIndices = MakeArray(map, totalTracks);
        }

        // build the replacement animation object with the game's interleaved vtable
        var newAnim = (HkaInterleavedUncompressedAnimation*)Alloc(sizeof(HkaInterleavedUncompressedAnimation), allocs);
        *newAnim = default;
        ((HkBaseObject*)newAnim)->Vfptr = (void*)interleavedVtbl;
        ((hkReferencedObject*)newAnim)->MemSizeAndRefCount = 0; // not heap-owned, never freed by havok
        newAnim->Animation.Type = hkaAnimation.AnimationType.InterleavedAnimation;
        newAnim->Animation.Duration = duration;
        newAnim->Animation.NumberOfTransformTracks = totalTracks;
        newAnim->Animation.NumberOfFloatTracks = floatTrackCount;
        newAnim->Animation.ExtractedMotion = anim->ExtractedMotion;
        // annotations are the scheduler's timed event markers (fidgets, facial timings) -
        // always carried over, never stripped
        newAnim->Animation.AnnotationTracks = anim->AnnotationTracks;
        newAnim->Transforms = MakeArray(transforms, frames * totalTracks);
        newAnim->Floats = floats == null ? EmptyArray<float>() : MakeArray(floats, frames * floatTrackCount);

        // in-place pointer swap; the old spline object stays owned by the resource
        binding->Animation.ptr = (hkaAnimation*)newAnim;
        anims->Animations.Data[index].ptr = (hkaAnimation*)newAnim;
    }

    // The snap is the pose at the reference frame; every frame gets the vanilla delta
    // relative to that frame. Returns the baked frame-0 rotation.
    private static BoneTransform WriteLoopTrack(hkQsTransformf* transforms, int t, int totalTracks, int frames, hkQsTransformf[] samples, hkQsTransformf[] reference, int numBones, short bone, in BoneTransform snap, bool withTranslation)
    {
        var vanillaRef = GameState.ToBoneTransform(reference[bone]);
        var previous = default(Quaternion);
        var frame0 = default(BoneTransform);
        for (var f = 0; f < frames; f++)
        {
            var baked = BakeMath.Bake(snap, vanillaRef, GameState.ToBoneTransform(samples[f * numBones + bone]), withTranslation);
            var rot = f == 0 ? baked.Rotation : BakeMath.AlignSign(previous, baked.Rotation);
            baked = baked with { Rotation = rot };
            if (f == 0)
                frame0 = baked;
            previous = rot;
            transforms[f * totalTracks + t] = ToHk(baked);
        }

        return frame0;
    }

    // Vanilla start frames with the correction eased in at the rate of the bone's OWN vanilla
    // motion (it moves once, when vanilla moves it); bones vanilla never moves take the root-out schedule.
    private static void WriteStartTrack(hkQsTransformf* transforms, int t, int totalTracks, int frames, hkQsTransformf[] samples, int numBones, short bone, in BoneTransform target, bool withTranslation, in BakeMath.StaggeredEase ease, int depth)
    {
        var final = GameState.ToBoneTransform(samples[(frames - 1) * numBones + bone]);
        var correction = Quaternion.Normalize(
            Quaternion.Inverse(final.Rotation) * BakeMath.AlignSign(final.Rotation, target.Rotation));
        var shift = withTranslation ? target.Translation - final.Translation : Vector3.Zero;

        var track = new Quaternion[frames];
        for (var f = 0; f < frames; f++)
            track[f] = GameState.ToBoneTransform(samples[f * numBones + bone]).Rotation;
        var progress = BakeMath.ArcProgress(track);

        var previous = default(Quaternion);
        for (var f = 0; f < frames; f++)
        {
            var vanilla = GameState.ToBoneTransform(samples[f * numBones + bone]);
            var w = progress is not null ? progress[f] : ease.Weight(depth, f);
            var rot = Quaternion.Normalize(vanilla.Rotation * Quaternion.Slerp(Quaternion.Identity, correction, w));
            rot = f == 0 ? rot : BakeMath.AlignSign(previous, rot);
            previous = rot;
            transforms[f * totalTracks + t] = ToHk(vanilla with { Rotation = rot, Translation = vanilla.Translation + shift * w });
        }
    }

    private static void WriteExtraTrack(hkQsTransformf* transforms, int t, int totalTracks, int frames, in ExtraTrack extra, bool asStart, in BakeMath.StaggeredEase ease)
    {
        for (var f = 0; f < frames; f++)
        {
            var w = asStart ? ease.Weight(extra.Depth, f) : 1f;
            transforms[f * totalTracks + t] = ToHk(extra.Rest with
            {
                Rotation = w >= 1f ? extra.Target : Quaternion.Slerp(extra.From, extra.Target, w),
            });
        }
    }

    // Posed bones the animation does not track (any modded skeleton): appended as FK tracks
    // (held for loops, eased in for _start), indices from the sampling skeleton. Unposed = no track.
    private static List<ExtraTrack> ExtraTracks(CapturedPose pose, BoneRig rig, HashSet<string> trackedNames)
    {
        var extras = new List<ExtraTrack>();
        var unposed = new List<string>();
        var unknown = new List<string>();
        foreach (var (name, local) in pose.Locals)
        {
            if (trackedNames.Contains(name) || SkipBones.Contains(name))
                continue;
            if (!rig.Bones.TryGetValue(name, out var rigBone))
            {
                unknown.Add(name);
                continue;
            }
            // no reference entry (some skeleton mods leave the array short) = assume posed
            if (pose.References.TryGetValue(name, out var reference)
                && MathF.Abs(Quaternion.Dot(local.Rotation, reference.Rotation)) >= PosedEpsilon)
            {
                unposed.Add(name);
                continue;
            }

            var from = rigBone.Reference.Rotation;
            extras.Add(new ExtraTrack(rigBone.Index, rig.Depths[rigBone.Index], from, BakeMath.AlignSign(from, local.Rotation), rigBone.Reference));
            ExtraBoneNames.Add(name);
        }

        // the log is the place to find out why a custom bone did or did not make it
        Plugin.Log.Debug($"custom bones saved: {(extras.Count == 0 ? "none" : string.Join(", ", ExtraBoneNames))}");
        if (unposed.Count > 0)
            Plugin.Log.Debug($"custom bones left at bind pose (unposed): {string.Join(", ", unposed)}");
        if (unknown.Count > 0)
            Plugin.Log.Debug($"posed bones absent from the sampling skeleton: {string.Join(", ", unknown)}");
        return extras;
    }

    // Names, indices, reference pose and hierarchy depth of whichever skeleton the bake
    // samples against (havok skeletons are parent-first, so one pass gives depths).
    private static BoneRig RigFromSkeleton(hkaSkeleton* skeleton)
    {
        var count = skeleton->Bones.Length;
        var bones = new Dictionary<string, (short Index, BoneTransform Reference)>(count);
        var depths = new int[count];
        var planted = new bool[count];
        for (short i = 0; i < count; i++)
        {
            var parent = i < skeleton->ParentIndices.Length ? skeleton->ParentIndices[i] : (short)-1;
            depths[i] = parent >= 0 && parent < i ? depths[parent] + 1 : 0;
            var name = skeleton->Bones.Data[i].Name.String;
            planted[i] = (parent >= 0 && parent < i && planted[parent]) || (name is not null && PlantedRoots.Contains(name));
            if (!string.IsNullOrEmpty(name) && i < skeleton->ReferencePose.Length)
                bones[name] = (i, GameState.ToBoneTransform(skeleton->ReferencePose.Data[i]));
        }

        return new BoneRig(bones, depths, planted);
    }

    private static byte[] SaveContainer(hkRootLevelContainer* container, string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "reanimate_out.hkx");

        var klass = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry()->GetClassByName("hkRootLevelContainer");
        if (klass == null)
            throw new InvalidOperationException("hkRootLevelContainer class not found in the game's registry");

        var stream = stackalloc hkOstream[1];
        stream->Ctor(path);
        try
        {
            var result = stackalloc hkResult[1];
            var options = new hkSerializeUtil.SaveOptions
            {
                Flags = new() { Storage = (int)hkSerializeUtil.SaveOptionBits.Default },
            };
            hkSerializeUtil.Save(result, container, klass, stream->StreamWriter.ptr, options);
            if (*(int*)result != 0)
                throw new InvalidOperationException("hkSerializeUtil.Save reported failure");
        }
        finally
        {
            stream->Dtor();
        }

        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        if (bytes.Length <= 8)
            throw new InvalidOperationException("havok save produced an empty tagfile");
        return bytes;
    }

    private static nint Alloc(int size, List<nint> allocs)
    {
        var ptr = Marshal.AllocHGlobal(Math.Max(1, size));
        allocs.Add(ptr);
        return ptr;
    }

    // DontDeallocate keeps havok's destructors and serializer away from our HGlobal memory.
    private static hkArray<T> MakeArray<T>(T* data, int count) where T : unmanaged => new()
    {
        Data = data,
        Length = count,
        CapacityAndFlags = count | unchecked((int)hkArray<T>.hkArrayFlags.DontDeallocate),
    };

    private static hkArray<T> EmptyArray<T>() where T : unmanaged => new()
    {
        Data = null,
        Length = 0,
        CapacityAndFlags = unchecked((int)hkArray<T>.hkArrayFlags.DontDeallocate),
    };

    private static hkQsTransformf ToHk(in BoneTransform t) => new()
    {
        Translation = new() { X = t.Translation.X, Y = t.Translation.Y, Z = t.Translation.Z, W = 0f },
        Rotation = new() { X = t.Rotation.X, Y = t.Rotation.Y, Z = t.Rotation.Z, W = t.Rotation.W },
        Scale = new() { X = t.Scale.X, Y = t.Scale.Y, Z = t.Scale.Z, W = 1f },
    };
}

// Live = hkaSkeleton* of the character's loaded body skeleton; Sklb = vanilla skeleton
// tagfile bytes for a race that isn't on screen.
public sealed record SkeletonSource(nint Live, byte[]? Sklb)
{
    public static SkeletonSource FromLive(nint skeleton) => new(skeleton, null);
    public static SkeletonSource FromSklb(byte[] sklbHavok) => new(0, sklbHavok);
}
