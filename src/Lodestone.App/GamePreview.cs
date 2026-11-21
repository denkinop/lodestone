using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lodestone.Core.Assets;
using Lodestone.Core.Cdn;

namespace Lodestone.App;

internal sealed class GamePreview(OfficialCdnClient cdn, Func<string, PreviewCharacter?> findCharacter) : IDisposable
{
    private const string Root = "/_game_preview/";
    private readonly object gate = new();
    private readonly SemaphoreSlim buildLock = new(1);
    private Operation? active;
    private Prepared? prepared;
    private CdnSnapshot? snapshot;
    private CharacterScene? builder;
    private long checkedAt;
    private bool disposed;

    internal async Task<GamePreviewResult> PrepareAsync(string contentId, string requestId)
    {
        if (!ulong.TryParse(contentId, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !Guid.TryParseExact(requestId, "D", out var id)
            || findCharacter(contentId) is not { Character.Appearance: not null } character)
            return new(Error: "该角色的外观不可用，请重新读取角色列表");

        Operation operation;
        lock (gate)
        {
            if (disposed)
                return new(Error: "窗口已关闭");
            InvalidateLocked();
            operation = active = new Operation(id);
        }

        try
        {
            var result = await Task.Run(async () =>
            {
                var token = operation.Cancellation.Token;
                await buildLock.WaitAsync(token);
                try
                {
                    var scene = PreviewCache.ReadScene(character);
                    token.ThrowIfCancellationRequested();
                    if (scene is null)
                    {
                        var source = await GetSourceAsync(token);
                        scene = await source.BuildAsync(character.Character, snapshot!.Revision, token);
                        token.ThrowIfCancellationRequested();
                        try { PreviewCache.SaveScene(character, scene); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    var loaded = scene.Scene.Gear.Where(entry => entry.Loaded).Select(entry => entry.Slot)
                        .ToHashSet(StringComparer.Ordinal);
                    var gear = CharacterScene.DescribeGear(character.Character.Equipment, loaded);
                    return new Prepared(id, character, JsonSerializer.SerializeToUtf8Bytes(scene.Scene with { Gear = gear }, PreviewCache.Json),
                        scene.Textures, gear.SelectMany(entry => entry.Candidates).Select(candidate => candidate.Icon)
                            .Where(icon => icon > 0).ToHashSet());
                }
                finally { buildLock.Release(); }
            });
            lock (gate)
            {
                if (!ReferenceEquals(active, operation) || findCharacter(contentId) != character)
                    return new(Error: "角色已切换，请重新加载预览");
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                prepared = result with { Cancellation = new CancellationTokenSource() };
            }
            var route = Root + id.ToString("D") + "/";
            return new(route + "scene.json", route + "textures/", route + "icons/", Full: HasPortrait(character, "full"),
                Portrait: HasPortrait(character, "portrait"));
        }
        catch (OperationCanceledException)
        {
            return new(Error: operation.Released ? "已取消预览" : "角色资源下载超时，请重试");
        }
        catch (Exception ex) when (ex is OfficialCdnException or SqPackException)
        {
            return new(Error: ex.Message);
        }
        catch (Exception ex) when (ex is IOException or OverflowException or ArgumentException or InvalidOperationException)
        {
            return new(Error: "无法完成角色资源解析，请稍后重试");
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(active, operation))
                    active = null;
                operation.Cancellation.Dispose();
            }
        }
    }

    private async Task<CharacterScene> GetSourceAsync(CancellationToken token)
    {
        var timestamp = Interlocked.Read(ref checkedAt);
        if (snapshot is null || timestamp == 0 || Environment.TickCount64 - timestamp > 300_000)
        {
            var current = await cdn.GetSnapshotAsync(token);
            if (snapshot?.Fingerprint != current.Fingerprint)
                builder = new CharacterScene(new SqPackArchive(cdn, current));
            snapshot = current;
            Interlocked.Exchange(ref checkedAt, Environment.TickCount64);
        }
        return builder!;
    }

    internal void RefreshSource() => Interlocked.Exchange(ref checkedAt, 0);

    private static bool HasPortrait(PreviewCharacter character, string mode) => PreviewCache.ReadPng(character, mode + ".png") is not null;

    internal bool SavePortrait(string requestId, string mode, string encoded)
    {
        const string header = "data:image/png;base64,";
        if (!Guid.TryParseExact(requestId, "D", out var id) || mode is not ("full" or "portrait")
            || encoded.Length > 5_600_000 || !encoded.StartsWith(header, StringComparison.Ordinal))
            return false;
        lock (gate)
        {
            if (prepared is not { } state || state.Id != id || findCharacter(state.Character.Character.ContentId) != state.Character)
                return false;
            try
            {
                var png = Convert.FromBase64String(encoded[header.Length..]);
                if (!PreviewCache.IsPng(png))
                    return false;
                PreviewCache.SavePng(state.Character, mode + ".png", png);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return false; }
        }
    }

    internal bool Release(string requestId)
    {
        if (!Guid.TryParseExact(requestId, "D", out var id))
            return false;
        lock (gate)
        {
            if (active?.Id != id && prepared?.Id != id)
                return false;
            InvalidateLocked();
            return true;
        }
    }

    internal void Invalidate()
    {
        lock (gate)
            InvalidateLocked();
    }

    private void InvalidateLocked()
    {
        if (active is { } operation)
        {
            operation.Released = true;
            operation.Cancellation.Cancel();
            active = null;
        }
        if (prepared is { } previous)
        {
            previous.Cancellation?.Cancel();
            previous.Cancellation?.Dispose();
            prepared = null;
        }
    }

    internal void Respond(object? sender, WebViewWebResourceRequestedEventArgs request)
    {
        var uri = request.Uri;
        if (!LodestoneApplication.IsLocalWebViewUri(uri) || !uri.AbsolutePath.StartsWith(Root, StringComparison.Ordinal))
            return;
        request.Handled = true;
        var parts = uri.AbsolutePath[Root.Length..].Split('/');
        if (parts is ["portrait", var session, var characterId, var revision, "full.png" or "portrait.png"]
            && Guid.TryParseExact(session, "D", out var sessionId) && Guid.TryParseExact(revision, "D", out var revisionId)
            && findCharacter(characterId) is { } portrait && portrait.Session == sessionId && portrait.Revision == revisionId
            && PreviewCache.ReadPng(portrait, parts[4]) is { } png)
        {
            Reply(request, "image/png", png);
            return;
        }
        lock (gate)
        {
            if (prepared is { } state && Guid.TryParseExact(parts[0], "D", out var id) && state.Id == id
                && findCharacter(state.Character.Character.ContentId) == state.Character)
            {
                if (parts is [_, "scene.json"])
                {
                    Reply(request, "application/json", state.Json);
                    return;
                }
                if (parts is [_, "textures", var file] && file.EndsWith(".png", StringComparison.Ordinal)
                    && state.Textures.TryGetValue(file[..^4], out var texture))
                {
                    Reply(request, "image/png", texture);
                    return;
                }
                if (parts is [_, "icons", var icon] && icon.EndsWith(".png", StringComparison.Ordinal)
                    && int.TryParse(icon.AsSpan(0, icon.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var iconId)
                    && state.Icons.Contains(iconId))
                {
                    var token = state.Cancellation!.Token;
                    request.SetResponse(200, "OK", "image/png", Task.Run(() => ReadIconAsync(state, iconId, token)));
                    return;
                }
            }
            request.SetResponse(404, "Not Found", "application/json", Task.FromResult<Stream?>(null));
        }
    }

    private async Task<Stream?> ReadIconAsync(Prepared state, int icon, CancellationToken token)
    {
        try
        {
            var name = $"icon-v2-{icon}.png";
            if (PreviewCache.ReadPng(state.Character, name) is { } cached)
                return new MemoryStream(cached, writable: false);
            await buildLock.WaitAsync(token);
            try
            {
                var source = await GetSourceAsync(token);
                var bytes = await source.ReadIconAsync(icon, token);
                if (bytes is null)
                    return null;
                token.ThrowIfCancellationRequested();
                try { PreviewCache.SavePng(state.Character, name, bytes); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return new MemoryStream(bytes, writable: false);
            }
            finally { buildLock.Release(); }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or OverflowException or ArgumentException
            or ObjectDisposedException)
        {
            return null;
        }
    }

    private static void Reply(WebViewWebResourceRequestedEventArgs request, string mime, byte[] bytes) =>
        request.SetResponse(200, "OK", mime, Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false)));

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            InvalidateLocked();
        }
    }

    private sealed record Operation(Guid Id)
    {
        internal CancellationTokenSource Cancellation { get; } = new(TimeSpan.FromMinutes(3));
        internal volatile bool Released;
    }

    private sealed record Prepared(Guid Id, PreviewCharacter Character, byte[] Json,
        IReadOnlyDictionary<string, byte[]> Textures, HashSet<int> Icons)
    {
        internal CancellationTokenSource? Cancellation { get; init; }
    }
}

internal sealed record GamePreviewResult(
    [property: JsonPropertyName("scene")] string? Scene = null,
    [property: JsonPropertyName("textureRoot")] string? TextureRoot = null,
    [property: JsonPropertyName("iconRoot")] string? IconRoot = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("full")] bool Full = false,
    [property: JsonPropertyName("portrait")] bool Portrait = false);
