using System.Globalization;
using System.Text.Json;
using Lodestone.Core.Assets;

namespace Lodestone.Core.Database;

public sealed partial class GarlandDatabaseClient
{
    private static GarlandDetail Describe(GarlandEntry entry, JsonElement record, JsonElement doc, string? location,
        (decimal X, decimal Y)? coordinates, GarlandMap? map)
    {
        var fields = new List<GarlandField>();
        var links = new List<GarlandLinks>();
        var partials = ArrayOf(Get(doc, "partials")).Select(ReadPartial).OfType<GarlandEntry>()
            .DistinctBy(item => (item.Type, item.Id)).ToDictionary(item => (item.Type, item.Id));
        var ingredients = Get(doc, "ingredients");
        if (ingredients.ValueKind == JsonValueKind.Object)
        {
            foreach (var ingredient in ingredients.EnumerateObject())
                if (Text(ingredient.Value, "id") is { } id && ReadEntry("item", id, ingredient.Value) is { } item)
                    partials.TryAdd((item.Type, item.Id), item);
        }
        var used = new HashSet<(string Type, string Id)> { (entry.Type, entry.Id) };

        void Add(string label, string? value)
        {
            if (Clean(value) is { } text)
                fields.Add(new(label, text));
        }

        void Link(string label, string type, IEnumerable<JsonElement> values)
        {
            var entries = new List<GarlandEntry>();
            foreach (var value in values)
            {
                var id = Scalar(value) ?? Text(value, "id") ?? Text(value, "item");
                if (id is null || !ValidId(type, id) || id == entry.Id && type == entry.Type
                    || entries.Any(item => item.Id == id))
                    continue;
                var item = partials.GetValueOrDefault((type, id));
                if (item is null && type == "item" && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId)
                    && ExdCatalog.Bundled.MarketItems.Find(itemId) is { } known)
                    item = new(type, id, known.Name, known.Icon, null);
                item ??= new(type, id, $"#{id}", 0, null);
                var count = Text(value, "num") ?? Text(value, "amount");
                entries.Add(count is null ? item : item with { Summary = $"数量 {count}" });
                used.Add((type, id));
                if (entries.Count == 24)
                    break;
            }
            if (entries.Count > 0)
                links.Add(new(label, entries.ToArray()));
        }

        if (entry.Type == "patch")
        {
            Add("版本系列", Text(record, "series"));
            var patches = Get(record, "patches");
            if (patches.ValueKind == JsonValueKind.Object)
            {
                foreach (var patch in patches.EnumerateObject().Reverse())
                {
                    if (patch.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    var count = patch.Value.EnumerateObject().Where(category => category.Value.ValueKind == JsonValueKind.Array)
                        .Sum(category => category.Value.GetArrayLength());
                    Add(patch.Name, $"{count} 个条目");
                }
            }
            return new(entry, null, fields.ToArray(), []);
        }

        Add("版本", Positive(record, "patch"));
        Add("地点", location);
        Add("等级", Text(record, "lvl"));
        if (coordinates is { } point)
            Add("坐标", FormattableString.Invariant($"X {point.X} · Y {point.Y}"));
        switch (entry.Type)
        {
            case "quest":
                var reqs = Get(record, "reqs");
                var reward = Get(record, "reward");
                Add("任务等级", Positive(ArrayOf(Get(reqs, "jobs")).FirstOrDefault(), "lvl"));
                Add("经验", Positive(reward, "xp"));
                Add("金币", Positive(reward, "gil"));
                Link("接取 NPC", "npc", [Get(record, "issuer")]);
                Link("交付 NPC", "npc", [Get(record, "target")]);
                Link("前置任务", "quest", ArrayOf(Get(reqs, "quests")));
                Link("后续任务", "quest", ArrayOf(Get(record, "next")));
                Link("任务奖励", "item", ArrayOf(Get(reward, "items")));
                Link("解锁副本", "instance", [Get(reward, "instance")]);
                break;
            case "item":
                Add("品级", Text(record, "ilvl"));
                Add("装备等级", Positive(record, "elvl"));
                Add("适用职业", Text(record, "jobCategories"));
                Add("堆叠上限", Positive(record, "stackSize"));
                Add("可交易", Get(record, "tradeable").ValueKind == JsonValueKind.Undefined
                    ? null : Positive(record, "tradeable") is not null ? "是" : "否");
                var recipe = ArrayOf(Get(record, "craft")).FirstOrDefault();
                Add("制作等级", Positive(recipe, "lvl"));
                Link("制作材料", "item", ArrayOf(Get(recipe, "ingredients")));
                Link("采集点", "node", ArrayOf(Get(record, "nodes")));
                Link("钓鱼点", "fishing", ArrayOf(Get(record, "fishing")));
                Link("掉落怪物", "mob", ArrayOf(Get(record, "drops")));
                Link("宝箱来源", "item", ArrayOf(Get(record, "treasure")));
                break;
            case "achievement":
                Add("成就点数", Text(record, "points"));
                Add("称号", Text(record, "title"));
                Link("成就奖励", "item", [Get(record, "item")]);
                break;
            case "instance":
                Add("副本类型", Text(record, "category"));
                Add("最低等级", Positive(record, "min_lvl"));
                Add("最低品级", Positive(record, "min_ilvl"));
                if (Positive(record, "time") is { } minutes) Add("限时", minutes + " 分钟");
                Link("解锁任务", "quest", [Get(record, "unlockedByQuest")]);
                Link("副本奖励", "item", ArrayOf(Get(record, "rewards")));
                break;
            case "leve":
                Add("委托人", Text(record, "client"));
                Add("经验", Positive(record, "xp"));
                Add("金币", Positive(record, "gil"));
                Link("理符发行人", "npc", [Get(record, "levemete")]);
                Link("理符奖励", "item", ArrayOf(Get(Get(doc, "rewards"), "entries")));
                break;
            case "npc":
                Link("关联任务", "quest", ArrayOf(Get(record, "quests")));
                break;
            case "fate":
            case "node":
            case "fishing":
                Add("同步等级", Positive(record, "maxlvl"));
                Link(entry.Type == "fate" ? "奖励物品" : "可获得物品", "item", ArrayOf(Get(record, "items")));
                break;
            case "action":
                Add("射程", Positive(record, "range"));
                Add("咏唱时间", Seconds(record, "cast"));
                Add("复唱时间", Seconds(record, "recast"));
                break;
        }
        var otherEntries = partials.Values.Where(item => !used.Contains((item.Type, item.Id)) && item.Name != entry.Name)
            .DistinctBy(item => (item.Type, item.Name)).Take(16).ToArray();
        if (otherEntries.Length > 0)
            links.Add(new("关联条目", otherEntries));

        return new(entry, Clean(Text(record, "description")), fields.ToArray(), links.ToArray(), map);
    }

    private static string? Positive(JsonElement value, string key) => Text(value, key) is { } text
        && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number > 0 ? text : null;

    private static string? Seconds(JsonElement value, string key) =>
        decimal.TryParse(Text(value, key), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            ? (milliseconds / 1000).ToString("0.###", CultureInfo.InvariantCulture) + " 秒" : null;

    private static (decimal X, decimal Y)? ReadCoordinates(JsonElement entry)
    {
        var coords = ArrayOf(Get(entry, "coords")).Take(2).Select(Scalar).ToArray();
        var x = coords.ElementAtOrDefault(0) ?? Text(entry, "x");
        var y = coords.ElementAtOrDefault(1) ?? Text(entry, "y");
        return decimal.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var xValue)
            && decimal.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var yValue)
            ? (xValue, yValue) : null;
    }
}
