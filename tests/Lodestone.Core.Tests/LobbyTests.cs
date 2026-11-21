using System.Globalization;
using System.Text;
using System.Text.Json;
using Lodestone.Core.Lobby;
using Xunit;
using static Lodestone.Core.Lobby.LobbyBinary;

namespace Lodestone.Core.Tests;

public sealed class LobbyTests
{
    private const string Version = "ffxiv_dx11.exe/4/a94a8fe5ccb19ba61c4c0873d391e987982fbbd3";
    private const uint Actor = 0xe0037604;
    private static readonly TimeProvider Clock = new FixedTime();

    [Fact]
    public void BlowfishMatchesReferenceIncludingUnencryptedTail()
    {
        var plaintext = Enumerable.Range(0, 56).Select(i => (byte)i).ToArray();
        var encrypted = (byte[])plaintext.Clone();
        var cipher = new LobbyCipher("foobar"u8, 0, 7500);
        cipher.Encrypt(encrypted);
        Assert.Equal("34a19702b9499c3319870c835547f532e89d7fd3f73d2057df09bc70e3997484",
            Convert.ToHexStringLower(encrypted.AsSpan(0, 32)));
        Assert.Equal(plaintext[32..], encrypted[32..]);
        cipher.Decrypt(encrypted);
        Assert.Equal(plaintext, encrypted);
    }

    [Fact]
    public async Task ReadsSplitAndCoalescedBundlesAndMultipleCharacterPages()
    {
        var cipher = NewCipher();
        var accounts = AccountData(41, 42);
        var world = new byte[108];
        Put(world, 12, 1u);
        Put(world, 24, (ushort)45);
        Encoding.UTF8.GetBytes("示例世界").CopyTo(world, 44);
        var input = new byte[][]
        {
            Bundle((7, new byte[8])),
            Setup(cipher),
            Bundle(Ipc(cipher, 21, world), Ipc(cipher, 12, accounts)),
            Bundle((7, new byte[8]), Ipc(cipher, 13, Page(false, Character(9007199254740993, 1), Character(0, 2)))),
            Bundle(Ipc(cipher, 13, Page(true, Character(ulong.MaxValue, 3)))),
        }.SelectMany(value => value).ToArray();
        using var wire = new FragmentedStream(input);

        var result = await GameLobbyClient.ReadAsync(wire, "fixture-ticket", Version, 7500, 1, Clock, CancellationToken.None);

        Assert.Equal(1, result.SelectedIndex);
        Assert.Equal(2, result.Accounts.Count);
        Assert.Equal(["9007199254740993", "18446744073709551615"], result.Characters.Select(value => value.ContentId));
        var character = result.Characters[0];
        Assert.Equal("示例世界", character.HomeWorld.Name);
        Assert.Equal("名字来自外观", character.Name);
        Assert.True(character.Appearance!.Highlights);
        Assert.True(character.Appearance.SmallIris);
        Assert.True(character.Appearance.Lipstick);
        Assert.True(character.Appearance.FacePaintReversed);
        Assert.Equal(5, character.Appearance.EyeShape);
        Assert.Equal(28, character.ClassJobId);
        Assert.Equal(91, character.Level);
        Assert.Equal(new CharacterBirthday(12, 32), character.Birthday);
        Assert.Equal(8, character.GuardianDeityId);
        Assert.Equal(new CharacterEquipment("off_hand", 100, 200, 3, 4, 5),
            Assert.Single(character.Equipment, value => value.Slot == "off_hand"));
        Assert.Equal(new CharacterEquipment("head", 10, 0, 3, 4, 5),
            Assert.Single(character.Equipment, value => value.Slot == "head"));
        VerifyOutput(wire.Output.ToArray(), cipher);
    }

    [Fact]
    public async Task ReturnsAccountsWithoutGuessingWhenMultipleAreAvailable()
    {
        var cipher = NewCipher();
        using var wire = new FragmentedStream([..Bundle((7, new byte[8])), ..Setup(cipher),
            ..Bundle(Ipc(cipher, 12, AccountData(41, 42)))]);
        var result = await GameLobbyClient.ReadAsync(wire, "fixture-ticket", Version, 7500, null, Clock, CancellationToken.None);
        Assert.Equal(2, result.Accounts.Count);
        Assert.Null(result.SelectedIndex);
        Assert.Empty(result.Characters);
    }

    [Fact]
    public async Task EmptyAccountListHasNoSelectedAccount()
    {
        var cipher = NewCipher();
        using var wire = new FragmentedStream([..Bundle((7, new byte[8])), ..Setup(cipher),
            ..Bundle(Ipc(cipher, 12, AccountData()))]);
        var result = await GameLobbyClient.ReadAsync(wire, "fixture-ticket", Version, 7500, null, Clock, CancellationToken.None);
        Assert.Empty(result.Accounts);
        Assert.Null(result.SelectedIndex);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(43)]
    public async Task CannotTreatATruncatedBundleAsValid(int length)
    {
        using var wire = new FragmentedStream(Bundle((7, new byte[8]))[..length]);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => GameLobbyClient.ReadAsync(wire, "fixture-ticket", Version, 7500, null, Clock, CancellationToken.None));
    }

    [Fact]
    public async Task LobbyErrorDoesNotExposeTicket()
    {
        var cipher = NewCipher();
        var error = new byte[18];
        Put(error, 8, 701u);
        using var wire = new FragmentedStream([..Bundle((7, new byte[8])), ..Setup(cipher), ..Bundle(Ipc(cipher, 2, error))]);
        var exception = await Assert.ThrowsAsync<LobbyException>(
            () => GameLobbyClient.ReadAsync(wire, "fixture-ticket", Version, 7500, null, Clock, CancellationToken.None));
        Assert.Contains("701", exception.Message);
        Assert.DoesNotContain("fixture-ticket", exception.ToString());
    }

    [Fact]
    public void AreaDirectoryAcceptsOnlyOfficialLobbyHosts()
    {
        var areas = GameAreaDirectory.Parse("""
            var servers=
            [{"Areaid":"1","AreaStat":1,"AreaName":"示例","AreaLobby":"ffxivlobby01.ff14.sdo.com","AreaOrder":2},
             {"Areaid":"7","AreaStat":1,"AreaName":"伪造","AreaLobby":"ffxivlobby01.ff14.sdo.com.example.org","AreaOrder":1}];
            """);
        Assert.Equal(1, Assert.Single(areas).Id);
    }

    [Fact]
    public void AnUnavailableLevelDoesNotDiscardCharacterAppearance()
    {
        var (_, characters) = LobbyRecords.Characters(Page(true, Character(101, 0, "bad")));
        var character = Assert.Single(characters);
        Assert.Equal(28, character.ClassJobId);
        Assert.Null(character.Level);
        Assert.NotNull(character.Appearance);
        Assert.Null(character.DetailError);
    }

    [Theory]
    [InlineData(0, 1, 8, false, true)]
    [InlineData(12, 33, 8, false, true)]
    [InlineData(1, 1, 0, true, false)]
    public void MissingProfileFieldsDoNotDiscardAppearance(int month, int day, int deity, bool hasBirthday, bool hasDeity)
    {
        var (_, characters) = LobbyRecords.Characters(Page(true, Character(101, 0, month: month, day: day, deity: deity)));
        var character = Assert.Single(characters);
        Assert.Equal(hasBirthday, character.Birthday is not null);
        Assert.Equal(hasDeity, character.GuardianDeityId.HasValue);
        Assert.NotNull(character.Appearance);
        Assert.Null(character.DetailError);
    }

    private static void VerifyOutput(byte[] output, LobbyCipher cipher)
    {
        var setupLength = (int)U32(output, 24);
        Assert.Equal(616 + 56, setupLength);
        Assert.Equal(3, U16(output, 28));
        Assert.Equal("fdbc958225e28b8d71a86233546c090f", Text(output.AsSpan(56 + 36, 32)));
        var login = output.AsSpan(setupLength);
        Assert.Equal(Actor, U32(login, 44));
        var loginLength = (int)U32(login, 24);
        var payload = login[56..loginLength];
        cipher.Decrypt(payload);
        Assert.Equal(6, U16(payload, 2));
        Assert.Equal(7500, U16(payload, 16 + 10));
        Assert.Equal("fixture-ticket", Text(payload.Slice(16 + 16, 64)));
        Assert.Equal(Version, Text(payload.Slice(16 + 400, 144)));
        var selection = login.Slice(loginLength + 56, 104);
        cipher.Decrypt(selection);
        Assert.Equal(3, U16(selection, 2));
        Assert.Equal(42UL, U64(selection, 16 + 16));
        Assert.Equal(8, U16(login[(loginLength + 160)..], 52));
    }

    private static byte[] AccountData(params ulong[] ids)
    {
        var accounts = new byte[1168];
        accounts[9] = (byte)ids.Length;
        for (var index = 0; index < ids.Length; index++)
        {
            var offset = 16 + index * 144;
            Put(accounts, offset, ids[index]);
            Put(accounts, offset + 8, (uint)index);
        }
        return accounts;
    }

    private static byte[] Character(ulong id, byte slot, string level = "91", int month = 12, int day = 32, int deity = 8)
    {
        var row = new byte[1184];
        Put(row, 0, 22UL);
        Put(row, 8, id);
        row[16] = slot;
        Put(row, 24, (ushort)45);
        "Base"u8.CopyTo(row.AsSpan(44));
        var appearance = Enumerable.Range(0, 26).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
        appearance[7] = "128";
        appearance[16] = "133";
        appearance[19] = "130";
        appearance[24] = "134";
        var weapon = (100UL | 200UL << 16 | 3UL << 32 | 4UL << 48 | 5UL << 56).ToString(CultureInfo.InvariantCulture);
        var content = new object?[25];
        content[0] = "名字来自外观";
        content[1] = "28";
        var levels = new object[32];
        Array.Fill(levels, 0);
        levels[18] = level;
        levels[28] = 55;
        content[2] = levels;
        content[6] = month.ToString(CultureInfo.InvariantCulture);
        content[7] = day;
        content[8] = deity.ToString(CultureInfo.InvariantCulture);
        content[13] = appearance;
        content[14] = weapon;
        content[15] = weapon;
        content[16] = new[] { (10 | (3 << 16) | (4 << 24)).ToString(CultureInfo.InvariantCulture) }.Concat(Enumerable.Repeat("0", 9)).ToArray();
        content[17] = new[] { 5 }.Concat(Enumerable.Repeat(0, 9)).ToArray();
        content[18] = new[] { "10", "11" };
        content[21] = "52";
        content[22] = "93";
        JsonSerializer.SerializeToUtf8Bytes(new { classname = "ClientSelectData", content }).CopyTo(row, 140);
        return row;
    }

    private static byte[] Page(bool last, params byte[][] rows)
    {
        var header = new byte[80];
        Put(header, 0, 2UL);
        header[8] = last ? (byte)1 : (byte)0;
        header[9] = (byte)rows.Length;
        return [..header, ..rows.SelectMany(row => row)];
    }

    private static LobbyCipher NewCipher() => new("fdbc958225e28b8d71a86233546c090f"u8, 1700000000, 7500);

    private static byte[] Setup(LobbyCipher cipher)
    {
        var data = new byte[640];
        Put(data, 0, Actor);
        Put(data, 8, 0x40u);
        cipher.Encrypt(data);
        return Bundle((10, data));
    }

    private static (ushort, byte[]) Ipc(LobbyCipher cipher, ushort opcode, byte[] payload)
    {
        var data = new byte[16 + payload.Length];
        Put(data, 0, (ushort)20);
        Put(data, 2, opcode);
        payload.CopyTo(data, 16);
        cipher.Encrypt(data);
        return (3, data);
    }

    private static byte[] Bundle(params (ushort Kind, byte[] Data)[] segments)
    {
        var bundle = new byte[40 + segments.Sum(segment => segment.Data.Length + 16)];
        Put(bundle, 24, (uint)bundle.Length);
        Put(bundle, 30, (ushort)segments.Length);
        bundle[32] = 1;
        var offset = 40;
        foreach (var (kind, data) in segments)
        {
            Put(bundle, offset, (uint)(data.Length + 16));
            Put(bundle, offset + 12, kind);
            data.CopyTo(bundle, offset + 16);
            offset += data.Length + 16;
        }
        return bundle;
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000000);
    }

    private sealed class FragmentedStream(byte[] input) : MemoryStream(input)
    {
        internal MemoryStream Output { get; } = new();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 17)], cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Output.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing) Output.Dispose();
            base.Dispose(disposing);
        }
    }
}
