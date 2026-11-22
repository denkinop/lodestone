using System.Text.Json;
using System.Text.Json.Serialization;
using Lodestone.Core.Lobby;

namespace Lodestone.Core.Assets;

public sealed record EquipmentCandidate([property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("icon")] int Icon)
{
    [JsonPropertyName("details")]
    public EquipmentDetails? Details { get; init; }
}

public sealed record EquipmentDetails(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("jobCategory")] string JobCategory,
    [property: JsonPropertyName("rarity")] int Rarity,
    [property: JsonPropertyName("itemLevel")] int ItemLevel,
    [property: JsonPropertyName("equipLevel")] int EquipLevel,
    [property: JsonPropertyName("physicalDamage")] int PhysicalDamage,
    [property: JsonPropertyName("magicalDamage")] int MagicalDamage,
    [property: JsonPropertyName("physicalDefense")] int PhysicalDefense,
    [property: JsonPropertyName("magicalDefense")] int MagicalDefense,
    [property: JsonPropertyName("attributes")] EquipmentAttribute[] Attributes);

public sealed record EquipmentAttribute([property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] int Value);

public sealed record EquipmentStain([property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("color")] string? Color);

public sealed class CharacterData
{
    internal static CharacterData Instance => ExdCatalog.Bundled.Character;
    private static readonly string[] Tribes = ["", "中原之民", "高地之民", "森林之民", "黑影之民", "平原之民", "沙漠之民",
        "逐日之民", "护月之民", "北洋之民", "红焰之民", "晨曦之民", "暮晖之民", "掠日之民", "迷失之民", "雷纳族", "维纳族"];

    internal static readonly (string Key, string Name, string Suffix, int Imc)[] Slots =
    [
        ("main_hand", "主手", "", 0), ("off_hand", "副手", "", 0), ("head", "头部", "met", 0),
        ("body", "身体", "top", 1), ("hands", "手部", "glv", 2), ("legs", "腿部", "dwn", 3),
        ("feet", "脚部", "sho", 4), ("ears", "耳饰", "ear", 0), ("neck", "项链", "nek", 1),
        ("wrists", "手镯", "wrs", 2), ("left_ring", "左戒指", "ril", 4), ("right_ring", "右戒指", "rir", 3)
    ];

    private readonly Dictionary<(int Slot, ulong Model), List<EquipmentCandidate>> candidates = [];
    private readonly Dictionary<int, EquipmentStain> stains = [];
    private readonly Dictionary<ushort, string> worlds = [];
    private readonly Dictionary<int, string> guardianDeities = [];

    internal CharacterData(JsonElement data)
    {
        foreach (var value in data.GetProperty("items").EnumerateArray())
        {
            var candidate = new EquipmentCandidate(value[0].GetInt32(), ReadName(value[1]), value[2].GetInt32())
            {
                Details = ReadDetails(value[4]),
            };
            foreach (var model in value[3].EnumerateArray())
            {
                var key = (model[0].GetInt32(), model[1].GetUInt64());
                if (!candidates.TryGetValue(key, out var entry))
                    candidates[key] = entry = [];
                if (entry.Count == 0 || entry[^1].Id != candidate.Id)
                    entry.Add(candidate);
            }
        }
        foreach (var value in data.GetProperty("stains").EnumerateArray())
        {
            var id = value[0].GetInt32();
            stains.Add(id, new EquipmentStain(id, ReadName(value[1]), value[2].GetString()));
        }
        foreach (var value in data.GetProperty("worlds").EnumerateArray())
            worlds[value[0].GetUInt16()] = ReadName(value[1]);
        foreach (var value in data.GetProperty("guardianDeities").EnumerateArray())
            guardianDeities[value[0].GetInt32()] = ReadName(value[1]);

        if (candidates.Count == 0 || stains.Count == 0 || worlds.Count == 0 || guardianDeities.Count == 0)
            throw new InvalidDataException("EXD 缺少角色或装备数据");

        foreach (var entries in candidates.Values)
            entries.Sort((left, right) =>
            {
                var obsolete = left.Name.StartsWith("过期", StringComparison.Ordinal)
                    .CompareTo(right.Name.StartsWith("过期", StringComparison.Ordinal));
                return obsolete != 0 ? obsolete : left.Id.CompareTo(right.Id);
            });
    }

    private static string ReadName(JsonElement value) => value.GetString() is { Length: > 0 } name
        ? name : throw new InvalidDataException("EXD 缺少角色或装备名称");

    private static EquipmentDetails ReadDetails(JsonElement data) => new(data[0].GetString() ?? "", data[1].GetString() ?? "",
        data[2].GetInt32(), data[3].GetInt32(), data[4].GetInt32(), data[5].GetInt32(),
        data[6].GetInt32(), data[7].GetInt32(), data[8].GetInt32(), data[9].EnumerateArray()
            .Select(value => new EquipmentAttribute(ReadName(value[0]), value[1].GetInt32())).ToArray());

    internal EquipmentStain? Stain(int id) => id == 0 ? null
        : stains.GetValueOrDefault(id) ?? new EquipmentStain(id, $"染色 {id}", null);

    public static string? GetWorldName(ushort id) => Instance.WorldName(id);
    public static string? GetGuardianName(int? id) => Instance.GuardianName(id);
    public static string? GetTribeName(int? id) => id is > 0 && id < Tribes.Length ? Tribes[id.Value] : null;

    public string? WorldName(ushort id) => worlds.GetValueOrDefault(id);
    public string? GuardianName(int? id) => id.HasValue ? guardianDeities.GetValueOrDefault(id.Value) : null;
    public string? StainColor(int id) => stains.GetValueOrDefault(id)?.Color;

    internal IReadOnlyList<EquipmentCandidate> Candidates(int slot, CharacterEquipment item)
    {
        ulong model = item.Model;
        model |= slot < 2 ? (ulong)item.ModelType << 16 | (ulong)item.Variant << 32 : (ulong)item.Variant << 16;
        return candidates.TryGetValue((slot, model), out var result) ? result : [];
    }
}
