using System.Text.Json;
using Discord;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Xunit;

namespace Backend.Tests;

public sealed class RankoonCommandPresentationTests
{
    [Theory]
    [InlineData(0, "0", "0 %", "100 XP")]
    [InlineData(50, "0", "50 %", "50 XP")]
    [InlineData(99, "0", "99 %", "1 XP")]
    [InlineData(100, "1", "0 %", "154 XP")]
    [InlineData(253, "1", "99 %", "1 XP")]
    [InlineData(254, "2", "0 %", "220 XP")]
    public void Rank_progress_resets_at_level_boundaries(int total, string level, string percent, string remaining)
    {
        var card = RankoonCommandPresentation.Rank("Player", null, new MemberXp { EarnedXp = total });
        Assert.Equal($"**{level}**", card.Fields[0].Value);
        Assert.Contains(percent, card.Fields[2].Value);
        Assert.Contains(remaining, card.Fields[2].Value);
        Assert.Equal(RankoonCommandPresentation.Brand, card.Color);
    }

    [Fact]
    public void Rank_uses_all_xp_sources_and_never_rounds_incomplete_progress_to_100_percent()
    {
        var card = RankoonCommandPresentation.Rank("Player", null,
            new MemberXp { ImportedMee6Xp = 80, EarnedXp = 30, ManualAdjustment = -10.1m });
        Assert.Contains("99 %", card.Fields[2].Value);
        Assert.Contains("1 XP", card.Fields[2].Value);
    }

    [Fact]
    public void New_member_and_empty_leaderboard_have_a_next_step()
    {
        var rank = RankoonCommandPresentation.Rank("Newcomer", null, null);
        Assert.Contains("Deine Reise", rank.Description);
        Assert.Contains("100 XP", rank.Fields[2].Value);
        var leaderboard = RankoonCommandPresentation.Leaderboard("Server", [], 1);
        Assert.Contains("/rank", leaderboard.Fields.Single().Value);
    }

    [Fact]
    public void Leaderboard_is_bounded_and_marks_the_viewer_without_user_mentions()
    {
        var entries = Enumerable.Range(1, 20).Select(i => new MemberXp
        {
            UserId = (ulong)i, DisplayName = new string('*', 200) + "@everyone", EarnedXp = 1000 - i
        }).ToArray();
        var card = RankoonCommandPresentation.Leaderboard("**@everyone**\nServer", entries, 3);
        Assert.Contains("🥇", card.Description);
        Assert.Contains("🥈", card.Description);
        Assert.Contains("🥉", card.Description);
        Assert.Contains(" · du", card.Description);
        Assert.DoesNotContain("@everyone", card.Description);
        Assert.Equal(10, card.Description.Split("XP").Length - 1);
        Assert.True(card.Description.Length <= 4096);
        Assert.True(card.Length <= 6000);
    }

    [Fact]
    public void User_labels_cannot_add_markdown_links_or_mentions()
    {
        var text = RankoonCommandPresentation.Text("[click](https://evil.example) <@123>\n**hi**", 100);
        Assert.Contains("\\[click\\]\\(", text);
        Assert.DoesNotContain("<@123>", text);
        Assert.DoesNotContain("\n", text);
        Assert.Equal("Unbekannt", RankoonCommandPresentation.Text("\n", 100));
    }

    [Fact]
    public void Every_card_is_within_discord_embed_limits()
    {
        Embed[] cards = [RankoonCommandPresentation.Help(), RankoonCommandPresentation.Rank("Player", null, null),
            RankoonCommandPresentation.VoiceStatus(new string('*', 100), 1, 2, 0, 3),
            RankoonCommandPresentation.Done("Gespeichert", "Kanal aktualisiert."), RankoonCommandPresentation.Error("Fehler", "Nutze /help.")];
        foreach (var card in cards)
        {
            Assert.True(card.Length <= 6000);
            Assert.True(card.Title.Length <= 256);
            Assert.True(card.Description.Length <= 4096);
            Assert.All(card.Fields, field => Assert.True(field.Value.Length <= 1024));
            Assert.Contains("Nur für dich", card.Footer!.Value.Text);
        }
    }

    [Fact]
    public void Platform_and_custom_bot_schemas_share_typed_voice_subcommands()
    {
        var provider = new RankoonCommandSchemaProvider();
        var native = provider.GetCommands().Cast<SlashCommandProperties>().Single(x => x.Name.Value == "voice");
        using var rest = JsonDocument.Parse(JsonSerializer.Serialize(provider.GetRestPayload()));
        var voice = rest.RootElement.EnumerateArray().Single(x => x.GetProperty("name").GetString() == "voice");
        var options = voice.GetProperty("options").EnumerateArray().ToArray();
        Assert.All(rest.RootElement.EnumerateArray(), x => Assert.Equal(0, x.GetProperty("contexts")[0].GetInt32()));
        Assert.Equal(native.Options.Value.Select(x => x.Name), options.Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(["status", "name", "limit", "kick", "transfer"], options.Select(x => x.GetProperty("name").GetString()));
        Assert.All(options, x => Assert.Equal(1, x.GetProperty("type").GetInt32()));
        var limit = options.Single(x => x.GetProperty("name").GetString() == "limit").GetProperty("options")[0];
        Assert.Equal(4, limit.GetProperty("type").GetInt32());
        Assert.Equal(0, limit.GetProperty("min_value").GetDouble());
        Assert.Equal(99, limit.GetProperty("max_value").GetDouble());
        Assert.True(limit.GetProperty("required").GetBoolean());
        var name = options.Single(x => x.GetProperty("name").GetString() == "name").GetProperty("options")[0];
        Assert.Equal(1, name.GetProperty("min_length").GetInt32());
        Assert.Equal(100, name.GetProperty("max_length").GetInt32());
    }

    [Theory]
    [InlineData("limit", "-1", false)]
    [InlineData("limit", "100", false)]
    [InlineData("limit", "1.5", false)]
    [InlineData("limit", "0", true)]
    [InlineData("limit", "99", true)]
    [InlineData("name", "   ", false)]
    [InlineData("name", "Gaming\nLounge", false)]
    [InlineData("name", "Gaming Lounge", true)]
    [InlineData("unknown", null, false)]
    public void Invalid_voice_inputs_are_rejected_without_clamping(string action, string? value, bool valid) =>
        Assert.Equal(valid, RankoonVoiceCommandValidation.Validate(action, value, 1, null, false) == null);

    [Theory]
    [InlineData("kick")]
    [InlineData("transfer")]
    public void Member_actions_require_another_member_in_the_same_channel(string action)
    {
        Assert.NotNull(RankoonVoiceCommandValidation.Validate(action, null, 1, null, false));
        Assert.NotNull(RankoonVoiceCommandValidation.Validate(action, null, 1, 1, true));
        Assert.NotNull(RankoonVoiceCommandValidation.Validate(action, null, 1, 2, false));
        Assert.Null(RankoonVoiceCommandValidation.Validate(action, null, 1, 2, true));
        Assert.NotNull(RankoonVoiceCommandValidation.Validate("name", new string('a', 101), 1, null, false));
    }
}
