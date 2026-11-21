using System.Net.Sockets;
using Lodestone.Core.Auth;
using static Lodestone.Core.Lobby.LobbyBinary;

namespace Lodestone.Core.Lobby;

public sealed record LobbyRoster(IReadOnlyList<ServiceAccount> Accounts, int? SelectedIndex, IReadOnlyList<LobbyCharacter> Characters);

public sealed class GameLobbyClient
{
    public Task<LobbyRoster> ReadAsync(GameArea area, GameAuthorization authorization, string versionInfo, ushort lobbyVersion,
        int? accountIndex, CancellationToken cancellationToken = default) =>
        ReadAsync(area.Host, 54994, authorization.Sid, versionInfo, lobbyVersion, accountIndex, cancellationToken);

    internal async Task<LobbyRoster> ReadAsync(string host, int port, string sid, string versionInfo,
        ushort lobbyVersion, int? accountIndex, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = timeout.Token;
        try
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await client.ConnectAsync(host, port, token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await ReadAsync(client.GetStream(), sid, versionInfo, lobbyVersion, accountIndex, TimeProvider.System, token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LobbyException("连接或读取 Lobby 超时");
        }
        catch (Exception ex) when (ex is SocketException or IOException and not LobbyException)
        {
            throw new LobbyException("Lobby 连接失败或提前关闭");
        }
    }

    internal static async Task<LobbyRoster> ReadAsync(Stream stream, string sid, string versionInfo,
        ushort lobbyVersion, int? accountIndex, TimeProvider clock, CancellationToken token)
    {
        var connection = new LobbyConnection(stream, clock);
        await connection.AuthenticateAsync(sid, versionInfo, lobbyVersion, token);
        var accounts = LobbyRecords.Accounts(await connection.ReceiveIpcAsync(12, token));
        var publicAccounts = accounts.Select(account => new ServiceAccount(account.Index, account.Name)).ToArray();
        if (accountIndex is null && accounts.Count != 1)
            return new LobbyRoster(publicAccounts, null, []);
        var account = accountIndex is null ? accounts[0] : accounts.FirstOrDefault(entry => entry.Index == accountIndex);
        if (account is null)
            throw new LobbyException("所选服务账号不可用");

        var selection = new byte[88];
        Put(selection, 0, 2UL);
        selection[8] = (byte)account.Index;
        selection[9] = 1;
        Put(selection, 10, lobbyVersion);
        Put(selection, 16, account.Id);
        await connection.SendIpcAsync(3, selection, token);

        var characters = new Dictionary<string, LobbyCharacter>();
        for (var pageIndex = 0; pageIndex < 16; pageIndex++)
        {
            var (last, page) = LobbyRecords.Characters(await connection.ReceiveIpcAsync(13, token));
            foreach (var character in page)
                characters[character.ContentId] = character;
            if (last)
                return new LobbyRoster(publicAccounts, account.Index, characters.Values.OrderBy(character => character.Slot)
                    .Select(character => character with
                    {
                        HomeWorld = Resolve(character.HomeWorld, connection.Worlds),
                        CurrentWorld = Resolve(character.CurrentWorld, connection.Worlds),
                    }).ToArray());
        }
        throw new LobbyException("Lobby 角色页超过支持上限");
    }

    private static CharacterWorld Resolve(CharacterWorld world, IReadOnlyDictionary<ushort, string> names) =>
        names.TryGetValue(world.Id, out var name) ? world with { Name = name } : world;
}
