using System.Text.RegularExpressions;
using static Lodestone.Core.Assets.CharacterModel;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

internal sealed partial record CharacterMaterial(string Shader, Dictionary<string, string> Maps, float[][] Palette,
    float AlphaCutoff, float[]? DiffuseTint, float[]? SpecularTint, float[]? ScleraTint, float[]? EyeReflection,
    float[]? EyeEmission)
{
    private uint[] DyeRows { get; init; } = [];
    internal bool UsesLegacyDyeTable { get; private init; }
    internal bool HasDyeRows => DyeRows.Any(row => (row & 0x3f) != 0);

    internal static CharacterMaterial Read(byte[] data)
    {
        var dataset = U16(data, 6);
        var length = U16(data, 8);
        var shaderOffset = U16(data, 10);
        var header = Field(data, 12, 4);
        var start = 16 + 4 * (header[0] + header[1] + header[2]);
        var strings = Field(data, start, length);
        var shader = CString(strings, shaderOffset);
        var maps = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < header[0]; index++)
        {
            var path = CString(strings, U16(data, 16 + index * 4));
            var suffix = TextureSuffix().Match(path).Groups[1].Value;
            var role = suffix switch
            {
                "base" or "d" => "base", "norm" or "n" => "normal", "mask" or "m" => "mask",
                "id" => "index", "spec" or "s" => "specular", _ => null
            };
            if (role is not null && path.StartsWith("chara/", StringComparison.Ordinal))
                maps.TryAdd(role, path);
        }

        var table = start + length + header[3];
        Field(data, table, dataset);
        var rows = dataset >= 2048 ? 32 : dataset >= 512 ? 16 : 0;
        var legacy = shader == "characterlegacy.shpk" || rows == 16;
        var palette = new float[rows][];
        for (var row = 0; row < rows; row++)
        {
            var values = Field(data, table + row * rows * 2, rows * 2);
            var roughness = legacy ? 1 - F16(values, 6) / 16 : F16(values, 32);
            var metalness = legacy ? 0 : F16(values, 36);
            var effect = rows == 32 && float.IsFinite(F16(values, 48)) ? (int)F16(values, 48) : 0;
            palette[row] =
            [
                Color(F16(values, 0)), Color(F16(values, 2)), Color(F16(values, 4)), Unit(roughness),
                Color(F16(values, 8)), Color(F16(values, 10)), Color(F16(values, 12)), Unit(metalness),
                Color(F16(values, 16)), Color(F16(values, 18)), Color(F16(values, 20)),
                effect is 0 or 7 or 10 or 12 or >= 17 ? 1 : 0
            ];
        }

        var constants = Constants(data, table + dataset);
        var dyeOffset = table + rows * rows * 2;
        var dyeBytes = table + dataset - dyeOffset;
        uint[] dyeRows = dyeBytes switch
        {
            128 when rows == 32 => Enumerable.Range(0, 32).Select(row => U32(data, dyeOffset + row * 4)).ToArray(),
            32 when rows >= 16 => Enumerable.Range(0, 16).Select(row => (uint)U16(data, dyeOffset + row * 2)).ToArray(),
            _ => [],
        };
        return new CharacterMaterial(shader, maps, palette,
            Unit(constants.TryGetValue(699138595, out var alpha) ? alpha[0] : 0.45f),
            Tint(constants, 0x2C2A34DD), Tint(constants, 0x141722D5), Tint(constants, 0x11C90091),
            constants.TryGetValue(0xCB0338DC, out var reflection) && reflection.Length == 3 ? reflection.Select(Color).ToArray() : null,
            Tint(constants, 0x38A64362)) { DyeRows = dyeRows, UsesLegacyDyeTable = dyeBytes == 32 };
    }

    internal void ApplyDyes(StainingTemplate table, int firstStain, int secondStain)
    {
        Span<float> values = stackalloc float[3];
        var usesLegacyShading = Palette.Length == 16 || Shader == "characterlegacy.shpk";
        for (var row = 0; row < DyeRows.Length; row++)
        {
            var entry = DyeRows[row];
            var template = (int)(UsesLegacyDyeTable ? entry >> 5 : (entry >> 16) & 0x7ff);
            var channel = UsesLegacyDyeTable ? 0 : (entry >> 27) & 3;
            var stain = channel switch { 0 => firstStain, 1 => secondStain, _ => 0 };
            if (template == 0 || stain == 0)
                continue;
            // Dye flags select which material values the game template replaces.
            for (var component = 0; component < 3; component++)
            {
                if ((entry & (1u << component)) == 0 || !table.TryRead(template, stain, component, values))
                    continue;
                if (component == 1 && values[0] == 0 && values[1] == 0 && values[2] == 0)
                    continue;
                for (var index = 0; index < 3; index++)
                    Palette[row][component * 4 + index] = Color(values[index]);
            }
            if (UsesLegacyDyeTable && usesLegacyShading && (entry & 8) != 0 && table.TryRead(template, stain, 3, values))
                Palette[row][3] = Unit(1 - values[0] / 16);
            if (!UsesLegacyDyeTable && !usesLegacyShading)
            {
                if ((entry & 16) != 0 && table.TryRead(template, stain, 4, values))
                    Palette[row][7] = Unit(values[0]);
                if ((entry & 32) != 0 && table.TryRead(template, stain, 5, values))
                    Palette[row][3] = Unit(values[0]);
            }
        }
    }

    private static Dictionary<uint, float[]> Constants(byte[] data, int offset)
    {
        var result = new Dictionary<uint, float[]>();
        if (offset > data.Length - 12)
            return result;
        var size = U16(data, offset);
        var entries = offset + 12 + 8 * U16(data, offset + 2);
        var count = U16(data, offset + 4);
        var values = entries + 8 * count + 12 * U16(data, offset + 6);
        if (values > data.Length - size)
            return result;
        for (var i = 0; i < count; i++)
        {
            var entry = entries + 8 * i;
            var key = U32(data, entry);
            if (key is not (699138595 or 0x2C2A34DD or 0x141722D5 or 0x11C90091 or 0xCB0338DC or 0x38A64362))
                continue;
            var start = U16(data, entry + 4);
            var length = U16(data, entry + 6);
            if (length == 0 || length % 4 != 0 || start + length > size)
                continue;
            var components = new float[Math.Min(3, length / 4)];
            for (var j = 0; j < components.Length; j++)
                components[j] = F32(data, values + start + j * 4);
            result[key] = components;
        }
        return result;
    }

    private static float Color(float value) => float.IsFinite(value) ? MathF.Round(Math.Clamp(value, 0, 8), 4) : 0;
    private static float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private static float[]? Tint(Dictionary<uint, float[]> constants, uint key) =>
        constants.TryGetValue(key, out var color) && color.Length == 3
            ? color.Select(value => Color(value) * Color(value)).ToArray() : null;

    internal static IEnumerable<string> Paths(string model, string material, int variant)
    {
        var name = material.TrimStart('/');
        if (name.Length == 0 || !MaterialName().IsMatch(name))
            yield break;
        var root = model[..model.LastIndexOf("/model/", StringComparison.Ordinal)];
        var human = HumanStem().Match(name);
        if (human.Success)
        {
            var kind = human.Groups[2].Value;
            var category = kind switch { "b" => "body", "f" => "face", "h" => "hair", "t" => "tail", _ => "zear" };
            root = $"chara/human/c{human.Groups[1].Value}/obj/{category}/{kind}{human.Groups[3].Value}";
            if (kind == "f")
            {
                yield return $"{root}/material/{name}";
                yield break;
            }
            variant = 1;
        }
        var directory = $"{root}/material/v{Math.Max(1, variant):0000}";
        yield return $"{directory}/{name}";
        yield return $"{root}/material/{name}";
        var weapon = WeaponStem().Match(name);
        if (model.StartsWith("chara/weapon/", StringComparison.Ordinal) && weapon.Success)
        {
            yield return $"chara/weapon/{weapon.Groups[1].Value}/obj/body/{weapon.Groups[2].Value}/material/v{Math.Max(1, variant):0000}/{name}";
            yield return $"{directory}/mt_{model.Split('/')[2]}{name[8..]}";
        }
    }

    [GeneratedRegex(@"_(base|norm|mask|id|spec|[dnms])(?:_\d+)?\.tex$")]
    private static partial Regex TextureSuffix();
    [GeneratedRegex(@"^mt_c(\d{4})([bfhtz])(\d{4})")]
    private static partial Regex HumanStem();
    [GeneratedRegex(@"^mt_(w\d{4})(b\d{4})")]
    private static partial Regex WeaponStem();
    [GeneratedRegex(@"\A[a-zA-Z0-9_.-]+\.mtrl\z")]
    private static partial Regex MaterialName();
}
