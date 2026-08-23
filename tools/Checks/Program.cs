using System.Numerics;
using ReAnimate.Bake;
using ReAnimate.Formats;

// Offline checks against REAL game files (extract_testfiles.py pulls them):
//   ReAnimateChecks <dir with idle_*.pap / *.sklb>
// Pap/tmb round-trips, havok blob magic, phase padding, the swap retarget and the bake math.

var dir = args.Length > 0 ? args[0] : ".";
var failed = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : $"  ({detail})")}");
    if (!ok)
        failed++;
}

// hk2014 binary tagfile magic: 0xCAB00D1E 0xD011FACE
bool IsTagfile(byte[] blob) =>
    blob.Length > 8
    && BitConverter.ToUInt32(blob, 0) == 0xCAB00D1E
    && BitConverter.ToUInt32(blob, 4) == 0xD011FACE;

var papFiles = Directory.GetFiles(dir, "*.pap").OrderBy(f => f).ToList();
foreach (var papPath in papFiles)
{
    var name = Path.GetFileName(papPath);
    var original = File.ReadAllBytes(papPath);
    var pap = PapFile.Read(original);

    Check($"{name}: version", pap.Version == 0x00020001, $"0x{pap.Version:X8}");
    Check($"{name}: anims >= 1", pap.AnimCount >= 1, $"{pap.AnimCount}");
    Check($"{name}: havok blob is TAG0", IsTagfile(pap.HavokData), $"{pap.HavokData.Length} bytes");
    Check($"{name}: timeline present", pap.TimelineBytes.Length > 0, $"{pap.TimelineBytes.Length} bytes");

    var rewritten = pap.Write();
    Check($"{name}: round-trip byte-identical", rewritten.AsSpan().SequenceEqual(original),
        $"{original.Length} -> {rewritten.Length} bytes");

    // a size-changing havok swap keeps the timeline 4-byte phase
    var grown = PapFile.Read(original);
    var bigger = new byte[grown.HavokData.Length + 13];
    grown.HavokData.CopyTo(bigger, 0);
    var phasePad = ((grown.HavokData.Length % 4) - (bigger.Length % 4) + 4) % 4;
    Array.Resize(ref bigger, bigger.Length + phasePad);
    grown.HavokData = bigger;
    var grownBytes = grown.Write();
    var reparsed = PapFile.Read(grownBytes);
    Check($"{name}: havok swap preserves timeline bytes",
        reparsed.TimelineBytes.AsSpan().SequenceEqual(pap.TimelineBytes));
    Check($"{name}: havok swap preserves timeline phase",
        FindTimelineOffset(grownBytes) % 4 == FindTimelineOffset(original) % 4);

    // tmb split/join
    var tmbs = pap.Tmbs();
    Check($"{name}: one tmb per animation", tmbs.Count == pap.AnimCount, $"{tmbs.Count} tmbs / {pap.AnimCount} anims");
    var rejoined = PapFile.Read(original);
    rejoined.SetTmbs(tmbs);
    Check($"{name}: tmb split/join byte-identical", rejoined.TimelineBytes.AsSpan().SequenceEqual(pap.TimelineBytes));
    for (var i = 0; i < tmbs.Count; i++)
    {
        var paths = TmbFile.AnimationPaths(tmbs[i]);
        Check($"{name}: tmb {i} C009 path == info name", paths.Contains(pap.AnimName(i)), $"{pap.AnimName(i)} vs [{string.Join(", ", paths)}]");
    }
}

foreach (var sklbPath in Directory.GetFiles(dir, "*.sklb"))
{
    var name = Path.GetFileName(sklbPath);
    var havok = SklbFile.HavokData(File.ReadAllBytes(sklbPath));
    Check($"{name}: havok blob is TAG0", IsTagfile(havok), $"{havok.Length} bytes");
}

// the swap retarget on real files
var idlePap = papFiles.FirstOrDefault(f => Path.GetFileName(f).StartsWith("idle_"));
var sitPap = papFiles.FirstOrDefault(f => Path.GetFileName(f).StartsWith("sit_"));
var posePap = papFiles.FirstOrDefault(f => Path.GetFileName(f).StartsWith("pose01_"));
if (idlePap is not null && sitPap is not null && posePap is not null)
{
    var idleBytes = File.ReadAllBytes(idlePap);
    var sitBytes = File.ReadAllBytes(sitPap);
    var poseBytes = File.ReadAllBytes(posePap);
    var idle = PapFile.Read(idleBytes);
    var sit = PapFile.Read(sitBytes);
    var pose = PapFile.Read(poseBytes);

    // pose01 (1 anim) retargeted at sit (1 anim): names + C009 follow
    var swapped = PapRetarget.Retarget(poseBytes, sitBytes, out _);
    var result = PapFile.Read(swapped);
    Check("retarget pose01->sit: info name renamed", result.AnimName(0) == sit.AnimName(0), $"{result.AnimName(0)}");
    Check("retarget pose01->sit: C009 path is the target name", TmbFile.AnimationPaths(result.Tmbs()[0]).Contains(sit.AnimName(0)));
    Check("retarget pose01->sit: havok untouched", result.HavokData.AsSpan().SequenceEqual(pose.HavokData));
    Check("retarget pose01->sit: tmb size field == length", BitConverter.ToInt32(result.Tmbs()[0], 4) == result.Tmbs()[0].Length);

    // sit (1 anim, type 0) retargeted at idle (additive type 15 + base type 0): type match picks the base name
    var baseIndex = Enumerable.Range(0, idle.AnimCount).First(i => idle.AnimType(i) == 0);
    var swapped2 = PapFile.Read(PapRetarget.Retarget(sitBytes, idleBytes, out _));
    Check("retarget sit->idle: matched by type, not index", swapped2.AnimName(0) == idle.AnimName(baseIndex), $"{swapped2.AnimName(0)} (base is {idle.AnimName(baseIndex)})");

    // idle (2 anims) retargeted at pose01 (1 anim): base gets the name, the additive keeps its own
    var swapped3 = PapFile.Read(PapRetarget.Retarget(idleBytes, poseBytes, out var renamed3));
    Check("retarget idle->pose01: base renamed, additive kept", swapped3.AnimName(baseIndex) == pose.AnimName(0) && renamed3.Count == 1,
        $"{swapped3.AnimName(0)} / {swapped3.AnimName(1)}");

    // default: the target's timeline (sounds/effects) with the source's animation length
    var withTargetTmb = PapFile.Read(PapRetarget.Retarget(idleBytes, sitBytes, out _));
    var sitTmb = sit.Tmbs()[0];
    var idleBaseTmb = idle.Tmbs()[baseIndex];
    var outTmb = withTargetTmb.Tmbs()[baseIndex];
    Check("retarget (target timeline): tmb taken from target", outTmb.Length == sitTmb.Length && TmbFile.AnimationPaths(outTmb).SequenceEqual(TmbFile.AnimationPaths(sitTmb)));
    Check("retarget (target timeline): C009 duration is the source's",
        TmbFile.C009Duration(outTmb) == TmbFile.C009Duration(idleBaseTmb) && TmbFile.C009Duration(outTmb) != TmbFile.C009Duration(sitTmb),
        $"{TmbFile.C009Duration(outTmb)} vs source {TmbFile.C009Duration(idleBaseTmb)} / target {TmbFile.C009Duration(sitTmb)}");

    // sound rule: vanilla sample timelines carry no cues of their own, so the target's
    // timeline is what plays; a timeline with cues would be kept instead
    Check("sound rule: vanilla pose01 timeline has no own cues", !TmbFile.HasAudioVisual(pose.Tmbs()[0]));
    Check("sound rule: swapped pap carries the target's timeline", result.Tmbs()[0].Length == sitTmb.Length);
    var longer = PapRetarget.Retarget(poseBytes, swapped, out _); // round trip back through a longer/equal name
    Check("retarget: second retarget still parses", PapFile.Read(longer).AnimCount == 1);
}

// re-save idempotence: baking the live frame f of a previous bake, with f as reference,
// reproduces the previous bake exactly
{
    var rnd = new Random(7);
    Quaternion Q() => Quaternion.Normalize(new Quaternion((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, 1f));
    var vanilla = Enumerable.Range(0, 30).Select(_ => new BoneTransform(Vector3.Zero, Q(), Vector3.One)).ToArray();
    var pose0 = new BoneTransform(Vector3.Zero, Q(), Vector3.One);
    var first = vanilla.Select(v => BakeMath.Bake(pose0, vanilla[0], v)).ToArray();
    const int f = 17;
    var resnap = first[f];
    var second = vanilla.Select(v => BakeMath.Bake(resnap, vanilla[f], v)).ToArray();
    Check("re-save: reference-frame bake is idempotent", Enumerable.Range(0, 30).All(k => QNear(first[k].Rotation, second[k].Rotation)));
    var naive = vanilla.Select(v => BakeMath.Bake(resnap, vanilla[0], v)).ToArray();
    Check("re-save: frame-0 reference would drift", !Enumerable.Range(0, 30).All(k => QNear(first[k].Rotation, naive[k].Rotation)));
}

// vanilla-progress timing for _start corrections
{
    Quaternion Rx(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitX, deg * MathF.PI / 180f);
    // a bone that holds for 10 frames, swings 60 degrees over 10 frames, then holds 10 more
    var track = Enumerable.Range(0, 30).Select(f => Rx(f < 10 ? 0 : f < 20 ? (f - 10) * 6f : 60f)).ToArray();
    var p = BakeMath.ArcProgress(track)!;
    Check("arc: static lead-in stays at 0", p[9] == 0f);
    Check("arc: follows the bone's own motion", p[15] > 0.45f && p[15] < 0.55f);
    Check("arc: done when vanilla is done", p[20] >= 0.999f && p[29] == 1f);
    Check("arc: monotonic", Enumerable.Range(1, 29).All(f => p[f] >= p[f - 1]));
    Check("arc: unmoving bone yields null", BakeMath.ArcProgress(Enumerable.Repeat(Rx(0), 30).ToArray()) is null);
}

// body position (n_hara) carries the snap's translation offset, idempotently
{
    var h0 = new BoneTransform(new Vector3(0, 0.989f, 0), Quaternion.Identity, Vector3.One);
    var hf = new BoneTransform(new Vector3(0, 0.993f, 0.002f), Quaternion.Identity, Vector3.One);
    var sat = new BoneTransform(new Vector3(-0.05f, 0.174f, -0.213f), Quaternion.Identity, Vector3.One);
    var baked = BakeMath.Bake(sat, h0, hf, withTranslation: true);
    Check("hara: translation offset rides on vanilla", VNear(baked.Translation, hf.Translation + (sat.Translation - h0.Translation)));
    Check("hara: frame 0 reproduces the snap position", VNear(BakeMath.Bake(sat, h0, h0, withTranslation: true).Translation, sat.Translation));
    var resnap = baked;
    Check("hara: re-save from frame f is idempotent", VNear(BakeMath.Bake(resnap, hf, h0, withTranslation: true).Translation, BakeMath.Bake(sat, h0, h0, withTranslation: true).Translation));
    Check("other bones: translation stays vanilla", VNear(BakeMath.Bake(sat, h0, hf).Translation, hf.Translation));
}

// root-out ease schedule
var ease = new BakeMath.StaggeredEase(frames: 60, minDepth: 1, maxDepth: 9);
Check("ease: frame 0 untouched at every depth", Enumerable.Range(1, 9).All(d => ease.Weight(d, 0) == 0f));
Check("ease: last frame fully in at every depth", Enumerable.Range(1, 9).All(d => ease.Weight(d, 59) >= 1f));
Check("ease: cascade done by ~55% of the clip", Enumerable.Range(1, 9).All(d => ease.Weight(d, 34) >= 0.999f));
Check("ease: parent leads child", ease.Weight(3, 20) > ease.Weight(6, 20) && ease.Weight(6, 20) >= ease.Weight(9, 20));
Check("ease: monotonic per bone", Enumerable.Range(1, 58).All(f => ease.Weight(5, f + 1) >= ease.Weight(5, f)));
Check("ease: child starts before parent finishes (overlap)", Enumerable.Range(1, 58).Any(f => ease.Weight(3, f) > 0f && ease.Weight(3, f) < 1f && ease.Weight(4, f) > 0f));
var shortEase = new BakeMath.StaggeredEase(frames: 4, minDepth: 2, maxDepth: 2);
Check("ease: degenerate clip still lands", shortEase.Weight(2, 3) >= 1f && shortEase.Weight(2, 0) == 0f);

// bake math identities
var snap = new BoneTransform(new Vector3(1, 2, 3), Quaternion.Normalize(new Quaternion(0.2f, 0.3f, 0.1f, 0.9f)), Vector3.One);
var v0 = new BoneTransform(new Vector3(0.5f, 0, 0), Quaternion.Normalize(new Quaternion(0.1f, 0, 0, 1f)), Vector3.One);
var vf = new BoneTransform(new Vector3(0.6f, 0.1f, 0), Quaternion.Normalize(new Quaternion(0, 0.15f, 0, 1f)), new Vector3(1, 1.01f, 1));

var frame0 = BakeMath.Bake(snap, v0, v0);
Check("bake: frame 0 returns snapped rotation", QNear(frame0.Rotation, snap.Rotation));
Check("bake: translation always vanilla", VNear(frame0.Translation, v0.Translation));

var frameF = BakeMath.Bake(snap, v0, vf);
Check("bake: scale always vanilla", VNear(frameF.Scale, vf.Scale));
var expectedDelta = Quaternion.Normalize(Quaternion.Inverse(v0.Rotation) * vf.Rotation);
Check("bake: delta composes on snap", QNear(frameF.Rotation, Quaternion.Normalize(snap.Rotation * expectedDelta)));

Check("align sign flips antipodal", BakeMath.AlignSign(new Quaternion(0, 0, 0, 1), new Quaternion(0, 0, 0, -1)).W == 1);

Console.WriteLine(failed == 0 ? "\nAll checks passed." : $"\n{failed} check(s) FAILED.");
return failed == 0 ? 0 : 1;

// the timeline offset is the last of the three header offsets (magic, version, count, skeleton, info, havok, timeline)
static int FindTimelineOffset(byte[] pap) => BitConverter.ToInt32(pap, 22);

static bool QNear(Quaternion a, Quaternion b) =>
    MathF.Abs(Quaternion.Dot(a, b)) > 0.99999f;

static bool VNear(Vector3 a, Vector3 b) =>
    (a - b).Length() < 1e-5f;
