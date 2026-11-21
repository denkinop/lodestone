using System.Globalization;
using System.Text.Json;
using static Lodestone.Core.Lobby.LobbyBinary;

namespace Lodestone.Core.Lobby;

public sealed record ServiceAccount(int Index, string Name);
internal sealed record ServiceAccountRecord(int Index, ulong Id, string Name);
public sealed record CharacterWorld(ushort Id, string Name);
public sealed record CharacterEquipment(
    string Slot, ushort Model, ushort ModelType, ushort Variant, byte Stain1, byte Stain2);
public sealed record CharacterAppearance(
    IReadOnlyList<int> Customize, int DisplayFlags, int Voice, IReadOnlyList<int> Glasses)
{
    public int Race => Customize[0];
    public int Gender => Customize[1];
    public int Tribe => Customize[4];
    public bool Highlights => Customize[7] == 1 || (Customize[7] & 128) != 0;
    public int EyeShape => Customize[16] & 127;
    public bool SmallIris => (Customize[16] & 128) != 0;
    public int MouthShape => Customize[19] & 127;
    public bool Lipstick => (Customize[19] & 128) != 0;
    public int FacePaint => Customize[24] & 127;
    public bool FacePaintReversed => (Customize[24] & 128) != 0;
}

public sealed record LobbyCharacter(
    string ContentId, string PlayerId, byte Slot, byte Flags, string Name,
    CharacterWorld HomeWorld, CharacterWorld CurrentWorld, CharacterAppearance? Appearance,
    IReadOnlyList<CharacterEquipment> Equipment, string? DetailError);

internal static class LobbyRecords
{
    private const int CharacterRecord = 1184;
    private const int CharacterHeader = 80;
    private static readonly string[] Slots =
        ["head", "body", "hands", "legs", "feet", "ears", "neck", "wrists", "left_ring", "right_ring"];

    internal static IReadOnlyList<ServiceAccountRecord> Accounts(ReadOnlySpan<byte> body)
    {
        if (body.Length < 16)
            throw new LobbyException("服务账号列表头不完整");
        var count = body[9];
        var size = body.Length == 1168 ? 144 : 80;
        if (count > 8 || body.Length < 16 + count * size)
            throw new LobbyException("服务账号列表长度无效");
        var accounts = new List<ServiceAccountRecord>(count);
        for (var position = 0; position < count; position++)
        {
            var entry = body[(16 + position * size)..];
            var index = U32(entry, 8);
            if (index > 7 || accounts.Any(account => account.Index == index))
                throw new LobbyException("服务账号索引无效");
            accounts.Add(new ServiceAccountRecord((int)index, U64(entry, 0), Text(entry[12..80])));
        }
        return accounts;
    }

    internal static Dictionary<ushort, string> WorldNames(ReadOnlySpan<byte> body)
    {
        if (body.Length < 24)
            throw new LobbyException("服务器列表头不完整");
        var count = U32(body, 12);
        if (count > 6 || body.Length < 24 + count * 84)
            throw new LobbyException("服务器列表长度无效");
        var worlds = new Dictionary<ushort, string>();
        for (var index = 0; index < count; index++)
        {
            var world = body.Slice(24 + index * 84, 84);
            var id = U16(world, 0);
            var name = Text(world[20..]);
            if (id != 0 && name.Length > 0)
                worlds[id] = name;
        }
        return worlds;
    }

    internal static (bool Last, IReadOnlyList<LobbyCharacter> Characters) Characters(ReadOnlySpan<byte> body)
    {
        if (body.Length < CharacterHeader)
            throw new LobbyException("角色列表头不完整");
        if (U64(body, 0) is not (0 or 2))
            throw new LobbyException("角色列表回包序号不匹配");
        var count = body[9];
        if (count > 2 || body.Length < CharacterHeader + count * CharacterRecord)
            throw new LobbyException("角色列表长度无效");
        var characters = new List<LobbyCharacter>(count);
        for (var index = 0; index < count; index++)
        {
            var row = body.Slice(CharacterHeader + index * CharacterRecord, CharacterRecord);
            var contentId = U64(row, 8);
            if (contentId == 0)
                continue;
            var character = new LobbyCharacter(contentId.ToString(CultureInfo.InvariantCulture),
                U64(row, 0).ToString(CultureInfo.InvariantCulture), row[16], row[17], Text(row[44..76]),
                new CharacterWorld(U16(row, 24), Text(row[76..108])),
                new CharacterWorld(U16(row, 26), Text(row[108..140])),
                null, [], "Lobby 未返回角色外观数据");
            var details = UntilNull(row.Slice(140, 1024));
            characters.Add(details.IsEmpty ? character : ParseDetails(character, details));
        }
        return ((body[8] & 1) != 0, characters);
    }

    private static LobbyCharacter ParseDetails(LobbyCharacter character, ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var data = document.RootElement;
            if (data.GetProperty("classname").GetString() != "ClientSelectData")
                throw new FormatException();
            var content = data.GetProperty("content");
            var customize = content[13].EnumerateArray().Select(Number).ToArray();
            if (customize.Length < 26)
                throw new FormatException();
            var appearance = new CharacterAppearance(customize, Number(content[21]), Number(content[22]),
                content[18].EnumerateArray().Select(Number).ToArray());
            if (content[16].GetArrayLength() != Slots.Length || content[17].GetArrayLength() != Slots.Length)
                throw new FormatException();
            var gear = new List<CharacterEquipment>(12);
            for (var index = 0; index < Slots.Length; index++)
            {
                var packed = checked((uint)WeaponValue(content[16][index]));
                gear.Add(new CharacterEquipment(Slots[index], (ushort)packed, 0, (byte)(packed >> 16),
                    (byte)(packed >> 24), checked((byte)Number(content[17][index]))));
            }
            gear.Add(Weapon("main_hand", WeaponValue(content[14])));
            gear.Add(Weapon("off_hand", WeaponValue(content[15])));

            var name = content[0].ValueKind == JsonValueKind.String ? content[0].GetString() : null;
            var worldName = content[24].ValueKind == JsonValueKind.String ? content[24].GetString() : null;
            return character with
            {
                Name = string.IsNullOrEmpty(name) ? character.Name : name,
                CurrentWorld = character.CurrentWorld.Name.Length == 0 && worldName is not null
                    ? character.CurrentWorld with { Name = worldName } : character.CurrentWorld,
                Appearance = appearance,
                Equipment = gear,
                DetailError = null
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException
            or KeyNotFoundException or IndexOutOfRangeException or OverflowException)
        {
            return character with { DetailError = "无法解析 Lobby 角色外观数据" };
        }
    }

    private static int Number(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? int.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetInt32();

    private static ulong WeaponValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            return text.StartsWith('-')
                ? unchecked((uint)int.Parse(text, CultureInfo.InvariantCulture))
                : ulong.Parse(text, CultureInfo.InvariantCulture);
        }
        return value.TryGetInt32(out var signed) && signed < 0 ? unchecked((uint)signed) : value.GetUInt64();
    }

    private static CharacterEquipment Weapon(string slot, ulong raw) =>
        new(slot, (ushort)raw, (ushort)(raw >> 16), (ushort)(raw >> 32), (byte)(raw >> 48), (byte)(raw >> 56));
}
