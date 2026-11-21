using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Lodestone.Core.Auth;

namespace Lodestone.App;

internal sealed class SavedGameLoginStore
{
    private const string StorageKey = "sdo-saved-game-login-v1";
    private readonly SemaphoreSlim access = new(1);

    internal async Task<(int AreaId, SavedGameLogin Login)?> ReadAsync(Guid installation)
    {
        await access.WaitAsync();
        try
        {
            var json = await SecureStorage.Default.GetAsync(StorageKey);
            if (json is null)
                return null;
            var entry = JsonSerializer.Deserialize<StoredLogin>(json);
            if (entry?.Installation == installation && entry.AreaId > 0
                && IsValid(entry.AccountId) && IsValid(entry.SessionKey))
                return (entry.AreaId, new SavedGameLogin(entry.AccountId, entry.SessionKey));
        }
        catch (Exception ex) when (IsStorageError(ex)) { }
        finally { access.Release(); }
        return null;
    }

    internal async Task<bool> SaveAsync(Guid installation, int areaId, SavedGameLogin login, CancellationToken token)
    {
        await access.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await SecureStorage.Default.SetAsync(StorageKey,
                JsonSerializer.Serialize(new StoredLogin(installation, areaId, login.AccountId, login.SessionKey)));
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex)) { return false; }
        finally { access.Release(); }
    }

    internal async Task<bool> ForgetAsync()
    {
        await access.WaitAsync();
        try
        {
            SecureStorage.Default.Remove(StorageKey);
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex)) { return false; }
        finally { access.Release(); }
    }

    private static bool IsValid(string? text) => text is { Length: > 0 and <= 2048 }
        && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or COMException or CryptographicException or JsonException;

    private sealed record StoredLogin(Guid Installation, int AreaId, string AccountId, string SessionKey);
}
