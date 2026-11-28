using System.Text.Json;

namespace Lodestone.Core.Assets;

internal sealed record GameMapLocation(int Id, int PlaceId, int SubPlaceId, string Name, string SubName, bool IsDefault)
{
    internal string DisplayName => SubName.Length > 0 ? SubName : Name;
}

public sealed class GameMapCatalog
{
    internal static GameMapCatalog Empty { get; } = new([]);
    private readonly GameMapLocation[] maps;
    private readonly ILookup<int, GameMapLocation> byPlace;
    private readonly ILookup<int, GameMapLocation> bySubPlace;

    private GameMapCatalog(GameMapLocation[] maps)
    {
        this.maps = maps;
        byPlace = maps.Where(map => map.PlaceId > 0).ToLookup(map => map.PlaceId);
        bySubPlace = maps.Where(map => map.SubPlaceId > 0).ToLookup(map => map.SubPlaceId);
    }

    internal static GameMapCatalog Read(JsonElement rows) => new(rows.EnumerateArray().Select(row =>
        new GameMapLocation(row[0].GetInt32(), row[1].GetInt32(), row[2].GetInt32(),
            row[3].GetString() ?? "", row[4].GetString() ?? "", row[5].GetBoolean())).ToArray());

    internal GameMapLocation? Find(int zone, int area, string? name)
    {
        if (zone > 0)
        {
            if (area > 0 && Select(bySubPlace[area].Where(map => map.PlaceId == zone)) is { } floor)
                return floor;
            if (Select(byPlace[zone].Where(map => map.SubPlaceId == 0)) is { } mainMap)
                return mainMap;
            if (Select(bySubPlace[zone]) is { } subMap)
                return subMap;
        }
        if (area > 0 && Select(byPlace[area].Where(map => map.SubPlaceId == 0).Concat(bySubPlace[area])) is { } areaMap)
            return areaMap;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var named = maps.Where(map => string.Equals(map.DisplayName, name.Trim(), StringComparison.Ordinal)).ToArray();
        return named.Select(map => map.PlaceId).Distinct().Take(2).Count() == 1 ? Select(named) : null;
    }

    private static GameMapLocation? Select(IEnumerable<GameMapLocation> matches)
    {
        var candidates = matches.ToArray();
        if (candidates.Length == 1)
            return candidates[0];
        var defaults = candidates.Where(map => map.IsDefault).Take(2).ToArray();
        return defaults.Length == 1 ? defaults[0] : null;
    }
}
