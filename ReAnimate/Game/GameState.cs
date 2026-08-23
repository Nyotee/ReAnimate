using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using ReAnimate.Bake;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace ReAnimate.Game;

public sealed record PlayerInfo(ushort RaceSexId, byte AnimVariant, EmoteController.PoseType Family, byte Slot, byte ClassJob);

// PlaybackTime/Duration: where the actor's base animation was when the snap was taken,
// so the bake can use THAT frame as its reference instead of frame 0. Without it a
// re-save of a playing bake adds one frame's motion to the pose every time.
public sealed record CapturedPose(
    Dictionary<string, BoneTransform> Locals,
    Dictionary<string, BoneTransform> References,
    float PlaybackTime = 0f,
    float PlaybackDuration = 0f);

// Framework thread only. Poses are read in local space: that is what animation tracks
// hold, and local rotation is race-independent, so cross-actor snaps transfer.
public static unsafe class GameState
{
    // cpose slots per family never exceed 7 (standing: base + pose01..06)
    public const byte MaxSlots = 7;

    public static readonly EmoteController.PoseType[] BakeableFamilies =
    [
        EmoteController.PoseType.Idle,
        EmoteController.PoseType.Sit,
        EmoteController.PoseType.GroundSit,
        EmoteController.PoseType.Doze,
        EmoteController.PoseType.WeaponDrawn,
    ];

    // ClassJob row -> battle animation folder (the game has no readable field for it; this
    // is VFXEditor's verified table, base classes share their job's weapon). Crafters and
    // gatherers have no weapon-drawn idles.
    private static readonly Dictionary<byte, string> BattleFolders = new()
    {
        [1] = "swd_sld", [19] = "swd_sld",   // GLA / PLD
        [2] = "clw_clw", [20] = "clw_clw",   // PGL / MNK
        [3] = "2ax_emp", [21] = "2ax_emp",   // MRD / WAR
        [4] = "2sp_emp", [22] = "2sp_emp",   // LNC / DRG
        [5] = "2bw_emp", [23] = "2bw_emp",   // ARC / BRD
        [6] = "stf_sld", [24] = "stf_sld",   // CNJ / WHM
        [7] = "jst_sld", [25] = "jst_sld",   // THM / BLM
        [26] = "2bk_emp", [27] = "2bk_emp", [28] = "2bk_emp", // ACN / SMN / SCH
        [29] = "dgr_dgr", [30] = "dgr_dgr",  // ROG / NIN
        [31] = "2gn_emp",                    // MCH
        [32] = "2sw_emp",                    // DRK
        [33] = "2gl_emp",                    // AST
        [34] = "2kt_emp",                    // SAM
        [35] = "2rp_emp",                    // RDM
        [36] = "rod_emp",                    // BLU
        [37] = "2gb_emp",                    // GNB
        [38] = "chk_chk",                    // DNC
        [39] = "2km_emp",                    // RPR
        [40] = "2ff_emp",                    // SGE
        [41] = "bld_bld",                    // VPR
        [42] = "brs_plt",                    // PCT
    };

    private static readonly Dictionary<string, string> WeaponLabels = new()
    {
        ["bt_swd_sld"] = "Sword & Shield", ["bt_clw_clw"] = "Fists", ["bt_2ax_emp"] = "Axe",
        ["bt_2sp_emp"] = "Spear", ["bt_2bw_emp"] = "Bow", ["bt_stf_sld"] = "Cane", ["bt_jst_sld"] = "Staff",
        ["bt_2bk_emp"] = "Book", ["bt_dgr_dgr"] = "Daggers", ["bt_2gn_emp"] = "Gun", ["bt_2sw_emp"] = "Greatsword",
        ["bt_2gl_emp"] = "Globe", ["bt_2kt_emp"] = "Katana", ["bt_2rp_emp"] = "Rapier", ["bt_rod_emp"] = "Rod",
        ["bt_2gb_emp"] = "Gunblade", ["bt_chk_chk"] = "Chakrams", ["bt_2km_emp"] = "Scythe",
        ["bt_2ff_emp"] = "Nouliths", ["bt_bld_bld"] = "Blades", ["bt_brs_plt"] = "Brush",
    };

    public static readonly string[] AllBattleFolders =
        BattleFolders.Values.Distinct().Select(f => $"bt_{f}").ToArray();

    // "bt_2sw_emp" for the player's current class, null when the class has no battle idles.
    public static string? BattleFolder(byte classJob)
        => BattleFolders.TryGetValue(classJob, out var f) ? $"bt_{f}" : null;

    public static string WeaponLabel(string battleFolder)
        => WeaponLabels.TryGetValue(battleFolder, out var l) ? l : battleFolder;

    // "Standing Idle" / "Weapon Drawn (Greatsword)": the family, with the weapon when it matters.
    public static string FamilyLabel(EmoteController.PoseType family, string? battleFolder)
        => family == EmoteController.PoseType.WeaponDrawn && battleFolder is not null
            ? $"{FamilyName(family)} ({WeaponLabel(battleFolder)})"
            : FamilyName(family);

    public static readonly ushort[] PlayableRaces =
        [101, 201, 301, 401, 501, 601, 701, 801, 901, 1001, 1101, 1201, 1301, 1401, 1501, 1601, 1701, 1801];

    // The actor whose pose gets snapped: the GPose target if posing in GPose (that's where
    // Brio/Ktisis edits live), else the local player.
    public static IGameObject? ResolvePoseActor()
    {
        if (Plugin.ClientState.IsGPosing)
        {
            var target = TargetSystem.Instance()->GPoseTarget;
            if (target != null && IsHuman((nint)target))
                return Plugin.Objects.CreateObjectReference((nint)target);

            // fall back to the player's own GPose copy
            var name = Plugin.Objects.LocalPlayer?.Name.TextValue;
            if (name is not null)
            {
                foreach (var obj in Plugin.Objects)
                {
                    if (obj.ObjectIndex is >= 201 and < 244 && obj.Name.TextValue == name && IsHuman(obj.Address))
                        return obj;
                }
            }
        }

        var player = Plugin.Objects.LocalPlayer;
        return player is not null && IsHuman(player.Address) ? player : null;
    }

    private static bool IsHuman(nint address) => HumanBase(address) != null;

    private static CharacterBase* HumanBase(nint address)
    {
        var cbase = ((CSGameObject*)address)->GetCharacterBase();
        return cbase != null && cbase->GetModelType() == CharacterBase.ModelType.Human ? cbase : null;
    }

    // Race, animation variant and current stance, always from the LOCAL player: the mod
    // redirects the player's own idle files.
    public static PlayerInfo? ReadPlayerInfo()
    {
        var player = Plugin.Objects.LocalPlayer;
        if (player is null)
            return null;

        var chara = (Character*)player.Address;
        var cbase = HumanBase(player.Address);
        if (cbase == null)
            return null;

        return new PlayerInfo(
            ((Human*)cbase)->RaceSexId,
            cbase->AnimationVariant,
            chara->EmoteController.CurrentPoseType,
            chara->EmoteController.CPoseState,
            chara->CharacterData.ClassJob);
    }

    // The live cpose slot, but only when the player is actually IN that family right now.
    // For any other family we genuinely don't know, so we never pretend to.
    public static byte? LiveSlot(EmoteController.PoseType family)
    {
        var info = ReadPlayerInfo();
        return info is not null && info.Family == family ? info.Slot : null;
    }

    // Slot count for a family (the game reports count - 1).
    public static byte SlotCount(EmoteController.PoseType family)
        => (byte)Math.Clamp(EmoteController.GetAvailablePoses(family) + 1, 1, MaxSlots);

    // Body partial only (expressions live in their own pap). `baseline` = the actor's untouched
    // twin (the real player while a GPose copy is posed): its live bones define "unposed" for
    // physics and fidget bones, which never rest at bind pose; without one, bind pose does.
    public static CapturedPose? CapturePose(IGameObject actor, IGameObject? baseline)
    {
        var locals = LiveLocals(actor.Address, out var bindPose);
        if (locals is null || bindPose is null)
            return null;

        var references = baseline is null ? null : LiveLocals(baseline.Address, out _);
        var (time, duration) = LivePlayback(actor.Address);
        return new CapturedPose(locals, references ?? bindPose, time, duration);
    }

    // The dominant normal (non-additive) animation control on the body partial: its local
    // time and clip duration. (0, 0) when nothing is playing.
    private static (float Time, float Duration) LivePlayback(nint address)
    {
        var partial = BodyPartial(address);
        var animated = partial == null ? null : partial->GetHavokAnimatedSkeleton(0);
        if (animated == null)
            return (0f, 0f);

        var bestWeight = 0f;
        var result = (0f, 0f);
        for (var i = 0; i < animated->AnimationControls.Length; i++)
        {
            var control = (FFXIVClientStructs.Havok.Animation.Playback.Control.hkaAnimationControl*)animated->AnimationControls[i].Value;
            if (control == null || control->Binding.ptr == null || control->Binding.ptr->Animation.ptr == null)
                continue;
            if (control->Binding.ptr->BlendHint.Storage != 0 || control->Weight <= bestWeight)
                continue;

            bestWeight = control->Weight;
            result = (control->LocalTime, control->Binding.ptr->Animation.ptr->Duration);
        }

        return result;
    }

    private static Dictionary<string, BoneTransform>? LiveLocals(nint address, out Dictionary<string, BoneTransform>? bindPose)
    {
        bindPose = null;
        var pose = BodyPose(address);
        if (pose == null)
            return null;

        var local = pose->GetSyncedPoseLocalSpace();
        if (local == null || local->Data == null)
            return null;

        var skeleton = pose->Skeleton;
        var locals = new Dictionary<string, BoneTransform>(skeleton->Bones.Length);
        bindPose = new Dictionary<string, BoneTransform>(skeleton->Bones.Length);
        for (var i = 0; i < skeleton->Bones.Length && i < local->Length; i++)
        {
            var name = skeleton->Bones.Data[i].Name.String;
            if (string.IsNullOrEmpty(name))
                continue;

            locals[name] = ToBoneTransform(local->Data[i]);
            if (i < skeleton->ReferencePose.Length)
                bindPose[name] = ToBoneTransform(skeleton->ReferencePose.Data[i]);
        }

        return locals.Count > 0 ? locals : null;
    }

    // The LOCAL player's live body skeleton (hkaSkeleton*), modded bones and all. The
    // baker samples against it and takes extra-bone indices from it, so whatever is
    // loaded on the character is what gets saved - no files to dig through.
    public static nint LiveBodySkeleton()
    {
        var player = Plugin.Objects.LocalPlayer;
        var pose = player is null ? null : BodyPose(player.Address);
        return pose == null ? 0 : (nint)pose->Skeleton;
    }

    private static PartialSkeleton* BodyPartial(nint address)
    {
        var cbase = ((CSGameObject*)address)->GetCharacterBase();
        if (cbase == null || cbase->Skeleton == null || cbase->Skeleton->PartialSkeletonCount == 0)
            return null;
        return &cbase->Skeleton->PartialSkeletons[0];
    }

    private static hkaPose* BodyPose(nint address)
    {
        var partial = BodyPartial(address);
        var pose = partial == null ? null : partial->GetHavokPose(0);
        return pose != null && pose->Skeleton != null ? pose : null;
    }

    internal static BoneTransform ToBoneTransform(in hkQsTransformf t) => new(
        new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z),
        new Quaternion(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W),
        new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z));

    public static string FamilyName(EmoteController.PoseType type) => type switch
    {
        EmoteController.PoseType.Idle => "Standing Idle",
        EmoteController.PoseType.Sit => "Sitting",
        EmoteController.PoseType.GroundSit => "Ground Sit",
        EmoteController.PoseType.Doze => "Doze",
        EmoteController.PoseType.WeaponDrawn => "Weapon Drawn",
        EmoteController.PoseType.Umbrella => "Parasol",
        _ => type.ToString(),
    };

    public static string RaceName(ushort raceSexId)
    {
        var hundreds = raceSexId / 100;
        var female = hundreds % 2 == 0;
        var name = (female ? hundreds - 1 : hundreds) switch
        {
            1 => "Midlander",
            3 => "Highlander",
            5 => "Elezen",
            7 => "Miqo'te",
            9 => "Roegadyn",
            11 => "Lalafell",
            13 => "Au Ra",
            15 => "Hrothgar",
            17 => "Viera",
            _ => "Adventurer",
        };
        return $"{name} {(female ? "Female" : "Male")}";
    }
}
