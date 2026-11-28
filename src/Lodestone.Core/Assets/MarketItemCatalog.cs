using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lodestone.Core.Assets;

public sealed record MarketItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] int Icon);

public sealed class MarketItemCatalog
{
    internal static MarketItemCatalog Empty { get; } = new([]);
    private readonly Dictionary<int, MarketItem> items;

    private MarketItemCatalog(IEnumerable<MarketItem> entries) => items = entries.ToDictionary(item => item.Id);

    internal static MarketItemCatalog Read(JsonElement rows) => new(rows.EnumerateArray().Select(row =>
        new MarketItem(row[0].GetInt32(), row[1].GetString() ?? "", row[2].GetInt32())));

    public MarketItem Get(int id) => items.GetValueOrDefault(id) ?? new(id, $"物品 #{id}", 0);

    public MarketItem[] Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 80)
            return [];
        query = query.Trim().Normalize(NormalizationForm.FormKC);
        if (int.TryParse(query.TrimStart('#'), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var id) && id > 0)
            return [Get(id)];

        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        return items.Values.Select(item => (Item: item, Match: item.Name.IndexOf(query, comparison)))
            .Where(result => result.Match >= 0)
            .OrderBy(result => string.Equals(result.Item.Name, query, comparison) ? 0 : result.Match == 0 ? 1 : 2)
            .ThenBy(result => result.Item.Name.Length)
            .ThenBy(result => result.Item.Id)
            .Take(30).Select(result => result.Item).ToArray();
    }
}
