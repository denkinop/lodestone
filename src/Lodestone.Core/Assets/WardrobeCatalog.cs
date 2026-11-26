using System.Globalization;
using System.Text.Json.Serialization;
using Lodestone.Core.Lobby;

namespace Lodestone.Core.Assets;

public sealed record WardrobeItem(
    [property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] int Icon, [property: JsonPropertyName("rarity")] int Rarity,
    [property: JsonPropertyName("itemLevel")] int ItemLevel, [property: JsonPropertyName("equipLevel")] int EquipLevel,
    [property: JsonPropertyName("jobCategory")] string JobCategory, [property: JsonPropertyName("covers")] IReadOnlyList<string> Covers);

public sealed record WardrobePage([property: JsonPropertyName("items")] IReadOnlyList<WardrobeItem> Items,
    [property: JsonPropertyName("more")] bool More);

public sealed record WardrobeChange(string Slot, int? ItemId = null, byte? Stain1 = null, byte? Stain2 = null);

internal sealed record WardrobeOutfit(IReadOnlyList<CharacterEquipment> Equipment,
    IReadOnlyDictionary<string, string> CoveredSlots, IReadOnlySet<string> TriedSlots,
    IReadOnlyDictionary<string, int> DyeChannels);

public sealed class WardrobeCatalog
{
    internal const int FirstSlot = 2;
    private const int PageSize = 36;
    private static readonly int SlotCount = CharacterData.Slots.Length;
    private static readonly int EquipmentMask = ((1 << SlotCount) - 1) & ~((1 << FirstSlot) - 1);
    private readonly List<Entry>[] bySlot = Enumerable.Range(0, SlotCount).Select(_ => new List<Entry>()).ToArray();
    private readonly Dictionary<(int Slot, int Id), Entry> byId = [];
    private readonly Dictionary<(int Slot, ulong Model), int> modelCoverage = [];
    private readonly Dictionary<(int Slot, ulong Model), int> modelDyeChannels = [];
    private readonly HashSet<int> icons = [];
    private readonly HashSet<int> dyeIds;
    public IReadOnlyList<EquipmentStain> Dyes { get; }

    internal WardrobeCatalog(IEnumerable<EquipmentStain> stains)
    {
        Dyes = stains.Where(stain => stain.Id is > 0 and <= byte.MaxValue
            && !stain.Name.StartsWith("染色 ", StringComparison.Ordinal)
            && stain.Color is { Length: 7 } color && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit))
            .OrderBy(stain => stain.Id).ToArray();
        dyeIds = Dyes.Select(stain => stain.Id).ToHashSet();
    }

    internal void Add(int slot, ulong model, EquipmentCandidate candidate, int covered, int dyeChannels)
    {
        if (slot >= SlotCount || (model & 0xffff) == 0 || model > uint.MaxValue || candidate.Id <= 0 || dyeChannels is < 0 or > 2)
            throw new InvalidDataException("EXD 包含无效的衣橱装备");
        covered &= EquipmentMask & ~(1 << slot);
        var details = candidate.Details!;
        var item = new WardrobeItem(candidate.Id, candidate.Name, candidate.Icon, details.Rarity, details.ItemLevel,
            details.EquipLevel, details.JobCategory, SlotsIn(covered).Select(index => CharacterData.Slots[index].Key).ToArray());
        var entry = new Entry(item, model, covered, dyeChannels);
        if (!byId.TryAdd((slot, item.Id), entry))
            throw new InvalidDataException("EXD 包含重复的衣橱装备");
        bySlot[slot].Add(entry);
        if (item.Icon > 0)
            icons.Add(item.Icon);
        var modelKey = (slot, model);
        if (!modelCoverage.TryAdd(modelKey, covered) && modelCoverage[modelKey] != covered)
            modelCoverage[modelKey] = 0;
        modelDyeChannels[modelKey] = Math.Max(modelDyeChannels.GetValueOrDefault(modelKey), dyeChannels);
    }

    internal void Sort()
    {
        foreach (var entries in bySlot)
            entries.Sort((left, right) => right.Item.Id.CompareTo(left.Item.Id));
    }

    internal int CoveredMask(int slot, int itemId, ulong model) =>
        byId.TryGetValue((slot, itemId), out var entry) && entry.Model == model ? entry.Covered : 0;

    internal int DyeChannels(int slot, int itemId, ulong model) =>
        byId.TryGetValue((slot, itemId), out var entry) && entry.Model == model ? entry.DyeChannels : 0;

    public bool HasIcon(int id) => icons.Contains(id);

    public WardrobePage Search(string slot, string? query, int page)
    {
        var index = SlotIndex(slot);
        if (index < FirstSlot || page is < 0 or > int.MaxValue / PageSize)
            return new([], false);
        var term = query?.Trim() ?? "";
        if (int.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
            return page == 0 && byId.TryGetValue((index, itemId), out var entry) ? new([entry.Item], false) : new([], false);
        var items = bySlot[index].Where(entry => term.Length == 0 || entry.Item.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Skip(page * PageSize).Take(PageSize + 1).Select(entry => entry.Item).ToArray();
        return new(items.Length > PageSize ? items[..PageSize] : items, items.Length > PageSize);
    }

    internal WardrobeOutfit Compose(IReadOnlyList<CharacterEquipment> original, IReadOnlyList<WardrobeChange> changes)
    {
        if (changes.Count > SlotCount - FirstSlot)
            throw new SqPackException("试穿部位数量无效");
        var equipment = new List<CharacterEquipment>();
        var coveredSlots = new Dictionary<string, string>(StringComparer.Ordinal);
        var triedSlots = new HashSet<string>(StringComparer.Ordinal);
        var dyeChannels = new Dictionary<string, int>(StringComparer.Ordinal);
        var dyedOriginals = new Dictionary<string, CharacterEquipment>(StringComparer.Ordinal);
        var claimed = 0;
        var changed = 0;

        // Most recent changes take priority; older choices can reappear when the newer choice is restored.
        for (var i = changes.Count - 1; i >= 0; i--)
        {
            var change = changes[i];
            var slot = change is null ? -1 : SlotIndex(change.Slot);
            if (change is null || slot < FirstSlot || (changed & (1 << slot)) != 0)
                throw new SqPackException("试穿部位无效");
            var bit = 1 << slot;
            changed |= bit;
            var hasDye = change.Stain1.HasValue || change.Stain2.HasValue;
            if (change.ItemId is null)
            {
                var worn = original.FirstOrDefault(item => item.Slot == change.Slot && item.Model > 0);
                if (worn is null || !hasDye)
                    throw new SqPackException("该部位没有可染色的装备");
                dyedOriginals.Add(change.Slot, ApplyDyes(worn, change, OriginalDyeChannels(slot, worn)));
                continue;
            }
            if (change.ItemId == 0)
            {
                if (hasDye)
                    throw new SqPackException("无法为已卸下的装备染色");
                claimed |= bit;
                continue;
            }
            if (!byId.TryGetValue((slot, change.ItemId.Value), out var entry))
                throw new SqPackException($"该物品无法试穿于{CharacterData.Slots[slot].Name}");
            var selected = new CharacterEquipment(change.Slot, (ushort)entry.Model, 0, (ushort)(entry.Model >> 16), 0, 0);
            selected = ApplyDyes(selected, change, entry.DyeChannels);
            if ((claimed & (bit | entry.Covered)) != 0)
                continue;
            Apply(selected, slot, entry.Covered, entry.DyeChannels);
            triedSlots.Add(change.Slot);
        }

        var remaining = original.Where(item => item.Model > 0).Select(item =>
        {
            var slot = SlotIndex(item.Slot);
            var covered = modelCoverage.GetValueOrDefault((slot, (ulong)item.Model | (ulong)item.Variant << 16));
            return (Item: item, Slot: slot, Covered: covered);
        });
        foreach (var (item, slot, covered) in remaining.Where(entry => entry.Slot >= FirstSlot).OrderByDescending(entry => entry.Covered != 0))
        {
            if ((claimed & ((1 << slot) | covered)) == 0)
                Apply(dyedOriginals.GetValueOrDefault(item.Slot, item), slot, covered, OriginalDyeChannels(slot, item));
        }
        return new(equipment, coveredSlots, triedSlots, dyeChannels);

        void Apply(CharacterEquipment item, int slot, int covered, int channels)
        {
            equipment.Add(item);
            dyeChannels[item.Slot] = channels;
            claimed |= (1 << slot) | covered;
            foreach (var index in SlotsIn(covered))
                coveredSlots.Add(CharacterData.Slots[index].Key, item.Slot);
        }
    }

    private int OriginalDyeChannels(int slot, CharacterEquipment item) => Math.Max(
        modelDyeChannels.GetValueOrDefault((slot, (ulong)item.Model | (ulong)item.Variant << 16)),
        item.Stain2 > 0 ? 2 : item.Stain1 > 0 ? 1 : 0);

    private CharacterEquipment ApplyDyes(CharacterEquipment item, WardrobeChange change, int channels)
    {
        if (change.Stain1.HasValue && channels < 1 || change.Stain2.HasValue && channels < 2)
            throw new SqPackException("该装备不支持此染色通道");
        if (change.Stain1 is > 0 && !dyeIds.Contains(change.Stain1.Value)
            || change.Stain2 is > 0 && !dyeIds.Contains(change.Stain2.Value))
            throw new SqPackException("染剂不可用");
        return item with { Stain1 = change.Stain1 ?? item.Stain1, Stain2 = change.Stain2 ?? item.Stain2 };
    }

    private static int SlotIndex(string slot) => Array.FindIndex(CharacterData.Slots, value => value.Key == slot);

    private static IEnumerable<int> SlotsIn(int mask)
    {
        for (var index = FirstSlot; index < SlotCount; index++)
            if ((mask & (1 << index)) != 0)
                yield return index;
    }

    private sealed record Entry(WardrobeItem Item, ulong Model, int Covered, int DyeChannels);
}
