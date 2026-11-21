using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lodestone.Core.Cdn;

public sealed record CdnFile(string RelativePath, long Size);

public sealed class CdnSnapshot
{
    internal CdnSnapshot(CdnManifest manifest, CdnSigning signing)
    {
        Manifest = manifest;
        Signing = signing;
    }

    public string Revision => Manifest.Revision;
    public string Fingerprint => Manifest.Fingerprint;
    public IReadOnlyDictionary<string, CdnFile> Files => Manifest.Files;
    internal CdnManifest Manifest { get; }
    internal CdnSigning Signing { get; }
}

internal sealed partial record CdnManifest(
    Uri BaseUri, string Revision, string Fingerprint, FrozenDictionary<string, CdnFile> Files)
{
    internal const string GameId = "100001900";
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static CdnManifest Parse(byte[] bytes)
    {
        using var lines = new StringReader(Utf8.GetString(bytes).TrimStart('\uFEFF'));
        var header = lines.ReadLine()?.Split('|');
        if (header is not [var baseUrl, GameId, var revision]
            || !RevisionPattern().IsMatch(revision)
            || !BaseUrlPattern().IsMatch(baseUrl))
        {
            throw new FormatException("官方修复清单来源或版本无效");
        }

        var files = new Dictionary<string, CdnFile>(StringComparer.OrdinalIgnoreCase);
        while (lines.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            if (line.Split('|') is not [var relative, var length, var fileHash]
                || !long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || !FileHashPattern().IsMatch(fileHash))
            {
                throw new FormatException("官方修复清单中的文件记录无效");
            }

            var path = relative.Replace('\\', '/');
            if (path.Length > 512 || path.Split('/').Any(segment =>
                    segment is "" or "." or ".."
                    || segment.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-' or ' ')))
                || !files.TryAdd(path, new CdnFile(relative, size)))
            {
                throw new FormatException("官方修复清单中的文件路径无效或重复");
            }
        }
        if (files.Count == 0)
            throw new FormatException("官方修复清单为空");

        return new CdnManifest(new Uri(baseUrl.TrimEnd('/')), revision,
            Convert.ToHexString(SHA256.HashData(bytes)), files.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\Ahttps://ff14\.jijiagames\.com/v3client/build/100001900/[0-9]+/apppc/[0-9]+/?\z")]
    private static partial Regex BaseUrlPattern();

    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+){3}\z")]
    private static partial Regex RevisionPattern();

    [GeneratedRegex(@"\A[0-9a-fA-F]{32}\z")]
    private static partial Regex FileHashPattern();
}
