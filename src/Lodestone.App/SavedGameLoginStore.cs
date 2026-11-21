using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Lodestone.Core.Auth;
using Lodestone.Core.Community;

namespace Lodestone.App;

internal sealed class SavedGameLoginStore
{
    private const string StorageKey = "sdo-saved-game-login-v1";
    private const string CommunityKey = "rising-stones-saved-session-v1";
    private readonly SemaphoreSlim access = new(1);

    internal async Task<SavedProfileLogin?> ReadAsync(Guid installation)
    {
        await access.WaitAsync();
        try
        {
            var entry = await ReadStoredLoginAsync();
            if (entry?.Installation == installation && entry.AreaId > 0
                && IsValid(entry.AccountId) && IsValid(entry.SessionKey))
                return new(entry.AreaId, entry.AreaName, new SavedGameLogin(entry.AccountId, entry.SessionKey));
        }
        catch (Exception ex) when (IsStorageError(ex)) { }
        finally { access.Release(); }
        return null;
    }

    internal async Task<bool> SaveAsync(Guid installation, int areaId, string areaName, SavedGameLogin login, CancellationToken token)
    {
        await access.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await SecureStorage.Default.SetAsync(StorageKey,
                JsonSerializer.Serialize(new StoredLogin(installation, areaId, login.AccountId, login.SessionKey, areaName)));
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex)) { return false; }
        finally { access.Release(); }
    }

    internal async Task<SavedCommunityCookie[]> ReadCommunityAsync(Guid installation, string accountId)
    {
        await access.WaitAsync();
        try
        {
            var json = await SecureStorage.Default.GetAsync(CommunityKey);
            if (json is null || json.Length > 65536)
                return [];
            var entry = JsonSerializer.Deserialize<StoredCommunity>(json);
            return entry?.Installation == installation && entry.AccountId == accountId && entry.Cookies is { } cookies
                ? cookies.Where(cookie => cookie?.IsUsable(DateTimeOffset.UtcNow) == true).ToArray() : [];
        }
        catch (Exception ex) when (IsStorageError(ex)) { return []; }
        finally { access.Release(); }
    }

    internal async Task SaveCommunityAsync(Guid installation, int areaId, string accountId, SavedCommunityCookie[] cookies)
    {
        if (cookies.Length == 0)
            return;
        await access.WaitAsync();
        try
        {
            var login = await ReadStoredLoginAsync();
            if (login?.Installation != installation || login.AccountId != accountId || login.AreaId != areaId)
                return;
            var json = JsonSerializer.Serialize(new StoredCommunity(installation, accountId, cookies));
            if (json.Length <= 65536)
                await SecureStorage.Default.SetAsync(CommunityKey, json);
        }
        catch (Exception ex) when (IsStorageError(ex)) { }
        finally { access.Release(); }
    }

    internal async Task<bool> ForgetAsync()
    {
        await access.WaitAsync();
        try
        {
            SecureStorage.Default.Remove(StorageKey);
            SecureStorage.Default.Remove(CommunityKey);
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex)) { return false; }
        finally { access.Release(); }
    }

    private static async Task<StoredLogin?> ReadStoredLoginAsync() => await SecureStorage.Default.GetAsync(StorageKey) is { } text
        ? JsonSerializer.Deserialize<StoredLogin>(text) : null;

    private static bool IsValid(string? text) => text is { Length: > 0 and <= 2048 }
        && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or ArgumentException or COMException or CryptographicException or JsonException;

    private sealed record StoredLogin(Guid Installation, int AreaId, string AccountId, string SessionKey, string? AreaName);
    private sealed record StoredCommunity(Guid Installation, string AccountId, SavedCommunityCookie[] Cookies);
}

internal sealed record SavedProfileLogin(int AreaId, string? AreaName, SavedGameLogin Login);

internal sealed record SavedCommunityCookie(string Name, string Value, string Domain, string Path, bool HttpOnly,
    DateTimeOffset? Expires, int SameSite)
{
    private static readonly string ApiHost = new Uri(RisingStonesSession.StatusUrl).Host;

    internal bool IsUsable(DateTimeOffset now) => Name is { Length: > 0 and <= 128 }
        && !Name.Equals("CASTGC", StringComparison.OrdinalIgnoreCase)
        && !Name.Equals("CAS_LOGIN_STATE", StringComparison.OrdinalIgnoreCase) && !Name.Any(char.IsControl)
        && Value is { Length: > 0 and <= 4096 } && !Value.Any(char.IsControl)
        && (Domain == RisingStonesSession.Portal.Host || Domain == ApiHost)
        && Path is { Length: > 0 and <= 512 } && Path.StartsWith('/') && !Path.Any(char.IsControl)
        && (Expires is null || Expires > now);
}
