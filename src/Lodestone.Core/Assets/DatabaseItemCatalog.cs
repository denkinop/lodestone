using System.Text.Json;

namespace Lodestone.Core.Assets;

internal sealed record DatabaseItem(int Id, string Name, int Icon, int ItemLevel);

internal sealed class DatabaseItemCatalog
{
    private const int NoMatch = 4;
    internal static DatabaseItemCatalog Empty { get; } = new([]);
    private readonly Dictionary<int, DatabaseItem> items;

    private DatabaseItemCatalog(IEnumerable<DatabaseItem> items) => this.items = items.ToDictionary(item => item.Id);

    internal static DatabaseItemCatalog Read(JsonElement rows) => new(rows.EnumerateArray().Select(row =>
        new DatabaseItem(row[0].GetInt32(), row[1].GetString() ?? "", row[2].GetInt32(), row[3].GetInt32())));

    internal DatabaseItem? Find(int id) => items.GetValueOrDefault(id);

    internal DatabaseItem[] Search(string query, int limit) => items.Values.Select(item => (Item: item, Rank: MatchRank(item.Name, query)))
        .Where(result => result.Rank < NoMatch)
        .OrderBy(result => result.Rank).ThenBy(result => result.Item.Name.Length).ThenBy(result => result.Item.Id)
        .Take(limit).Select(result => result.Item).ToArray();

    internal static int MatchRank(string name, string query)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        if (string.Equals(name, query, comparison)) return 0;
        if (name.EndsWith(query, comparison)) return 1;
        if (name.StartsWith(query, comparison)) return 2;
        return name.Contains(query, comparison) ? 3 : NoMatch;
    }
}
