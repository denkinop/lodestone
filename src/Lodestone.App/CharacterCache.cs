using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lodestone.Core.Assets;
using Lodestone.Core.Lobby;

namespace Lodestone.App;

internal sealed record CachedRoster(Guid Revision, LobbyCharacter[] Characters);
internal sealed record CharacterChoice(int Index, string Id);
internal sealed record CachedCatalog(ServiceAccount[] Accounts, Dictionary<int, CachedRoster> Rosters,
    int? ViewIndex, CharacterChoice? Choice)
{
    internal CachedCatalog WithRoster(LobbyRoster result)
    {
        var rosters = Rosters.Where(pair => result.Accounts.Any(account => account.Index == pair.Key))
            .ToDictionary();
        if (result.SelectedIndex is { } index)
            rosters[index] = new CachedRoster(Guid.NewGuid(), result.Characters.ToArray());
        var choice = Choice is { } selected && rosters.TryGetValue(selected.Index, out var roster)
            && roster.Characters.Any(character => character.ContentId == selected.Id) ? Choice : null;
        return new(result.Accounts.ToArray(), rosters, result.SelectedIndex, choice);
    }
}

internal sealed record PreviewCharacter(LobbyCharacter Character, Guid Session, Guid Revision, string Directory, CharacterData Data)
{
    internal string PortraitRoot => $"/_game_preview/portrait/{Session:D}/{Character.ContentId}/{Revision:D}/";
}

internal sealed class CharacterCache(string dataDirectory)
{
    internal string AccountPath(string account, int area) => Path.Combine(dataDirectory, "characters-v1",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{account}:{area}")))));

    internal static CachedCatalog? Read(string directory)
    {
        try
        {
            var file = new FileInfo(Path.Combine(directory, "roster.json"));
            if (!file.Exists || file.Length > 8 << 20)
                return null;
            return JsonSerializer.Deserialize<CachedCatalog>(File.ReadAllBytes(file.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void Save(string directory, CachedCatalog catalog) =>
        Write(Path.Combine(directory, "roster.json"), output => JsonSerializer.Serialize(output, catalog));

    internal static void Write(string path, Action<Stream> write)
    {
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = File.Create(temporary))
                write(output);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
