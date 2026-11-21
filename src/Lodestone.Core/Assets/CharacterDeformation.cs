using System.Numerics;
using static Lodestone.Core.Assets.CharacterModel;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

internal sealed class CharacterDeformation(byte[]? data)
{
    private readonly Dictionary<int, RaceDeformer> races = Read(data);

    internal IEnumerable<int> Ancestry(int race)
    {
        var visited = new HashSet<int>();
        while (race != 0 && visited.Add(race))
        {
            yield return race;
            race = races.TryGetValue(race, out var entry) ? entry.Parent : 0;
        }
    }

    internal float[] Apply(ModelMesh mesh, int source, int target, CancellationToken token)
    {
        var vertices = mesh.Vertices;
        if (source == target || mesh.Bones.Length == 0)
            return vertices;
        var inheritance = Ancestry(target).ToArray();
        var count = Array.IndexOf(inheritance, source);
        for (var index = count - 1; index >= 0; index--)
            if (races.TryGetValue(inheritance[index], out var deformer))
                vertices = Transform(mesh, vertices, deformer.Transforms, Matrix4x4.Identity, token);
        return vertices;
    }

    internal static float[] Transform(ModelMesh mesh, float[] vertices, IReadOnlyDictionary<string, Matrix4x4> transforms,
        Matrix4x4 fallback, CancellationToken token)
    {
        var weights = mesh.Weights;
        var joints = mesh.Joints;
        if (weights is null || joints is null || mesh.Bones.Length == 0)
            return vertices;
        var matrices = mesh.Bones.Select(name => transforms.GetValueOrDefault(name, fallback)).ToArray();
        if (matrices.All(matrix => matrix == Matrix4x4.Identity))
            return vertices;
        var result = vertices.ToArray();
        for (var i = 0; i < weights.Length / 8; i++)
        {
            if ((i & 1023) == 0)
                token.ThrowIfCancellationRequested();
            var influence = i * 8;
            var total = 0;
            for (var j = 0; j < 8; j++)
                total += weights[influence + j];
            if (total == 0)
                continue;
            var offset = i * VertexStride;
            var p = Vector(vertices, offset);
            var n = Vector(vertices, offset + 3);
            var b = Vector(vertices, offset + 8);
            var position = Vector3.Zero;
            var normal = Vector3.Zero;
            var binormal = Vector3.Zero;
            for (var j = 0; j < 8; j++)
            {
                var raw = weights[influence + j];
                if (raw == 0)
                    continue;
                var index = joints[influence + j];
                var matrix = index < matrices.Length ? matrices[index] : Matrix4x4.Identity;
                var weight = (float)raw / total;
                position += weight * Vector3.Transform(p, matrix);
                normal += weight * Vector3.TransformNormal(n, matrix);
                binormal += weight * Vector3.TransformNormal(b, matrix);
            }
            Write(result, offset, position);
            Write(result, offset + 3, normal);
            Write(result, offset + 8, binormal);
        }
        return result;
    }

    internal static Vector3 Vector(float[] values, int i) => new(values[i], values[i + 1], values[i + 2]);

    internal static void Write(float[] values, int i, Vector3 vector)
    {
        values[i] = vector.X;
        values[i + 1] = vector.Y;
        values[i + 2] = vector.Z;
    }

    private static Dictionary<int, RaceDeformer> Read(byte[]? data)
    {
        var result = new Dictionary<int, RaceDeformer>();
        if (data is null)
            return result;
        var count = Offset(data, 0);
        if (count > 1024)
            throw new SqPackException("种族变形表超出范围");
        var tree = new Dictionary<int, int>();
        for (var i = 0; i < count; i++)
            tree[U16(data, 6 + i * 12)] = U16(data, 4 + i * 12);
        for (var i = 0; i < count; i++)
        {
            var entry = 4 + i * 12;
            var race = U16(data, entry);
            var node = U16(data, entry + 2);
            var start = Offset(data, entry + 4);
            var parent = tree.GetValueOrDefault(U16(data, 4 + count * 12 + node * 8));
            var transforms = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            if (start != 0)
            {
                var size = Offset(data, start);
                if (size > 1024)
                    throw new SqPackException("种族骨骼变形表超出范围");
                var table = (start + 4 + size * 2 + 3) & ~3;
                for (var j = 0; j < size; j++)
                {
                    var name = CString(data, start + U16(data, start + 4 + j * 2));
                    var bytes = Field(data, table + 48 * j, 48);
                    transforms[name] = new Matrix4x4(
                        F32(bytes, 0), F32(bytes, 16), F32(bytes, 32), 0,
                        F32(bytes, 4), F32(bytes, 20), F32(bytes, 36), 0,
                        F32(bytes, 8), F32(bytes, 24), F32(bytes, 40), 0,
                        F32(bytes, 12), F32(bytes, 28), F32(bytes, 44), 1);
                }
            }
            result[race] = new RaceDeformer(parent, transforms);
        }
        return result;
    }

    private sealed record RaceDeformer(int Parent, Dictionary<string, Matrix4x4> Transforms);
}
