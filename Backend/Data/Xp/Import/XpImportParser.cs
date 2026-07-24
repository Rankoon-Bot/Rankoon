using System.Globalization;
using System.Text.Json;

namespace Rankoon.Data.Xp.Import;

public sealed class XpImportParser
{
    private const ulong MaximumSafeJsonInteger = 9_007_199_254_740_991;
    private static readonly string[] PointFields =
    [
        "ExtraPoints", "VcPoints", "VcPointCent", "MessagePoints", "ReactionPoints",
        "EventInterestPoints", "StagePoints", "Mee6Points", "NegativePoints"
    ];
    private static readonly string[] CustomRankingFields = [.. PointFields, "TotalWrittenMessages", "TotalVoiceChatSeconds"];

    public ParsedXpImport Parse(JsonElement payload, ulong guildId)
    {
        if (IsMee6(payload)) return ParseMee6(payload, guildId);
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("guild", out _))
            throw new XpImportParseException("xp.import.invalidPlayers");
        if (IsCustomRankoon(payload)) return ParseCustomRankoon(payload, guildId);
        throw new XpImportParseException("xp.import.unsupportedFormat");
    }

    private static bool IsMee6(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("guild", out var guild) && guild.ValueKind == JsonValueKind.Object &&
        guild.TryGetProperty("id", out _) &&
        payload.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array;

    private static bool IsCustomRankoon(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array || payload.GetArrayLength() == 0) return false;
        foreach (var entry in payload.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty("GuildId", out _) &&
                entry.TryGetProperty("DiscordUserId", out _) &&
                CustomRankingFields.Any(field => entry.TryGetProperty(field, out _))) return true;
        }
        return false;
    }

    private static ParsedXpImport ParseMee6(JsonElement payload, ulong guildId)
    {
        var importGuild = payload.GetProperty("guild").GetProperty("id");
        if (!TryReadSnowflake(importGuild, out var importedGuildId) || importedGuildId != guildId)
            throw new XpImportParseException("xp.import.guildMismatch");

        var skippedInvalid = 0;
        var duplicates = 0;
        var members = new Dictionary<ulong, XpImportMember>();
        foreach (var player in payload.GetProperty("players").EnumerateArray())
        {
            if (!TryParseMee6Member(player, out var member))
            {
                skippedInvalid++;
                continue;
            }
            if (members.ContainsKey(member.UserId)) duplicates++;
            members[member.UserId] = member;
        }
        if (members.Count == 0) throw new XpImportParseException("xp.import.noValidMembers");
        return new(XpImportFormat.Mee6, members.Values.ToArray(), skippedInvalid, 0, duplicates);
    }

    private static bool TryParseMee6Member(JsonElement player, out XpImportMember member)
    {
        member = default!;
        if (player.ValueKind != JsonValueKind.Object ||
            !player.TryGetProperty("id", out var id) || !TryReadSnowflake(id, out var userId)) return false;
        if (!TryReadOptionalNonNegativeDecimal(player, "xp", out var xp) ||
            !TryReadOptionalNonNegativeInt64(player, "message_count", out var messages)) return false;
        var name = userId.ToString(CultureInfo.InvariantCulture);
        if (player.TryGetProperty("username", out var username))
        {
            if (username.ValueKind != JsonValueKind.String) return false;
            name = username.GetString() ?? name;
        }
        member = new(userId, name, xp, messages, null);
        return true;
    }

    private static ParsedXpImport ParseCustomRankoon(JsonElement payload, ulong guildId)
    {
        var skippedInvalid = 0;
        var skippedForeignGuild = 0;
        var duplicates = 0;
        var matchingGuildSeen = false;
        var members = new Dictionary<ulong, XpImportMember>();
        foreach (var entry in payload.EnumerateArray())
        {
            if (!TryReadEntryGuild(entry, out var entryGuildId))
            {
                skippedInvalid++;
                continue;
            }
            if (entryGuildId != guildId)
            {
                skippedForeignGuild++;
                continue;
            }
            matchingGuildSeen = true;
            if (!TryParseCustomMember(entry, out var member))
            {
                skippedInvalid++;
                continue;
            }
            if (members.ContainsKey(member.UserId)) duplicates++;
            members[member.UserId] = member;
        }
        if (members.Count == 0)
            throw new XpImportParseException(!matchingGuildSeen && skippedForeignGuild > 0 ? "xp.import.guildMismatch" : "xp.import.noValidMembers");
        return new(XpImportFormat.CustomRankoon, members.Values.ToArray(), skippedInvalid, skippedForeignGuild, duplicates);
    }

    private static bool TryReadEntryGuild(JsonElement entry, out ulong guildId)
    {
        guildId = 0;
        return entry.ValueKind == JsonValueKind.Object &&
            entry.TryGetProperty("GuildId", out var value) && TryReadSnowflake(value, out guildId);
    }

    private static bool TryParseCustomMember(JsonElement entry, out XpImportMember member)
    {
        member = default!;
        if (!entry.TryGetProperty("DiscordUserId", out var id) || !TryReadSnowflake(id, out var userId)) return false;
        var name = userId.ToString(CultureInfo.InvariantCulture);
        if (entry.TryGetProperty("UserName", out var username))
        {
            if (username.ValueKind != JsonValueKind.String) return false;
            name = username.GetString() ?? name;
        }
        if (!TryReadOptionalNonNegativeInt64(entry, "TotalWrittenMessages", out var messages) ||
            !TryReadOptionalNonNegativeDecimal(entry, "TotalVoiceChatSeconds", out var voiceSeconds)) return false;

        var points = new decimal[PointFields.Length];
        for (var index = 0; index < PointFields.Length; index++)
        {
            if (!TryReadOptionalNonNegativeDecimal(entry, PointFields[index], out points[index])) return false;
        }
        try
        {
            var importedXp = checked(points[0] + points[1] + points[2] / 100m + points[3] + points[4] + points[5] + points[6] + points[7] - points[8]);
            member = new(userId, name, importedXp, messages, voiceSeconds);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static bool TryReadSnowflake(JsonElement value, out ulong result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.String)
            return ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result) && result != 0;
        if (TryReadExtendedNumber(value, "$numberLong", out var text))
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result != 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        var raw = value.GetRawText();
        return raw.All(char.IsAsciiDigit) && ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out result) &&
            result is > 0 and <= MaximumSafeJsonInteger;
    }

    private static bool TryReadOptionalNonNegativeInt64(JsonElement entry, string property, out long result)
    {
        result = 0;
        if (!entry.TryGetProperty(property, out var value)) return true;
        if (!TryReadDecimal(value, out var number) || number < 0 || decimal.Truncate(number) != number || number > long.MaxValue) return false;
        result = decimal.ToInt64(number);
        return true;
    }

    private static bool TryReadOptionalNonNegativeDecimal(JsonElement entry, string property, out decimal result)
    {
        result = 0;
        return !entry.TryGetProperty(property, out var value) || TryReadDecimal(value, out result) && result >= 0;
    }

    internal static bool TryReadDecimal(JsonElement value, out decimal result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDecimal(out result);
        foreach (var key in new[] { "$numberLong", "$numberInt", "$numberDecimal", "$numberDouble" })
        {
            if (!TryReadExtendedNumber(value, key, out var text)) continue;
            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }
        return false;
    }

    private static bool TryReadExtendedNumber(JsonElement value, string property, out string? text)
    {
        text = null;
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length == 0 ||
            !value.TryGetProperty(property, out var number) || number.ValueKind != JsonValueKind.String) return false;
        text = number.GetString();
        return text != null;
    }
}
