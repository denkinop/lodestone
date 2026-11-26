using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Lodestone.Core.Lobby;
using static Lodestone.Core.Assets.CharacterModel;
using static Lodestone.Core.Assets.SqPackFormat;

namespace Lodestone.Core.Assets;

public sealed record SceneMesh(string Slot, string Material, bool UnderHead, bool Attached, string Vertices, string Indices);
public sealed record SceneMaterial(string Shader, IReadOnlyDictionary<string, string> Maps, float[][] Palette, bool IsFace,
    string?[] Dyes, float AlphaCutoff = 0.45f, float[]? DiffuseTint = null, float[]? SpecularTint = null,
    float[]? ScleraTint = null, float[]? EyeReflection = null, float[]? EyeEmission = null);
public sealed record SceneGear([property: JsonPropertyName("slot")] string Slot, [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("model")] int Model, [property: JsonPropertyName("variant")] int Variant,
    [property: JsonPropertyName("loaded")] bool Loaded, [property: JsonPropertyName("names")] string[] Names,
    [property: JsonPropertyName("candidates")] IReadOnlyList<EquipmentCandidate> Candidates,
    [property: JsonPropertyName("stains")] EquipmentStain?[] Stains);
public sealed record SceneData(IReadOnlyList<SceneMesh> Meshes, IReadOnlyDictionary<string, SceneMaterial> Materials,
    IReadOnlyDictionary<string, string> Colors, IReadOnlyList<SceneGear> Gear, bool Posing, int? IdleRace,
    float[] EyePlane, float[][]? EyeCenters, float[] FaceForward, IReadOnlyDictionary<string, int> Appearance,
    string[] Warnings, string ResourceSource, bool HeadHidden);
public sealed record SceneAssets(SceneData Scene, IReadOnlyDictionary<string, byte[]> Textures);
public sealed record WardrobeScene(SceneAssets Assets, IReadOnlyDictionary<string, string> CoveredSlots, IReadOnlySet<string> AppliedSlots);

public sealed class CharacterScene(SqPackArchive pack)
{
    private const long MaxSceneBytes = 64L << 20;
    private static readonly int[] Races = [0, 101, 501, 1101, 701, 901, 1301, 1501, 1701];

    public Task<SceneAssets> BuildAsync(LobbyCharacter character, string revision, CancellationToken token) =>
        BuildAsync(character, revision, CharacterData.Instance, token);

    public Task<SceneAssets> BuildAsync(LobbyCharacter character, string revision, CharacterData data, CancellationToken token) =>
        BuildAsync(character, revision, data, null, token);

    public async Task<WardrobeScene> BuildWardrobeAsync(LobbyCharacter character, string revision, CharacterData data,
        IReadOnlyList<WardrobeChange> changes, CancellationToken token)
    {
        var outfit = data.Wardrobe.Compose(character.Equipment, changes);
        var assets = await BuildAsync(character with { Equipment = outfit.Equipment }, revision, data, outfit, token);
        return new(assets, outfit.CoveredSlots, outfit.TriedSlots);
    }

    private async Task<SceneAssets> BuildAsync(LobbyCharacter character, string revision, CharacterData data,
        WardrobeOutfit? outfit, CancellationToken token)
    {
        if (character.Appearance is not { Customize.Count: >= 26 } appearance || appearance.Race is < 1 or > 8
            || appearance.Gender is < 0 or > 1)
            throw new SqPackException("角色缺少可识别的外观数据");
        var custom = appearance.Customize;
        var race = Races[appearance.Race] + 100 * appearance.Gender + (appearance.Race == 1 && appearance.Tribe == 2 ? 200 : 0);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var faceTask = ReadFaceAsync();
        var baseParts = new List<Task<Part>> { ReadPartAsync("hair", HumanPath(race, "hair", custom[6], "hir")) };
        if (appearance.Race is 4 or 6 or 7)
            baseParts.Add(ReadPartAsync("tail", HumanPath(race, "tail", Math.Max(1, custom[22]), "til")));
        if (appearance.Race == 8)
            baseParts.Add(ReadPartAsync("ears_base", HumanPath(race, "zear", Math.Max(1, custom[22]), "zer")));
        var basePartsTask = Task.WhenAll(baseParts);
        var deformerTask = pack.ReadAsync("chara/xls/bonedeformer/human.pbd", token);
        var rigTask = CharacterRig.ReadAsync(pack, race, custom[21], token);
        var colorsTask = pack.ReadAsync("chara/xls/charamake/human.cmp", token);
        await Task.WhenAll(faceTask, basePartsTask, deformerTask, rigTask, colorsTask);
        var (face, facePart) = await faceTask;
        var parts = new List<Part> { facePart };
        parts.AddRange(await basePartsTask);
        if (appearance.Glasses.Any(id => id != 0))
            warnings.Add("当前预览未加载面饰");
        if (appearance.FacePaint != 0)
            warnings.Add("当前预览未加载面彩");

        var deformer = new CharacterDeformation(await deformerTask);
        var equipped = character.Equipment.ToDictionary(item => item.Slot, StringComparer.Ordinal);
        var equipment = new List<Task<(Part? Part, string? Warning)>>();
        for (var index = 0; index < CharacterData.Slots.Length; index++)
        {
            var slot = CharacterData.Slots[index];
            if (outfit?.CoveredSlots.ContainsKey(slot.Key) == true)
                continue;
            var item = equipped.GetValueOrDefault(slot.Key) ?? new CharacterEquipment(slot.Key, 0, 0, 0, 0, 0);
            if (item.Model == 0 && index is not (>= 3 and <= 6))
                continue;
            equipment.Add(ResolveSlotAsync(item, index));
        }

        var rig = await rigTask;
        if (rig?.IdleRace is null)
            warnings.Add(rig is null ? "骨骼资源不可用，已使用模型原始姿势" : "默认待机动作不可用，已使用备用站姿");
        var head = rig?.Head ?? Matrix4x4.Identity;
        var eyesTask = CharacterRig.ReadEyesAsync(pack, race, face, head, token);
        var equipmentTask = Task.WhenAll(equipment);
        await Task.WhenAll(equipmentTask, eyesTask);
        foreach (var (part, warning) in await equipmentTask)
        {
            if (part is not null)
                parts.Add(part);
            if (warning is not null)
                warnings.Add(warning);
        }
        var eyes = await eyesTask;
        var colors = ReadColors(await colorsTask, appearance);
        if (colors.Count == 0)
            warnings.Add("角色调色表缺失");
        var flags = await ReadHeadFlags(parts.Any(part => part.Slot == "head") ? equipped.GetValueOrDefault("head") : null, token);
        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var maps = new Dictionary<(string Path, bool Nearest), string?>();
        var materials = new Dictionary<string, SceneMaterial>(StringComparer.Ordinal);
        var meshes = new List<SceneMesh>();
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        long meshBytes = 0;
        long textureBytes = 0;
        foreach (var part in parts)
        {
            var bytes = part.Model;
            if (bytes is null)
            {
                ReportModelFailure(part, "模型缺失");
                continue;
            }
            CharacterModel model;
            try { model = Read(bytes, part.Slot == "face", token); }
            catch (Exception ex) when (part.Slot != "face" && ex is (SqPackException or OverflowException))
            {
                ReportModelFailure(part, "无法解析模型");
                continue;
            }
            foreach (var mesh in model.Meshes)
            {
                var groups = IndexGroups(mesh, model.Attributes, part, appearance, flags);
                if (groups.All(group => group.Indices.Length == 0))
                    continue;
                var paths = CharacterMaterial.Paths(part.Path, mesh.Material, part.Material).ToArray();
                var key = $"{paths.FirstOrDefault() ?? part.Path + '/' + mesh.Material}:{part.Stain1}:{part.Stain2}:{part.Slot == "face"}";
                if (!materials.TryGetValue(key, out var material))
                    materials[key] = material = await ResolveMaterial(paths, part, mesh.Material, LoadTexturesAsync, warnings, data, token);
                if (material.Shader == "characterocclusion.shpk")
                    continue;
                var vertices = deformer.Apply(mesh, part.Source, part.Target, token);
                if (part.Slot == "face")
                    vertices = CustomizeFace(mesh, vertices, appearance);
                var weapon = part.Slot is "main_hand" or "off_hand";
                if (!weapon && rig is not null)
                    vertices = rig.Skin(mesh, vertices, part.Slot, token);
                var posed = weapon ? rig?.Sheathe(mesh, vertices, part.Slot, race, token) : null;
                var encoded = FloatBase64(vertices);
                var attached = posed is null ? null : FloatBase64(posed);
                foreach (var (hidden, triangles) in groups)
                {
                    if (triangles.Length == 0)
                        continue;
                    var indices = IndexBase64(triangles);
                    meshBytes += (encoded.Length + indices.Length) + (attached is null ? 0 : attached.Length + indices.Length);
                    if (meshBytes > MaxSceneBytes)
                        throw new SqPackException("角色模型超出预览容量");
                    meshes.Add(new SceneMesh(part.Slot, key, hidden, false, encoded, indices));
                    if (attached is not null)
                        meshes.Add(new SceneMesh(part.Slot, key, hidden, true, attached, indices));
                    else if (weapon)
                        warnings.Add("部分武器仅可单独查看");
                    loaded.Add(part.Slot);
                }
            }
        }
        if (!loaded.Contains("face"))
            throw new SqPackException("该角色没有可显示的面部网格");

        var gear = DescribeGear(character.Equipment, loaded, data);
        return new SceneAssets(new SceneData(meshes, materials, colors, gear, rig is not null, rig?.IdleRace,
            [head.M11, head.M12, head.M13, -Vector3.Dot(new Vector3(head.M11, head.M12, head.M13), head.Translation)],
            eyes, [head.M31, head.M32, head.M33], new Dictionary<string, int>
            {
                ["height"] = custom[3], ["face"] = custom[5], ["hair"] = custom[6], ["skin_tone"] = custom[8],
                ["hair_tone"] = custom[10], ["highlights"] = custom[11], ["right_eye_color"] = custom[9],
                ["left_eye_color"] = custom[15], ["race_feature_type"] = custom[22], ["face_paint_id"] = appearance.FacePaint
            }, warnings.ToArray(), revision, true), textures);

        void ReportModelFailure(Part part, string message)
        {
            if (outfit?.TriedSlots.Contains(part.Slot) == true)
                throw new SqPackException($"{CharacterData.Slots.First(slot => slot.Key == part.Slot).Name}装备{message}");
            warnings.Add($"{message} · {FileName(part.Path)}");
        }

        async Task<(int Face, Part Part)> ReadFaceAsync()
        {
            var index = custom[5];
            if (appearance.Race != 1 && appearance.Tribe % 2 == 0 && index < 100)
            {
                var preferred = await ReadPartAsync("face", HumanPath(race, "face", index + 100, "fac"));
                if (preferred.Model is not null)
                    return (index + 100, preferred);
            }
            var part = await ReadPartAsync("face", HumanPath(race, "face", index, "fac"));
            return part.Model is not null ? (index, part) : throw new SqPackException("当前游戏资源缺少该角色的面部模型");
        }

        async Task<Part> ReadPartAsync(string slot, string path) => new(slot, path, await pack.ReadAsync(path, token));

        async Task<(Part? Part, string? Warning)> ResolveSlotAsync(CharacterEquipment item, int index)
        {
            var slot = CharacterData.Slots[index];
            var part = await ResolveEquipment(item, index, race, deformer, token);
            if (part is not null)
                return (part, null);
            if (outfit?.TriedSlots.Contains(slot.Key) == true)
                throw new SqPackException($"{slot.Name}缺少适用于当前角色的模型");
            return (null, item.Model != 0 ? $"{slot.Name}模型缺失" : null);
        }

        async Task<Dictionary<string, string>> LoadTexturesAsync(Dictionary<string, string> source)
        {
            var missing = source.Select(pair => (Path: pair.Value, Nearest: pair.Key == "index"))
                .Where(key => !maps.ContainsKey(key)).Distinct();
            foreach (var batch in missing.Chunk(3))
            {
                var downloaded = await Task.WhenAll(batch.Select(key => DownloadTextureAsync(key.Path)));
                for (var index = 0; index < batch.Length; index++)
                {
                    var key = batch[index];
                    var (texture, warning) = downloaded[index];
                    string? id = null;
                    if (warning is not null)
                        warnings.Add(warning);
                    if (texture is not null)
                    {
                        // Decode one at a time to keep the memory used by large textures bounded.
                        try
                        {
                            var png = CharacterTexture.ToPng(texture, key.Nearest, token);
                            if (textureBytes + png.Length > MaxSceneBytes)
                                throw new SqPackException("贴图超出预览容量");
                            id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{key.Nearest}:{key.Path}")))[..32];
                            textures.Add(id, png);
                            textureBytes += png.Length;
                        }
                        catch (Exception ex) when (ex is SqPackException or OverflowException or ArgumentException or InvalidDataException)
                        {
                            warnings.Add($"贴图无法读取 · {FileName(key.Path)}（{ex.Message}）");
                        }
                    }
                    maps[key] = id;
                }
            }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (role, path) in source)
                if (maps[(path, role == "index")] is { } id)
                    result.Add(role, id);
            return result;
        }

        async Task<(byte[]? Data, string? Warning)> DownloadTextureAsync(string path)
        {
            try
            {
                if (await pack.ReadAsync(path, token) is { } tex)
                    return (tex, null);
                return (null, $"贴图缺失 · {FileName(path)}");
            }
            catch (Exception ex) when (ex is SqPackException or OverflowException or ArgumentException or InvalidDataException)
            {
                return (null, $"贴图无法读取 · {FileName(path)}（{ex.Message}）");
            }
        }
    }

    public static SceneGear[] DescribeGear(IReadOnlyList<CharacterEquipment> equipment, IReadOnlySet<string>? loaded = null) =>
        DescribeGear(equipment, loaded, CharacterData.Instance);

    public static SceneGear[] DescribeGear(IReadOnlyList<CharacterEquipment> equipment, IReadOnlySet<string>? loaded, CharacterData data)
    {
        var equipped = equipment.ToDictionary(item => item.Slot, StringComparer.Ordinal);
        return CharacterData.Slots.Select((slot, index) =>
        {
            var item = equipped.GetValueOrDefault(slot.Key);
            var candidates = item is { Model: > 0 } ? data.Candidates(index, item) : [];
            return new SceneGear(slot.Key, slot.Name, item?.Model ?? 0, item?.Variant ?? 0, loaded?.Contains(slot.Key) ?? false,
                candidates.Select(candidate => candidate.Name).Distinct().ToArray(), candidates,
                [data.Stain(item?.Stain1 ?? 0), data.Stain(item?.Stain2 ?? 0)]);
        }).ToArray();
    }

    public async Task<byte[]?> ReadIconAsync(int id, CancellationToken token)
    {
        if (id is < 1 or > 999999)
            return null;
        var directory = id / 1000 * 1000;
        var path = FormattableString.Invariant($"ui/icon/{directory:000000}/{id:000000}");
        try
        {
            if (await pack.ReadAsync(path + "_hr1.tex", token) is { } highResolution)
                return CharacterTexture.ToPng(highResolution, false, token);
        }
        catch (SqPackException) { }
        var data = await pack.ReadAsync(path + ".tex", token);
        return data is null ? null : CharacterTexture.ToPng(data, false, token);
    }

    private async Task<Part?> ResolveEquipment(CharacterEquipment item, int index, int race, CharacterDeformation deform, CancellationToken token)
    {
        var slot = CharacterData.Slots[index];
        if (index < 2)
        {
            if (item.ModelType == 0)
                return null;
            var root = $"chara/weapon/w{item.Model:0000}/obj/body/b{item.ModelType:0000}";
            var path = $"{root}/model/w{item.Model:0000}b{item.ModelType:0000}.mdl";
            var imc = ReadImc($"{root}/b{item.ModelType:0000}.imc", 0, item.Variant, token);
            var model = pack.ReadAsync(path, token);
            await Task.WhenAll(imc, model);
            var (material, mask) = await imc;
            return new Part(slot.Key, path, await model, Material: material, Mask: mask,
                Stain1: item.Stain1, Stain2: item.Stain2);
        }
        var category = index <= 6 ? "equipment" : "accessory";
        var stem = $"{category[0]}{item.Model:0000}";
        var directory = $"chara/{category}/{stem}";
        foreach (var source in deform.Ancestry(race))
        {
            var path = $"{directory}/model/c{source:0000}{stem}_{slot.Suffix}.mdl";
            if (await pack.ReadAsync(path, token) is not { } model)
                continue;
            var (material, mask) = await ReadImc($"{directory}/{stem}.imc", slot.Imc, item.Variant, token);
            return new Part(slot.Key, path, model, source, race, material, mask, item.Stain1, item.Stain2);
        }
        return null;
    }

    private async Task<(int Material, int Mask)> ReadImc(string path, int slot, int variant, CancellationToken token)
    {
        var data = await pack.ReadAsync(path, token);
        if (data is null || data.Length < 4)
            return (1, 1023);
        var count = U16(data, 0);
        var mask = U16(data, 2);
        if ((mask & (1 << slot)) == 0)
            return (1, 1023);
        var index = Math.Min(variant, count) * BitOperations.PopCount((uint)mask)
            + BitOperations.PopCount((uint)(mask & ((1 << slot) - 1)));
        var offset = 4 + index * 6;
        return (Field(data, offset, 1)[0], U16(data, offset + 2) & 1023);
    }

    private async Task<ulong> ReadHeadFlags(CharacterEquipment? head, CancellationToken token)
    {
        if (head is null || head.Model == 0)
            return 0;
        var data = await pack.ReadAsync("chara/xls/equipmentparameter/equipmentparameter.eqp", token);
        var block = head.Model / 160;
        if (data is null || block >= 64)
            return 0;
        var blocks = BinaryPrimitives.ReadUInt64LittleEndian(Field(data, 0, 8));
        var bit = 1ul << block;
        if ((blocks & bit) == 0)
            return 0;
        var offset = (BitOperations.PopCount(blocks & (bit - 1)) * 160 + head.Model % 160) * 8;
        var flags = BinaryPrimitives.ReadUInt64LittleEndian(Field(data, offset, 8));
        return (flags & (1ul << 40)) != 0 ? flags : 0;
    }

    internal static (bool Hidden, ushort[] Indices)[] IndexGroups(ModelMesh mesh, string[] attributes, Part part,
        CharacterAppearance appearance, ulong head)
    {
        bool Has(int bit) => (head & (1ul << bit)) != 0;
        var hideHair = Has(42) && !Has(43);
        var hideScalp = Has(41) && !Has(43);
        var hidePart = head != 0 && (part.Slot == "hair" && hideHair || part.Slot == "neck" && !Has(45)
            || part.Slot == "ears" && hideHair && !Has(47));
        if (mesh.Parts.Length == 0)
            return [(hidePart, mesh.Indices)];
        var visible = new List<ushort>();
        var hidden = new List<ushort>();
        foreach (var sub in mesh.Parts)
        {
            var discard = false;
            var conceal = hidePart;
            for (var index = 0; index < Math.Min(attributes.Length, 32); index++)
            {
                if ((sub.Attributes & (1u << index)) == 0)
                    continue;
                var attr = attributes[index];
                if (Masked(attr, "atr_tv_", part.Mask))
                    discard = true;
                if (part.Slot == "hair" && hideScalp && attr == "atr_kam")
                    conceal = true;
                if (part.Slot != "face")
                    continue;
                if (Masked(attr, "atr_fv_", appearance.Customize[12]))
                    discard = true;
                var (trait, bit) = appearance.Race switch { 4 or 7 => ("atr_top", 51), 6 => ("atr_hrn", 52), _ => ("atr_mim", 50) };
                if (head != 0 && (attr == trait && !Has(bit) || attr == "atr_nek" && Has(44)))
                    conceal = true;
            }
            if (!discard)
                (conceal ? hidden : visible).AddRange(mesh.Indices.AsSpan(sub.Start, sub.Count));
        }
        return [(false, visible.ToArray()), (true, hidden.ToArray())];
    }

    private static bool Masked(string attr, string prefix, int mask) => attr.Length > prefix.Length
        && attr.StartsWith(prefix, StringComparison.Ordinal) && attr[^1] is >= 'a' and <= 'z'
        && (mask & (1 << (attr[^1] - 'a'))) == 0;

    private async Task<SceneMaterial> ResolveMaterial(string[] paths, Part part, string name,
        Func<Dictionary<string, string>, Task<Dictionary<string, string>>> load,
        SortedSet<string> warnings, CharacterData catalog, CancellationToken token)
    {
        var face = part.Slot == "face";
        string?[] dyes = [catalog.StainColor(part.Stain1), catalog.StainColor(part.Stain2)];
        foreach (var path in paths)
        {
            if (await pack.ReadAsync(path, token) is not { } data)
                continue;
            CharacterMaterial parsed;
            try { parsed = CharacterMaterial.Read(data); }
            catch (Exception ex) when (ex is SqPackException or OverflowException)
            {
                warnings.Add($"材质无法解析 · {FileName(path)}");
                continue;
            }
            var maps = parsed.Shader == "characterocclusion.shpk" ? new Dictionary<string, string>() : await load(parsed.Maps);
            return new SceneMaterial(parsed.Shader, maps, parsed.Palette, face, dyes, parsed.AlphaCutoff,
                parsed.DiffuseTint, parsed.SpecularTint, parsed.ScleraTint, parsed.EyeReflection, parsed.EyeEmission);
        }
        warnings.Add($"材质缺失 · {name.TrimStart('/')}");
        return new SceneMaterial("preview", new Dictionary<string, string>(), [], face, dyes);
    }

    private static Dictionary<string, string> ReadColors(byte[]? data, CharacterAppearance appearance)
    {
        if (data is null || data.Length < 178 * 1024)
            return [];
        var row = 18 + ((Math.Clamp(appearance.Tribe, 1, 16) - 1) * 2 + appearance.Gender) * 5;
        var custom = appearance.Customize;
        string Hex(int start, int size = 3) => "#" + Convert.ToHexStringLower(data.AsSpan(start, size));
        var hair = (row + 1) * 1024 + (custom[10] & 255) * 8;
        var colors = new Dictionary<string, string>
        {
            ["skin"] = Hex((row + 3) * 1024 + (custom[8] & 255) * 4), ["hair"] = Hex(hair), ["hairSheen"] = Hex(hair + 4),
            ["highlight"] = appearance.Highlights ? Hex((custom[11] & 255) * 4) : Hex(hair),
            ["eyeRight"] = Hex((custom[9] & 255) * 4), ["eyeLeft"] = Hex((custom[15] & 255) * 4)
        };
        if (appearance.Lipstick && appearance.Race != 7)
            colors["lips"] = Hex(2 * 1024 + (custom[20] & 255) * 4, 4);
        return colors;
    }

    private static string HumanPath(int race, string category, int index, string suffix) =>
        $"chara/human/c{race:0000}/obj/{category}/{category[0]}{index:0000}/model/c{race:0000}{category[0]}{index:0000}_{suffix}.mdl";
    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string FloatBase64(float[] values)
    {
        if (BitConverter.IsLittleEndian)
            return Convert.ToBase64String(MemoryMarshal.AsBytes(values.AsSpan()));
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return Convert.ToBase64String(bytes);
    }

    private static string IndexBase64(ushort[] indices)
    {
        if (BitConverter.IsLittleEndian)
            return Convert.ToBase64String(MemoryMarshal.AsBytes(indices.AsSpan()));
        var bytes = new byte[indices.Length * 2];
        for (var i = 0; i < indices.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), indices[i]);
        return Convert.ToBase64String(bytes);
    }

    internal sealed record Part(string Slot, string Path, byte[]? Model = null, int Source = 0, int Target = 0,
        int Material = 1, int Mask = 1023, int Stain1 = 0, int Stain2 = 0);
}
