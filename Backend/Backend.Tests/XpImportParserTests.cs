using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.Xp.Import;
using Xunit;

namespace Backend.Tests;

public sealed class XpImportParserTests
{
    private const ulong GuildId = 123456789012345678;
    private const ulong UserId = 234567890123456789;
    private readonly XpImportParser parser = new();

    [Fact]
    public void Parses_existing_mee6_format_and_defaults_optional_values()
    {
        var parsed = Parse("""
            {"guild":{"id":"123456789012345678"},"players":[
              {"id":"234567890123456789","xp":1234,"message_count":56,"username":"Example member"},
              {"id":"345678901234567890"}
            ]}
            """);

        Assert.Equal(XpImportFormat.Mee6, parsed.Format);
        Assert.Equal(2, parsed.Members.Count);
        Assert.Equal(new XpImportMember(UserId, "Example member", 1234m, 56, null), parsed.Members[0]);
        Assert.Equal("345678901234567890", parsed.Members[1].DisplayName);
    }

    [Fact]
    public void Mee6_guild_mismatch_is_structured()
    {
        var exception = Assert.Throws<XpImportParseException>(() => Parse("""{"guild":{"id":"1"},"players":[{"id":"234567890123456789"}]}"""));
        Assert.Equal("xp.import.guildMismatch", exception.ErrorKey);
    }

    [Fact]
    public void Missing_players_is_invalid_players()
    {
        var exception = Assert.Throws<XpImportParseException>(() => Parse("""{"guild":{"id":"123456789012345678"}}"""));
        Assert.Equal("xp.import.invalidPlayers", exception.ErrorKey);
    }

    [Fact]
    public void Mee6_skips_invalid_ids_and_last_duplicate_wins()
    {
        var parsed = Parse("""
            {"guild":{"id":"123456789012345678"},"players":[
              {"id":"invalid"},
              {"id":"234567890123456789","xp":1},
              {"id":"234567890123456789","xp":2}
            ]}
            """);

        Assert.Equal(1, parsed.SkippedInvalid);
        Assert.Equal(1, parsed.DuplicateUsers);
        Assert.Equal(2m, Assert.Single(parsed.Members).ImportedXp);
    }

    [Fact]
    public void Parses_custom_extended_json_and_exact_xp_formula()
    {
        var parsed = Parse(CustomEntry("""
            "UserName":"Example member",
            "TotalWrittenMessages":{"$numberInt":"321"},
            "TotalVoiceChatSeconds":{"$numberDouble":"48.4343059"},
            "ExtraPoints":13001,"VcPoints":111,"VcPointCent":25,"MessagePoints":1078,
            "ReactionPoints":12,"EventInterestPoints":0,"StagePoints":0,"Mee6Points":23020,"NegativePoints":26000,
            "_id":{"$oid":"688f75fb93192d975695afbb"},"Inactive":true,
            "CreatedAt":{"$date":"0001-01-01T00:00:00Z"},"AppliedVcSettlementVersions":{"x":{"$numberLong":"1"}}
            """));

        var member = Assert.Single(parsed.Members);
        Assert.Equal(XpImportFormat.CustomRankoon, parsed.Format);
        Assert.Equal(UserId, member.UserId);
        Assert.Equal("Example member", member.DisplayName);
        Assert.Equal(321, member.MessageCount);
        Assert.Equal(48.4343059m, member.VoiceSeconds);
        Assert.Equal(11222.25m, member.ImportedXp);
        Assert.Equal(11302.25m, member.ImportedXp + 100m - 20m);
    }

    [Fact]
    public void Missing_custom_point_fields_are_zero_and_unknown_fields_are_ignored()
    {
        var member = Assert.Single(Parse(CustomEntry("\"MessagePoints\":{\"$numberDecimal\":\"12.5\"},\"Unknown\":{\"nested\":true}")).Members);
        Assert.Equal(12.5m, member.ImportedXp);
        Assert.Equal(0, member.MessageCount);
        Assert.Equal(0m, member.VoiceSeconds);
    }

    [Fact]
    public void Custom_filters_foreign_guilds_skips_invalid_and_deduplicates()
    {
        var json = $"[{CustomEntryBody("\"MessagePoints\":1")},{CustomEntryBody("\"MessagePoints\":2")}," +
            "{\"GuildId\":{\"$numberLong\":\"999\"},\"DiscordUserId\":{\"$numberLong\":\"4\"},\"MessagePoints\":1}," +
            "{\"GuildId\":{\"$numberLong\":\"123456789012345678\"},\"DiscordUserId\":\"bad\",\"MessagePoints\":1}]";
        var parsed = Parse(json);

        Assert.Equal(1, parsed.SkippedForeignGuild);
        Assert.Equal(1, parsed.SkippedInvalid);
        Assert.Equal(1, parsed.DuplicateUsers);
        Assert.Equal(2m, Assert.Single(parsed.Members).ImportedXp);
    }

    [Fact]
    public void Only_foreign_custom_entries_return_guild_mismatch()
    {
        var exception = Assert.Throws<XpImportParseException>(() => Parse("""[{"GuildId":"999","DiscordUserId":"4","MessagePoints":1}]"""));
        Assert.Equal("xp.import.guildMismatch", exception.ErrorKey);
    }

    [Fact]
    public void Invalid_matching_entry_is_not_masked_by_a_foreign_entry()
    {
        var exception = Assert.Throws<XpImportParseException>(() => Parse("""
            [{"GuildId":"999","DiscordUserId":"4","MessagePoints":1},
             {"GuildId":"123456789012345678","DiscordUserId":"invalid","MessagePoints":1}]
            """));
        Assert.Equal("xp.import.noValidMembers", exception.ErrorKey);
    }

    [Theory]
    [InlineData("\"MessagePoints\":\"12\"")]
    [InlineData("\"MessagePoints\":-1")]
    [InlineData("\"NegativePoints\":-1")]
    [InlineData("\"TotalWrittenMessages\":1.5")]
    [InlineData("\"TotalWrittenMessages\":-1")]
    [InlineData("\"TotalVoiceChatSeconds\":-1")]
    [InlineData("\"UserName\":1")]
    public void Invalid_custom_values_are_skipped(string field)
    {
        var exception = Assert.Throws<XpImportParseException>(() => Parse(CustomEntry($"{field},\"ExtraPoints\":0")));
        Assert.Equal("xp.import.noValidMembers", exception.ErrorKey);
    }

    [Fact]
    public void Unsafe_numeric_snowflakes_are_rejected()
    {
        using var json = JsonDocument.Parse("123456789012345678");
        Assert.False(XpImportParser.TryReadSnowflake(json.RootElement, out _));
    }

    [Fact]
    public void Unsupported_roots_are_structured_errors()
    {
        foreach (var json in new[] { "[]", "{}", "null", "[1,2]" })
        {
            var exception = Assert.Throws<XpImportParseException>(() => Parse(json));
            Assert.Equal("xp.import.unsupportedFormat", exception.ErrorKey);
        }
    }

    [Fact]
    public void Import_pipeline_replaces_basis_preserves_internal_state_and_conditionally_updates_voice()
    {
        var mee6 = XpImportService.CreateUpdate(GuildId, new(UserId, "Name", 20m, 3, null), false, DateTime.UnixEpoch);
        var custom = XpImportService.CreateUpdate(GuildId, new(UserId, "Name", 11222.25m, 321, 48.4343059m), false, DateTime.UnixEpoch);
        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<MemberXp>();
        var registry = BsonSerializer.SerializerRegistry;
        var mee6Pipeline = mee6.Update.Render(new(serializer, registry)).AsBsonArray.ToJson();
        var customPipeline = custom.Update.Render(new(serializer, registry)).AsBsonArray.ToJson();

        Assert.Contains("imported_mee6_xp", customPipeline);
        Assert.Contains("11222.25", customPipeline);
        Assert.Contains("earned_xp", customPipeline);
        Assert.Contains("manual_adjustment", customPipeline);
        Assert.DoesNotContain("voice_seconds", mee6Pipeline);
        Assert.Contains("48.4343059", customPipeline);
        Assert.DoesNotContain("$oid", customPipeline);
        Assert.DoesNotContain("inactive", customPipeline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("applied", customPipeline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Existing_integer_import_and_voice_values_deserialize_as_decimals()
    {
        var document = new BsonDocument
        {
            { "guild_id", new BsonDecimal128((decimal)GuildId) },
            { "user_id", new BsonDecimal128((decimal)UserId) },
            { "imported_mee6_xp", 1234L },
            { "voice_seconds", 48L }
        };
        var member = BsonSerializer.Deserialize<MemberXp>(document);
        Assert.Equal(1234m, member.ImportedMee6Xp);
        Assert.Equal(48m, member.VoiceSeconds);
    }

    private ParsedXpImport Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return parser.Parse(document.RootElement, GuildId);
    }

    private static string CustomEntry(string fields) => $"[{CustomEntryBody(fields)}]";
    private static string CustomEntryBody(string fields) => $$"""
        {"GuildId":{"$numberLong":"123456789012345678"},"DiscordUserId":{"$numberLong":"234567890123456789"},{{fields}}}
        """;
}
