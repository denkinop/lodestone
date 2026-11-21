using System.Numerics;
using System.Text;
using static Lodestone.Core.Assets.CharacterModel;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

internal static class ResidentIdle
{
    internal static ReadOnlySpan<byte> TagMagic => [0x1e, 0x0d, 0xb0, 0xca, 0xce, 0xfa, 0x11, 0xd0];

    internal static Dictionary<int, Matrix4x4> Read(byte[] file)
    {
        if (!file.AsSpan().StartsWith("pap "u8))
            throw Invalid();
        var count = U16(file, 8);
        var info = Offset(file, 14);
        var tag = Offset(file, 18);
        var end = Offset(file, 22);
        if (info < 26 || (long)info + count * 40 > tag || tag > end || end > file.Length)
            throw Invalid();
        int? clip = null;
        for (var i = 0; i < count; i++)
            if (Field(file, info + i * 40, 9).SequenceEqual("cbnm_id0\0"u8))
            {
                clip = U16(file, info + i * 40 + 34);
                break;
            }
        if (clip is null)
            throw Invalid();
        var objects = new TagFile(file.AsMemory(tag, end - tag)).Read();
        var container = objects.FirstOrDefault(item => item.Type == "hkaAnimationContainer").Fields ?? throw Invalid();
        var animationIndex = Ints(container, "animations").ElementAtOrDefault(clip.Value);
        if (animationIndex <= 0 || animationIndex >= objects.Count)
            throw Invalid();
        var (type, animation) = objects[animationIndex];
        if (type != "hkaSplineCompressedAnimation" || animation is null || Number(animation, "endian") != 0)
            throw Invalid();
        var binding = Ints(container, "bindings").Where(i => i > 0 && i < objects.Count)
            .Select(i => objects[i].Fields).FirstOrDefault(fields => fields is not null && Number(fields, "animation") == animationIndex);
        if (binding is null || Number(binding, "blendHint") != 0)
            throw Invalid();

        var data = animation.GetValueOrDefault("data") as byte[] ?? throw Invalid();
        var offsets = Ints(animation, "blockOffsets");
        if (offsets.Length == 0)
            throw Invalid();
        var block = Field(data, offsets[0], (offsets.Length > 1 ? offsets[1] : data.Length) - offsets[0]);
        var tracks = Number(animation, "numberOfTransformTracks");
        var masks = Number(animation, "maskAndQuantizationSize");
        var bones = binding.ContainsKey("transformTrackToBoneIndices")
            ? Ints(binding, "transformTrackToBoneIndices") : tracks is > 0 and <= 1024 ? Enumerable.Range(0, tracks).ToArray() : [];
        if (tracks is < 1 or > 1024 || tracks * 4 > masks || masks > block.Length || bones.Length != tracks)
            throw Invalid();
        var cursor = new Cursor(block.ToArray(), masks);
        var result = new Dictionary<int, Matrix4x4>();
        for (var i = 0; i < tracks; i++)
        {
            var q = block[i * 4];
            var translation = Vector(cursor, block[i * 4 + 1], q & 3, 0);
            var rotation = Rotation(cursor, block[i * 4 + 2], q >> 2 & 15);
            var scale = Vector(cursor, block[i * 4 + 3], q >> 6, 1);
            result[bones[i]] = Transform(translation, rotation, scale);
        }
        return result;
    }

    internal static Matrix4x4 Transform(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        if (!float.IsFinite(translation.X + translation.Y + translation.Z + rotation.X + rotation.Y + rotation.Z
            + rotation.W + scale.X + scale.Y + scale.Z))
            throw Invalid();
        var result = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
        result.Translation = translation;
        return result;
    }

    private static int Number(Dictionary<string, object> fields, string key) => fields.GetValueOrDefault(key) is int value ? value : 0;
    private static int[] Ints(Dictionary<string, object> fields, string key) => fields.GetValueOrDefault(key) is object[] values
        ? values.Select(value => value is int number ? number : throw Invalid()).ToArray() : [];

    private static int SplineCount(Cursor reader)
    {
        var count = (int)reader.UInt(2) + 1;
        var degree = reader.Byte();
        var knots = reader.Take(count + degree + 1);
        if (degree >= count || knots[..(degree + 1)].ContainsAnyExcept((byte)0))
            throw Invalid();
        return count;
    }

    private static Vector3 Vector(Cursor reader, int mask, int quantization, float initial)
    {
        var count = (mask & 0x70) != 0 ? SplineCount(reader) : 0;
        if (count > 0)
            reader.Align(4);
        var vector = new Vector3(initial);
        Span<(float Low, float High)> ranges = stackalloc (float, float)[3];
        for (var i = 0; i < 3; i++)
        {
            if ((mask & (16 << i)) != 0)
                ranges[i] = (reader.Float(), reader.Float());
            else if ((mask & (1 << i)) != 0)
                vector[i] = reader.Float();
        }
        if (count > 0)
        {
            if (quantization > 1)
                throw Invalid();
            var size = quantization + 1;
            var maximum = (1 << (size * 8)) - 1;
            var axes = 0;
            for (var i = 0; i < 3; i++)
            {
                if ((mask & (16 << i)) == 0)
                    continue;
                var (low, high) = ranges[i];
                vector[i] = (float)(low + (high - (double)low) * reader.UInt(size) / maximum);
                axes++;
            }
            reader.Take((count - 1) * size * axes);
        }
        reader.Align(4);
        return vector;
    }

    private static Quaternion Rotation(Cursor reader, int mask, int quantization)
    {
        if (mask == 0)
            return Quaternion.Identity;
        var count = (mask & 0xf0) != 0 ? SplineCount(reader) : 1;
        Quaternion value;
        int stride;
        switch (quantization)
        {
            case 0:
                stride = 4;
                reader.Align(stride);
                var raw = reader.UInt(stride);
                var radius = (raw >> 18 & 1023) / 1023d;
                var scalar = 1 - radius * radius;
                var magnitude = Math.Sqrt(1 - scalar * scalar);
                var angle = raw & 262143;
                var index = (int)Math.Sqrt(angle);
                var phi = index * Math.PI / 1022;
                var theta = index == 0 ? 0 : (angle - (ulong)(index * index)) * Math.PI / (4 * index);
                float Signed(double component, int axis) => (float)((raw & (1ul << (28 + axis))) == 0 ? component : -component);
                value = new Quaternion(Signed(Math.Sin(phi) * Math.Cos(theta) * magnitude, 0),
                    Signed(Math.Sin(phi) * Math.Sin(theta) * magnitude, 1), Signed(Math.Cos(phi) * magnitude, 2), Signed(scalar, 3));
                break;
            case 1:
                stride = 5;
                var packed = reader.UInt(stride);
                value = QuaternionFromThree([(int)(packed & 4095) - 2047, (int)(packed >> 12 & 4095) - 2047,
                    (int)(packed >> 24 & 4095) - 2047], 2047, (int)(packed >> 36 & 3), (packed >> 38 & 1) != 0);
                break;
            case 2:
                stride = 6;
                reader.Align(2);
                var a = (int)reader.UInt(2);
                var b = (int)reader.UInt(2);
                var c = (int)reader.UInt(2);
                value = QuaternionFromThree([(a & 32767) - 16383, (b & 32767) - 16383, (c & 32767) - 16383],
                    16383, (a >> 15) | (b >> 14 & 2), (c >> 15) != 0);
                break;
            case 5:
                stride = 16;
                reader.Align(4);
                value = new Quaternion(reader.Float(), reader.Float(), reader.Float(), reader.Float());
                break;
            default:
                throw Invalid();
        }
        reader.Take((count - 1) * stride);
        reader.Align(4);
        return value;
    }

    private static Quaternion QuaternionFromThree(ReadOnlySpan<int> components, int midpoint, int omitted, bool negative)
    {
        Span<float> values = stackalloc float[4];
        var norm = 0d;
        for (int axis = 0, index = 0; axis < 4; axis++)
        {
            if (axis == omitted)
                continue;
            var v = components[index++] / (midpoint * Math.Sqrt(2));
            values[axis] = (float)v;
            norm += v * v;
        }
        values[omitted] = (float)(Math.Sqrt(Math.Max(0, 1 - norm)) * (negative ? -1 : 1));
        return new Quaternion(values[0], values[1], values[2], values[3]);
    }

    internal static SqPackException Invalid() => new("待机动作或骨骼资源格式无效");

    internal sealed class Cursor(ReadOnlyMemory<byte> data, int start = 0)
    {
        internal int Position { get; private set; } = start;
        internal int Length => data.Length;
        internal ReadOnlySpan<byte> Take(int size)
        {
            var value = Field(data.Span, Position, size);
            Position += size;
            return value;
        }

        internal void Align(int size) => Take((size - Position % size) % size);
        internal byte Byte() => Take(1)[0];
        internal float Float() => F32(Take(4), 0);
        internal ulong UInt(int size)
        {
            if (size > 8)
                throw Invalid();
            ulong value = 0;
            var bytes = Take(size);
            for (var i = 0; i < bytes.Length; i++)
                value |= (ulong)bytes[i] << (8 * i);
            return value;
        }

        internal int Integer()
        {
            ulong value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var part = Byte();
                value |= (ulong)(part & 127) << shift;
                if (part < 128)
                {
                    var magnitude = checked((int)(value >> 1));
                    return (value & 1) == 0 ? magnitude : -magnitude;
                }
            }
            throw Invalid();
        }
    }

    private sealed record Member(string Name, int Kind, int Size, string Target);

    private sealed class TagFile(ReadOnlyMemory<byte> data)
    {
        private readonly Cursor cursor = new(data, 8);
        private readonly List<string> strings = ["", ""];
        private readonly List<(string Name, Member[] Fields)> types = [("", [])];
        private readonly Dictionary<string, Member[]> definitions = new(StringComparer.Ordinal);
        private readonly List<(string Type, Dictionary<string, object>? Fields)> objects = [("", null)];
        private int quota = 1_000_000;

        internal List<(string Type, Dictionary<string, object>? Fields)> Read()
        {
            if (!data.Span.StartsWith(TagMagic))
                throw Invalid();
            while (cursor.Position < cursor.Length && types.Count < 4096 && objects.Count < 4096)
            {
                switch (cursor.Integer())
                {
                    case 1 when cursor.Integer() == 3:
                        break;
                    case 2:
                        AddType();
                        break;
                    case 4:
                        var (name, members) = Type(cursor.Integer());
                        objects.Add((name, Fields(members, ReadFlags(members), 0)));
                        break;
                    case 7:
                        return objects;
                    default:
                        throw Invalid();
                }
            }
            throw Invalid();
        }

        private void AddType()
        {
            var name = String();
            cursor.Integer();
            var parent = Type(cursor.Integer()).Fields;
            var count = cursor.Integer();
            if (count < 0 || count + parent.Length > 64)
                throw Invalid();
            var members = new List<Member>(parent);
            for (var i = 0; i < count; i++)
            {
                var field = String();
                var kind = cursor.Integer();
                var size = (kind & 0x20) != 0 ? cursor.Integer() : 0;
                var target = (kind & 15) is 8 or 9 ? String() : "";
                members.Add(new Member(field, kind, size, target));
            }
            var fields = members.ToArray();
            types.Add((name, fields));
            definitions[name] = fields;
        }

        private (string Name, Member[] Fields) Type(int index) => (uint)index < types.Count ? types[index] : throw Invalid();
        private Member[] Definition(string target) => definitions.TryGetValue(target, out var fields) ? fields : throw Invalid();
        private ulong ReadFlags(Member[] fields) => cursor.UInt((fields.Length + 7) / 8);

        private string String()
        {
            var length = cursor.Integer();
            if (length <= 0)
                return -length < strings.Count ? strings[-length] : throw Invalid();
            if (length > 4096 || strings.Count >= 65536)
                throw Invalid();
            var value = Encoding.UTF8.GetString(cursor.Take(length));
            strings.Add(value);
            return value;
        }

        private Dictionary<string, object> Fields(Member[] members, ulong flags, int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < members.Length; i++)
                if ((flags & (1ul << i)) != 0)
                    result[members[i].Name] = Value(members[i], depth + 1);
            return result;
        }

        private object Value(Member member, int depth)
        {
            if (depth > 24 || --quota < 0)
                throw Invalid();
            var kind = member.Kind & 15;
            if ((member.Kind & 0x10) != 0)
            {
                var count = cursor.Integer();
                if (count < 0 || count > cursor.Length)
                    throw Invalid();
                if (kind == 1)
                    return cursor.Take(count).ToArray();
                if (kind == 2 && cursor.Integer() is not (1 or 2 or 4 or 8))
                    throw Invalid();
                if (kind == 9)
                {
                    var fields = Definition(member.Target);
                    var flags = ReadFlags(fields);
                    if (count > quota)
                        throw Invalid();
                    quota -= count;
                    var values = new object[count];
                    for (var i = 0; i < count; i++)
                        values[i] = Fields(fields, flags, depth + 1);
                    return values;
                }
                return Array(member with { Kind = member.Kind & ~0x10 }, count, depth);
            }
            if ((member.Kind & 0x20) != 0)
                return Array(member with { Kind = kind, Size = 0 }, member.Size, depth);
            return kind switch
            {
                0 or 2 or 8 => cursor.Integer(), 1 => (int)cursor.Byte(), 3 => cursor.Float(),
                4 or 5 => cursor.Take(16).Length, 6 => cursor.Take(48).Length, 7 => cursor.Take(64).Length,
                9 => Fields(Definition(member.Target), ReadFlags(Definition(member.Target)), depth + 1),
                10 => String(), _ => throw Invalid()
            };
        }

        private object[] Array(Member member, int count, int depth)
        {
            if (count < 0 || count > quota)
                throw Invalid();
            var entries = new object[count];
            for (var i = 0; i < count; i++)
                entries[i] = Value(member, depth + 1);
            return entries;
        }
    }
}
