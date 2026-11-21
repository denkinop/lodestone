using System.Buffers.Binary;
using System.Text;
using Lodestone.Core.Lobby;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

internal sealed record ModelPart(int Start, int Count, uint Attributes);
internal sealed record ModelMesh(string Material, float[] Vertices, ushort[] Indices, ModelPart[] Parts,
    string[] Bones, byte[]? Weights, byte[]? Joints, IReadOnlyDictionary<string, (ushort Index, ushort Target)[]> Shapes);

internal sealed record CharacterModel(string[] Attributes, IReadOnlyList<ModelMesh> Meshes)
{
    internal const int VertexStride = 12;
    private const int DeclarationSize = 136;
    private const int MeshSize = 36;

    internal static CharacterModel Read(byte[] data, bool face, CancellationToken token)
    {
        var version = U32(data, 0) & 255;
        var declarations = U16(data, 12);
        var vertexStart = Offset(data, 16);
        var indexStart = Offset(data, 28);
        var stringsHeader = 68 + DeclarationSize * declarations;
        var strings = Field(data, stringsHeader + 8, Offset(data, stringsHeader + 4));
        var header = stringsHeader + 8 + strings.Length;
        var model = Field(data, header, 56);
        var meshCount = U16(model, 4);
        var attributes = U16(model, 6);
        var parts = U16(model, 8);
        var materials = U16(model, 10);
        var bones = U16(model, 12);
        var sets = U16(model, 14);
        var lod = header + 56 + U16(model, 24) * 32;
        var first = U16(data, lod);
        var count = U16(data, lod + 2);
        if (first + count > Math.Min(meshCount, declarations))
            throw new SqPackException("模型 LOD0 指向无效的网格");
        var meshTable = lod + 3 * 60 + ((model[27] & 16) == 0 ? 0 : 3 * 40);
        var attrTable = meshTable + meshCount * MeshSize;
        var partTable = attrTable + attributes * 4 + model[26] * 20;
        var matTable = partTable + parts * 16 + U16(model, 38) * 12;
        var boneTable = matTable + materials * 4;
        var boneSets = boneTable + bones * 4;
        var attrNames = Names(data, attrTable, attributes, strings);
        var matNames = Names(data, matTable, materials, strings);
        var boneNames = Names(data, boneTable, bones, strings);
        var (boneLists, shapeTable) = ReadBoneSets(data, boneSets, version, sets, boneNames);
        var shapes = face ? ReadShapes(data, shapeTable, model, strings) : [];
        var meshes = new List<ModelMesh>(count);
        var verticesRead = 0L;
        for (var i = (int)first; i < first + count; i++)
        {
            token.ThrowIfCancellationRequested();
            var layout = Field(data, meshTable + i * MeshSize, MeshSize);
            var indexCount = Offset(layout, 4);
            if (indexCount == 0)
                continue;
            var vertexCount = U16(layout, 0);
            verticesRead += vertexCount;
            var material = U16(layout, 8);
            var partStart = U16(layout, 10);
            var partCount = U16(layout, 12);
            var boneSet = U16(layout, 14);
            var firstIndex = Offset(layout, 16);
            if (verticesRead > 500_000 || material >= materials || partStart + partCount > parts
                || indexCount > 3_000_000 || indexCount % 3 != 0)
                throw new SqPackException("模型网格的大小或引用无效");
            var rawIndices = Field(data, checked(indexStart + firstIndex * 2), indexCount * 2);
            var indices = new ushort[indexCount];
            for (var j = 0; j < indices.Length; j++)
            {
                indices[j] = U16(rawIndices, j * 2);
                if (indices[j] >= vertexCount)
                    throw new SqPackException("模型三角形索引超出顶点范围");
            }
            var submeshes = new ModelPart[partCount];
            for (var j = 0; j < partCount; j++)
            {
                var offset = partTable + (partStart + j) * 16;
                var start = Offset(data, offset) - firstIndex;
                var length = Offset(data, offset + 4);
                if (start < 0 || start > indexCount - length || start % 3 != 0 || length % 3 != 0)
                    throw new SqPackException("模型子网格超出网格索引范围");
                submeshes[j] = new ModelPart(start, length, U32(data, offset + 8));
            }
            var (vertices, weights, joints) = ReadVertices(data, 68 + i * DeclarationSize, layout,
                vertexStart, vertexCount, token);
            meshes.Add(new ModelMesh(matNames[material], vertices, indices, submeshes,
                boneSet < sets ? boneLists[boneSet] : [], weights, joints,
                shapes.TryGetValue(firstIndex, out var shape) ? shape : new Dictionary<string, (ushort, ushort)[]>()));
        }
        return new CharacterModel(attrNames, meshes);
    }

    private static (float[] Vertices, byte[]? Weights, byte[]? Joints) ReadVertices(
        byte[] data, int declaration, ReadOnlySpan<byte> layout, int vertexStart, int count, CancellationToken token)
    {
        var fields = new (int Kind, int Offset, int Stride)[7];
        var used = Field(data, declaration, DeclarationSize);
        for (var i = 0; i < used.Length; i += 8)
        {
            var stream = used[i];
            if (stream == 255)
                break;
            var usage = used[i + 3];
            if (usage >= fields.Length || used[i + 4] != 0)
                continue;
            if (stream >= 3)
                throw new SqPackException("模型顶点流无效");
            fields[usage] = (used[i + 2], checked(vertexStart + Offset(layout, 20 + 4 * stream) + used[i + 1]),
                layout[32 + stream]);
        }

        var output = new float[count * VertexStride];
        var skinned = fields[1].Stride != 0 && fields[2].Stride != 0;
        var weights = skinned ? new byte[count * 8] : null;
        var joints = skinned ? new byte[count * 8] : null;
        for (var i = 0; i < count; i++)
        {
            if ((i & 1023) == 0)
                token.ThrowIfCancellationRequested();
            var vertex = output.AsSpan(i * VertexStride, VertexStride);
            vertex[4] = 1;
            ReadVector(data, fields[0], i, vertex[..3]);
            ReadVector(data, fields[3], i, vertex[3..6]);
            ReadVector(data, fields[4], i, vertex[6..8]);
            if (fields[6] is (8, var offset, var stride) && stride != 0)
            {
                var packed = Field(data, checked(offset + i * stride), 4);
                for (var axis = 0; axis < 4; axis++)
                    vertex[8 + axis] = (float)(packed[axis] * (2d / 255) - 1);
            }
            if (skinned)
            {
                CopySkin(data, fields[1], i, weights!);
                CopySkin(data, fields[2], i, joints!);
            }
        }
        return (output, weights, joints);
    }

    private static void CopySkin(byte[] data, (int Kind, int Offset, int Stride) field, int index, byte[] target) =>
        Field(data, checked(field.Offset + index * field.Stride), field.Kind == 17 ? 8 : 4)
            .CopyTo(target.AsSpan(index * 8));

    private static void ReadVector(byte[] data, (int Kind, int Offset, int Stride) field, int index, Span<float> target)
    {
        var (components, size) = field.Kind switch
        {
            1 => (2, 4), 2 => (3, 4), 3 => (4, 4), 8 or 16 => (4, 1),
            13 => (2, 2), 14 => (4, 2), 17 => (8, 1), _ => (0, 0)
        };
        if (field.Stride == 0 || components == 0)
            return;
        var value = Field(data, checked(field.Offset + index * field.Stride), components * size);
        for (var i = 0; i < Math.Min(target.Length, components); i++)
        {
            target[i] = size switch { 4 => F32(value, i * 4), 2 => F16(value, i * 2), _ => value[i] };
            if (!float.IsFinite(target[i]))
                throw new SqPackException("模型顶点包含无效数值");
        }
    }

    private static (string[][] Lists, int End) ReadBoneSets(byte[] data, int start, uint version, int count, string[] names)
    {
        var result = new string[count][];
        var end = start + count * (version < 6 ? 132 : 4);
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            var head = start + i * (version < 6 ? 132 : 4);
            var indexes = version < 6 ? head : head + U16(data, head) * 4;
            var length = version < 6 ? Math.Min((int)Field(data, head + 128, 1)[0], 64) : U16(data, head + 2);
            total += length;
            if (total > 1_000_000)
                throw new SqPackException("模型骨骼索引超出限制");
            end = Math.Max(end, (indexes + length * 2 + 3) & ~3);
            var list = result[i] = new string[length];
            for (var j = 0; j < length; j++)
            {
                var index = U16(data, indexes + j * 2);
                list[j] = index < names.Length ? names[index] : "";
            }
        }
        return (result, end);
    }

    private static Dictionary<int, Dictionary<string, (ushort, ushort)[]>> ReadShapes(
        byte[] data, int start, ReadOnlySpan<byte> model, ReadOnlySpan<byte> strings)
    {
        var count = U16(model, 16);
        var meshCount = U16(model, 18);
        var valueCount = U16(model, 20);
        var meshes = start + count * 16;
        var values = meshes + meshCount * 12;
        var result = new Dictionary<int, Dictionary<string, (ushort, ushort)[]>>();
        var total = 0L;
        for (var i = 0; i < count; i++)
        {
            var row = start + i * 16;
            var name = CString(strings, Offset(data, row));
            var first = U16(data, row + 4);
            var length = U16(data, row + 10);
            for (var j = (int)first; j < Math.Min(first + length, meshCount); j++)
            {
                var mesh = meshes + j * 12;
                var index = Offset(data, mesh);
                var value = Offset(data, mesh + 8);
                var pairs = Offset(data, mesh + 4);
                total += pairs;
                if (value > valueCount - pairs || total > 1_000_000)
                    throw new SqPackException("模型变形索引超出范围");
                if (!result.TryGetValue(index, out var shapes))
                    result[index] = shapes = new Dictionary<string, (ushort, ushort)[]>(StringComparer.Ordinal);
                var replacements = shapes[name] = new (ushort, ushort)[pairs];
                for (var k = 0; k < pairs; k++)
                {
                    var entry = values + (value + k) * 4;
                    replacements[k] = (U16(data, entry), U16(data, entry + 2));
                }
            }
        }
        return result;
    }

    internal static float[] CustomizeFace(ModelMesh mesh, float[] vertices, CharacterAppearance appearance)
    {
        var values = appearance.Customize;
        var output = vertices.ToArray();
        foreach (var (slot, option) in new (string, int)[]
        {
            ("brw", values[14]), ("chk", values[18]), ("eye", appearance.EyeShape),
            ("mth", appearance.MouthShape), ("nse", values[17]), ("irs", appearance.SmallIris ? 1 : 0),
            ("etc", appearance.Race == 3 ? values[22] - 1 : 0)
        })
        {
            if (option is < 1 or > 26 || !mesh.Shapes.TryGetValue($"shp_{slot}_{(char)('a' + option - 1)}", out var pairs))
                continue;
            var replacements = new Dictionary<ushort, ushort>();
            foreach (var (index, target) in pairs)
                if (index < mesh.Indices.Length && target < vertices.Length / VertexStride)
                    replacements[mesh.Indices[index]] = target;
            foreach (var (source, target) in replacements)
            {
                var a = source * VertexStride;
                var b = target * VertexStride;
                for (var j = 0; j < VertexStride - 1; j++)
                    output[a + j] += vertices[b + j] - vertices[a + j];
            }
        }
        return output;
    }

    private static string[] Names(byte[] data, int offset, int count, ReadOnlySpan<byte> strings)
    {
        var result = new string[count];
        for (var i = 0; i < count; i++)
            result[i] = CString(strings, Offset(data, offset + i * 4));
        return result;
    }

    internal static string CString(ReadOnlySpan<byte> data, int offset)
    {
        var value = Field(data, offset, data.Length - offset);
        var length = value.IndexOf((byte)0);
        if (length is < 0 or > 1024)
            throw new SqPackException("资源字符串无效");
        return Encoding.ASCII.GetString(value[..length]);
    }

    internal static int Offset(ReadOnlySpan<byte> data, int offset) => checked((int)U32(data, offset));
    internal static float F32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadSingleLittleEndian(Field(data, offset, 4));
    internal static float F16(ReadOnlySpan<byte> data, int offset) => (float)BitConverter.UInt16BitsToHalf(U16(data, offset));
}
