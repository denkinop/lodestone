using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lodestone.Core.Cdn;

public sealed record GameVersion(string Revision, string VersionInfo, bool FromCache);
public readonly record struct VersionDownloadProgress(long BytesRead, long TotalBytes);

public sealed partial class GameVersionService(OfficialCdnClient cdn, string cacheDirectory)
{
    private const string Executable = "ffxiv_dx11.exe";
    private const string ExecutablePath = "game/" + Executable;
    private const int MaximumExpansion = 5;
    private const int LobbyFieldLimit = 144;
    private const int ChunkSize = 1 << 20;
    private const int ConcurrentChunks = 4;
    private const int MaximumVersionFileSize = 1024;
    private const int MaximumCacheSize = 4096;
    private readonly SemaphoreSlim refreshLock = new(1);
    private readonly string cacheFile = Path.Combine(cacheDirectory, "version-info.json");

    public async Task<GameVersion> RefreshAsync(
        Action<VersionDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await cdn.GetSnapshotAsync(cancellationToken);
            if (!snapshot.Files.TryGetValue(ExecutablePath, out var executable) || executable.Size == 0)
                throw new OfficialCdnException("官方修复清单缺少游戏 EXE");
            var versions = GetVersionFiles(snapshot);
            var cached = await ReadCacheAsync(snapshot, executable.Size, versions.Count, cancellationToken);
            if (cached is not null)
                return new GameVersion(snapshot.Revision, cached.VersionInfo, FromCache: true);

            var suffix = await Task.WhenAll(versions.Select(async file =>
            {
                var bytes = await cdn.ReadRangeAsync(snapshot, file.Path, 0, file.Size, cancellationToken);
                var value = CdnManifest.Utf8.GetString(bytes).TrimStart('\uFEFF').Trim();
                if (!ExpansionVersion().IsMatch(value))
                    throw new OfficialCdnException($"官方资料片版本文件格式无效（{file.Path}）");
                return value[..value.LastIndexOf('.')];
            }));

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long bytesRead = 0;
            progress?.Invoke(new VersionDownloadProgress(bytesRead, executable.Size));
            while (bytesRead < executable.Size)
            {
                var downloads = new List<Task<byte[]>>(ConcurrentChunks);
                for (var offset = bytesRead; downloads.Count < ConcurrentChunks && offset < executable.Size;)
                {
                    var size = (int)Math.Min(ChunkSize, executable.Size - offset);
                    downloads.Add(cdn.ReadRangeAsync(snapshot, ExecutablePath, offset, size, cancellationToken));
                    offset += size;
                }
                // WhenAll returns blocks in file order even when the HTTP responses arrive out of order.
                foreach (var block in await Task.WhenAll(downloads))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha.AppendData(block);
                    bytesRead += block.Length;
                    progress?.Invoke(new VersionDownloadProgress(bytesRead, executable.Size));
                }
            }

            var digest = Convert.ToHexStringLower(sha.GetHashAndReset());
            var prefix = FormattableString.Invariant($"{Executable}/{bytesRead}/{digest}");
            var version = string.Join('+', suffix.Prepend(prefix));
            if (version.Length > LobbyFieldLimit)
                throw new OfficialCdnException("生成的游戏版本信息超出 Lobby 字段长度");

            await WriteCacheAsync(new CacheEntry(snapshot.Fingerprint, version), cancellationToken);
            return new GameVersion(snapshot.Revision, version, FromCache: false);
        }
        catch (System.Text.DecoderFallbackException ex)
        {
            throw new OfficialCdnException("官方资料片版本文件编码无效", ex);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static List<(string Path, int Size)> GetVersionFiles(CdnSnapshot snapshot)
    {
        var highestExpansion = 0;
        foreach (var path in snapshot.Files.Keys)
        {
            var match = ExpansionPath().Match(path);
            if (!match.Success)
                continue;
            var first = match.Groups[1];
            var digits = first.Success ? first.ValueSpan : match.Groups[2].ValueSpan;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index > MaximumExpansion || index < 1)
                throw new OfficialCdnException("发现客户端尚未支持的资料片版本");
            highestExpansion = Math.Max(highestExpansion, index);
        }

        var result = new List<(string, int)>(highestExpansion);
        for (var index = 1; index <= highestExpansion; index++)
        {
            var name = FormattableString.Invariant($"ex{index}");
            var nested = $"game/sqpack/{name}/{name}.ver";
            var flat = $"game/{name}.ver";
            var path = snapshot.Files.ContainsKey(nested) ? nested : flat;
            if (!snapshot.Files.TryGetValue(path, out var file) || file.Size is <= 0 or > MaximumVersionFileSize)
                throw new OfficialCdnException($"官方修复清单缺少有效的 {name}.ver");
            result.Add((path, (int)file.Size));
        }
        return result;
    }

    private async Task<CacheEntry?> ReadCacheAsync(
        CdnSnapshot snapshot, long executableSize, int expansionCount, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(cacheFile);
            if (stream.Length > MaximumCacheSize)
                return null;
            var entry = await JsonSerializer.DeserializeAsync<CacheEntry>(stream, cancellationToken: cancellationToken);
            if (entry?.Fingerprint != snapshot.Fingerprint || entry.VersionInfo is null
                || entry.VersionInfo.Length > LobbyFieldLimit || !CachedVersion().IsMatch(entry.VersionInfo))
                return null;
            var fields = entry.VersionInfo.Split('+');
            if (fields.Length != expansionCount + 1
                || !fields[0].StartsWith(FormattableString.Invariant($"{Executable}/{executableSize}/"), StringComparison.Ordinal))
                return null;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(CacheEntry entry, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var temporary = $"{cacheFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, cacheFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record CacheEntry(string Fingerprint, string VersionInfo);

    [GeneratedRegex(@"\Agame/(?:sqpack/ex([0-9]+)/|ex([0-9]+)\.ver\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExpansionPath();

    [GeneratedRegex(@"\A[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{4}\.[0-9]{4}\z")]
    private static partial Regex ExpansionVersion();

    [GeneratedRegex(@"\Affxiv_dx11\.exe/[0-9]+/[0-9a-f]{40}(?:\+[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{4}){0,5}\z")]
    private static partial Regex CachedVersion();
}
