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

public sealed record WardrobeChange(string Slot, int ItemId);

internal sealed record WardrobeOutfit(IReadOnlyList<CharacterEquipment> Equipment,
    IReadOnlyDictionary<string, string> CoveredSlots, IReadOnlySet<string> TriedSlots);

public sealed class WardrobeCatalog
{
    internal const int FirstSlot = 2;
    private const int PageSize = 36;
    private static readonly int SlotCount = CharacterData.Slots.Length;
    private static readonly int EquipmentMask = ((1 << SlotCount) - 1) & ~((1 << FirstSlot) - 1);
    private readonly List<Entry>[] bySlot = Enumerable.Range(0, SlotCount).Select(_ => new List<Entry>()).ToArray();
    private readonly Dictionary<(int Slot, int Id), Entry> byId = [];
    private readonly Dictionary<(int Slot, ulong Model), int> modelCoverage = [];
    private readonly HashSet<int> icons = [];

    internal WardrobeCatalog() { }

    internal void Add(int slot, ulong model, EquipmentCandidate candidate, int covered)
    {
        if (slot >= SlotCount || (model & 0xffff) == 0 || model > uint.MaxValue || candidate.Id <= 0)
            throw new InvalidDataException("EXD 包含无效的衣橱装备");
        covered &= EquipmentMask & ~(1 << slot);
        var details = candidate.Details!;
        var item = new WardrobeItem(candidate.Id, candidate.Name, candidate.Icon, details.Rarity, details.ItemLevel,
            details.EquipLevel, details.JobCategory, SlotsIn(covered).Select(index => CharacterData.Slots[index].Key).ToArray());
        var entry = new Entry(item, model, covered);
        if (!byId.TryAdd((slot, item.Id), entry))
            throw new InvalidDataException("EXD 包含重复的衣橱装备");
        bySlot[slot].Add(entry);
        if (item.Icon > 0)
            icons.Add(item.Icon);
        var modelKey = (slot, model);
        if (!modelCoverage.TryAdd(modelKey, covered) && modelCoverage[modelKey] != covered)
            modelCoverage[modelKey] = 0;
    }

    internal void Sort()
    {
        foreach (var entries in bySlot)
            entries.Sort((left, right) => right.Item.Id.CompareTo(left.Item.Id));
    }

    internal int CoveredMask(int slot, int itemId, ulong model) =>
        byId.TryGetValue((slot, itemId), out var entry) && entry.Model == model ? entry.Covered : 0;

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
            if (change.ItemId == 0)
            {
                claimed |= bit;
                continue;
            }
            if (!byId.TryGetValue((slot, change.ItemId), out var entry))
                throw new SqPackException($"该物品无法试穿于{CharacterData.Slots[slot].Name}");
            if ((claimed & (bit | entry.Covered)) != 0)
                continue;
            Apply(new CharacterEquipment(change.Slot, (ushort)entry.Model, 0, (ushort)(entry.Model >> 16), 0, 0), slot, entry.Covered);
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
                Apply(item, slot, covered);
        }
        return new(equipment, coveredSlots, triedSlots);

        void Apply(CharacterEquipment item, int slot, int covered)
        {
            equipment.Add(item);
            claimed |= (1 << slot) | covered;
            foreach (var index in SlotsIn(covered))
                coveredSlots.Add(CharacterData.Slots[index].Key, item.Slot);
        }
    }

    private static int SlotIndex(string slot) => Array.FindIndex(CharacterData.Slots, value => value.Key == slot);

    private static IEnumerable<int> SlotsIn(int mask)
    {
        for (var index = FirstSlot; index < SlotCount; index++)
            if ((mask & (1 << index)) != 0)
                yield return index;
    }

    private sealed record Entry(WardrobeItem Item, ulong Model, int Covered);
}
