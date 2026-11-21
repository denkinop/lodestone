using System.Text.RegularExpressions;

namespace Lodestone.Core.Cdn;

public enum SqPackCategory
{
    Character,
    Ui,
}

public static partial class SqPackCatalog
{
    public static IEnumerable<KeyValuePair<string, CdnFile>> Files(CdnSnapshot snapshot, SqPackCategory category)
    {
        var prefix = category switch
        {
            SqPackCategory.Character => "04",
            SqPackCategory.Ui => "06",
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
        foreach (var entry in snapshot.Files)
        {
            var match = ArchivePath().Match(entry.Key);
            if (match.Success && match.Groups[1].Value == prefix && entry.Value.Size > 0)
                yield return entry;
        }
    }

    [GeneratedRegex(@"\Agame/sqpack/(?:ffxiv|ex[0-9]+)/(04|06)[0-9a-f]{4}\.win32\.(?:index2?|dat[0-9]+)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchivePath();
}
