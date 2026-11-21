using System.Numerics;
using System.Text;
using static Lodestone.Core.Assets.CharacterModel;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

internal sealed class CharacterRig
{
    private readonly Dictionary<string, Matrix4x4> transforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Matrix4x4> joints = new(StringComparer.Ordinal);
    internal int? IdleRace { get; }
    internal Matrix4x4 Head => transforms.GetValueOrDefault("j_kao", Matrix4x4.Identity);

    private CharacterRig(Bone[] bones, int race, int earLength, Dictionary<string, Matrix4x4> motion, int? idleRace)
    {
        IdleRace = idleRace;
        var bind = Global(bones);
        var fallback = idleRace.HasValue ? [] : Fallback(bones, bind, race is 1101 or 1201);
        var posed = new Matrix4x4[bones.Length];
        var changed = new bool[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var (name, parent, local) = bones[i];
            var animated = motion.TryGetValue(name, out var frame);
            var value = animated ? frame : local;
            if (parent >= 0)
                value *= posed[parent];
            var adjusted = fallback.TryGetValue(name, out var adjustment);
            if (adjusted)
                value = Rotate(value, adjustment);
            var scaleEars = race is 1101 or 1201 && name is "j_mimi_l" or "j_mimi_r";
            if (scaleEars)
                value = Matrix4x4.CreateScale(0.68f + 0.46f * Math.Clamp(earLength, 0, 100) / 100) * value;
            changed[i] = animated || adjusted || scaleEars || parent >= 0 && changed[parent];
            if (changed[i] && Matrix4x4.Invert(bind[i], out var inverse))
                transforms[name] = inverse * value;
            joints[name] = posed[i] = value;
        }
    }

    internal static async Task<CharacterRig?> ReadAsync(SqPackArchive pack, int race, int earLength, CancellationToken token)
    {
        var bones = await ResolveSkeleton(pack, race, token);
        if (bones is null)
            return null;
        var shared = race switch { 301 => 101, 1201 => 1101, _ => 0 };
        foreach (var source in new[] { race, shared })
        {
            if (source == 0)
                break;
            try
            {
                var data = await pack.ReadAsync($"chara/human/c{source:0000}/animation/a0001/bt_common/resident/idle.pap", token);
                if (data is null)
                    continue;
                var frames = ResidentIdle.Read(data);
                var references = source == race ? bones : await ResolveSkeleton(pack, source, token);
                if (references is null || frames.Count == 0 || frames.Keys.Any(index => index < 0 || index >= references.Length))
                    continue;
                var motion = frames.ToDictionary(frame => references[frame.Key].Name, frame => (references[frame.Key].Local, frame.Value));
                var mapped = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
                foreach (var bone in bones)
                {
                    if (!motion.TryGetValue(bone.Name, out var entry))
                        continue;
                    if (source == race)
                        mapped[bone.Name] = entry.Value;
                    else if (Matrix4x4.Invert(entry.Local, out var inverse))
                        mapped[bone.Name] = entry.Value * inverse * bone.Local;
                }
                if (mapped.Count != 0)
                    return new CharacterRig(bones, race, earLength, mapped, source);
            }
            catch (Exception ex) when (ex is SqPackException or OverflowException) { }
        }
        return new CharacterRig(bones, race, earLength, [], null);
    }

    private static async Task<Bone[]?> ResolveSkeleton(SqPackArchive pack, int race, CancellationToken token)
    {
        var data = await pack.ReadAsync($"chara/human/c{race:0000}/skeleton/base/b0001/skl_c{race:0000}b0001.sklb", token);
        return TryParse(data, "n_root"u8);
    }

    private static Bone[]? TryParse(byte[]? data, ReadOnlySpan<byte> rootName)
    {
        if (data is null)
            return null;
        try { return Parse(data, rootName); }
        catch (Exception ex) when (ex is SqPackException or OverflowException) { return null; }
    }

    private static Bone[] Parse(byte[] data, ReadOnlySpan<byte> rootName)
    {
        var start = Offset(data, 12);
        if (!data.AsSpan().StartsWith("blks"u8) || !Field(data, start, 8).SequenceEqual(ResidentIdle.TagMagic))
            throw ResidentIdle.Invalid();
        Span<byte> rootKey = stackalloc byte[rootName.Length + 1];
        rootKey[0] = (byte)(rootName.Length * 2);
        rootName.CopyTo(rootKey[1..]);
        var relative = data.AsSpan(start).IndexOf(rootKey);
        if (relative < 4)
            throw ResidentIdle.Invalid();
        var root = start + relative;
        var flags = data[root - 1];
        if (flags is not (1 or 3))
            throw ResidentIdle.Invalid();
        var countStart = root - 2;
        while (countStart > start && (data[countStart - 1] & 128) != 0)
            countStart--;
        var cursor = new ResidentIdle.Cursor(data, countStart);
        var count = cursor.Integer();
        if (cursor.Position != root - 1 || count is < 2 or > 1024)
            throw ResidentIdle.Invalid();
        var countBytes = data.AsSpan(countStart, root - 1 - countStart);
        Span<byte> parentKey = stackalloc byte[countBytes.Length + 2];
        countBytes.CopyTo(parentKey);
        parentKey[^2] = 8;
        parentKey[^1] = 3;
        var search = Math.Max(start, root - count * 5);
        var parentStart = Field(data, search, countStart - search).LastIndexOf(parentKey);
        if (parentStart < 0)
            throw ResidentIdle.Invalid();
        cursor = new ResidentIdle.Cursor(data, search + parentStart + countBytes.Length + 1);
        var parents = new int[count];
        for (var i = 0; i < count; i++)
        {
            parents[i] = cursor.Integer();
            if (parents[i] < -1 || parents[i] >= i)
                throw ResidentIdle.Invalid();
        }
        if (cursor.Position != countStart)
            throw ResidentIdle.Invalid();
        cursor = new ResidentIdle.Cursor(data, root);
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var length = cursor.Integer();
            if (length is < 1 or > 96)
                throw ResidentIdle.Invalid();
            names[i] = Encoding.ASCII.GetString(cursor.Take(length));
            if ((flags & 2) != 0)
                cursor.Take(1);
        }
        if (cursor.Integer() != count)
            throw ResidentIdle.Invalid();
        var bones = new Bone[count];
        for (var i = 0; i < count; i++)
        {
            var values = cursor.Take(48);
            var translation = new Vector3(F32(values, 0), F32(values, 4), F32(values, 8));
            var rotation = new Quaternion(F32(values, 16), F32(values, 20), F32(values, 24), F32(values, 28));
            var scale = new Vector3(F32(values, 32), F32(values, 36), F32(values, 40));
            bones[i] = new Bone(names[i], parents[i], ResidentIdle.Transform(translation, rotation, scale));
        }
        return bones;
    }

    private static Matrix4x4[] Global(Bone[] bones)
    {
        var result = new Matrix4x4[bones.Length];
        for (var i = 0; i < bones.Length; i++)
            result[i] = bones[i].Parent < 0 ? bones[i].Local : bones[i].Local * result[bones[i].Parent];
        return result;
    }

    internal static async Task<float[][]?> ReadEyesAsync(SqPackArchive pack, int race, int face, Matrix4x4 head,
        CancellationToken token)
    {
        var est = await pack.ReadAsync("chara/xls/charadb/faceskeletontemplate.est", token);
        if (est is { Length: >= 4 })
        {
            var count = U32(est, 0);
            if (count <= (est.Length - 4) / 6)
                for (var i = 0; i < count; i++)
                    if (U16(est, 4 + 4 * i) == face && U16(est, 6 + 4 * i) == race)
                    {
                        face = U16(est, 4 + (int)count * 4 + i * 2);
                        break;
                    }
        }
        if (face == 0)
            return null;
        var data = await pack.ReadAsync($"chara/human/c{race:0000}/skeleton/face/f{face:0000}/skl_c{race:0000}f{face:0000}.sklb", token);
        var bones = TryParse(data, "j_kao"u8);
        if (bones is null)
            return null;
        var global = Global(bones);
        float[]? Find(string side)
        {
            var index = Array.FindIndex(bones, bone => bone.Name == $"j_f_eyepuru_{side}");
            if (index < 0)
                index = Array.FindIndex(bones, bone => bone.Name == $"j_f_eye_{side}");
            if (index < 0)
                return null;
            var point = Vector3.Transform(global[index].Translation, head);
            return [point.X, point.Y, point.Z];
        }
        return Find("l") is { } left && Find("r") is { } right ? [left, right] : null;
    }

    internal float[] Skin(ModelMesh mesh, float[] vertices, string slot, CancellationToken token)
    {
        var fallback = slot is "face" or "hair" or "head" or "ears" or "ears_base" ? Head : Matrix4x4.Identity;
        return CharacterDeformation.Transform(mesh, vertices, fallback == Matrix4x4.Identity ? transforms
            : mesh.Bones.Where(joints.ContainsKey).Distinct().ToDictionary(bone => bone,
                bone => transforms.GetValueOrDefault(bone, Matrix4x4.Identity)), fallback, token);
    }

    internal float[]? Sheathe(ModelMesh mesh, float[] vertices, string slot, int race, CancellationToken token)
    {
        var left = slot == "main_hand";
        if (!joints.TryGetValue(left ? "j_buki_kosi_l" : "j_buki_kosi_r", out var socket))
            return null;
        var shortRace = race is 1101 or 1201;
        var direction = left ? 1 : -1;
        var translation = socket.Translation;
        translation.X += direction * (shortRace ? 0.052f : 0.064f);
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        for (var i = 0; i < vertices.Length; i += VertexStride)
        {
            var point = new Vector2(vertices[i], vertices[i + 1]);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        var size = max - min;
        float scale;
        if (mesh.Bones.Contains("n_page_l") && mesh.Bones.Contains("n_page_r"))
        {
            var hinge = Matrix4x4.CreateScale(1, 0.48f, 1);
            vertices = CharacterDeformation.Transform(mesh, vertices, new Dictionary<string, Matrix4x4>
                { ["n_page_l"] = hinge, ["n_page_r"] = hinge }, Matrix4x4.Identity, token);
            socket = new Matrix4x4(0, .91f, .30f, 0, 0, -.29f, .96f, 0, direction, 0, 0, 0, 0, 0, 0, 1);
            scale = .66f;
        }
        else if (size.Y > size.X * 1.8f)
        {
            socket = new Matrix4x4(-1, -.13f * direction, 0, 0, .13f * direction, -.99f, 0, 0,
                0, 0, 1, 0, 0, 0, 0, 1);
            scale = shortRace ? .35f : .65f;
        }
        else
        {
            socket = Rotate(socket, Matrix4x4.CreateRotationX(Radians(65)));
            scale = shortRace ? .78f : 1;
        }
        socket.Translation = translation;
        var output = vertices.ToArray();
        for (var i = 0; i < vertices.Length; i += VertexStride)
        {
            var p = scale * CharacterDeformation.Vector(vertices, i);
            CharacterDeformation.Write(output, i, Vector3.Transform(p, socket));
            CharacterDeformation.Write(output, i + 3, Vector3.TransformNormal(CharacterDeformation.Vector(vertices, i + 3), socket));
            CharacterDeformation.Write(output, i + 8, Vector3.TransformNormal(CharacterDeformation.Vector(vertices, i + 8), socket));
        }
        return output;
    }

    private static Dictionary<string, Matrix4x4> Fallback(Bone[] bones, Matrix4x4[] bind, bool lalafell)
    {
        var result = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        foreach (var side in new[] { "l", "r" })
        {
            var upper = $"j_ude_a_{side}";
            var elbow = $"j_ude_b_{side}";
            var a = Array.FindIndex(bones, bone => bone.Name == upper);
            var b = Array.FindIndex(bones, bone => bone.Name == elbow);
            if (a < 0 || b < 0)
                continue;
            var delta = bind[b].Translation - bind[a].Translation;
            var direction = delta.X > 0 ? 1 : -1;
            result[upper] = Matrix4x4.CreateRotationZ(Radians(lalafell ? 24 : 19) * direction - MathF.Atan2(delta.X, -delta.Y));
            result[elbow] = Matrix4x4.CreateRotationX(Radians(-5));
            foreach (var finger in new[] { "hito", "naka", "kusuri", "ko" })
            {
                result[$"j_{finger}_a_{side}"] = Matrix4x4.CreateRotationZ(Radians(-11 * direction));
                result[$"j_{finger}_b_{side}"] = Matrix4x4.CreateRotationZ(Radians(-22 * direction));
            }
        }
        foreach (var (name, angle) in new[] { ('a', -36), ('b', -17), ('c', 6), ('d', 16) })
            result[$"n_sippo_{name}"] = Matrix4x4.CreateRotationX(Radians(angle));
        return result;
    }

    private static float Radians(float degrees) => degrees * (MathF.PI / 180);

    private static Matrix4x4 Rotate(Matrix4x4 matrix, Matrix4x4 rotation)
    {
        var result = matrix * rotation;
        result.Translation = matrix.Translation;
        return result;
    }

    private sealed record Bone(string Name, int Parent, Matrix4x4 Local);
}
