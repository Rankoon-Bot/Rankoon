using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class ActivityXpIngressTests
{
    private static readonly ActivityXpMember Member = new(2, "Member", false, [4], DateTimeOffset.UtcNow.AddYears(-1));

    [Theory]
    [InlineData("message", 5, 60, "message:100")]
    [InlineData("thread_message", 5, 60, "thread_message:100")]
    [InlineData("thread_create", 15, null, "thread:100")]
    [InlineData("reaction", 2, 30, "reaction:100:2:emoji")]
    [InlineData("event_interest", 10, null, "event-interest:100:2")]
    public async Task Every_gateway_source_builds_the_correct_immutable_ledger_request(string source, int amount, int? cooldown, string key)
    {
        var xp = new Xp();
        var processor = Processor(xp);
        Assert.True(await processor.GrantAsync(new(1, 100, source, Member, ChannelId: 3, MessageLength: 1, Emote: "emoji")));
        var request = Assert.Single(xp.Grants);
        Assert.Equal(1UL, request.GuildId); Assert.Equal(2UL, request.UserId); Assert.Equal(3UL, request.ChannelId);
        Assert.Equal("Member", request.DisplayName);
        Assert.Equal(source, request.Source); Assert.Equal(amount, request.Amount);
        Assert.Equal(cooldown, request.CooldownSeconds); Assert.Equal(key, request.GrantKey);
        Assert.Equal(DateTimeKind.Utc, request.OccurredAtUtc.Kind);
        XpService.ValidateGrant(request);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("thread_message")]
    [InlineData("thread_create")]
    [InlineData("reaction")]
    [InlineData("event_interest")]
    public async Task Bots_disabled_rules_and_role_exclusions_never_reach_the_ledger(string source)
    {
        var xp = new Xp(); var processor = Processor(xp);
        var input = new ActivityXpInput(1, 100, source, Member, MessageLength: 100);
        Assert.False(await processor.GrantAsync(input with { Member = Member with { IsBot = true } }));
        xp.Settings.Enabled = false;
        Assert.False(await processor.GrantAsync(input));
        xp.Settings.Enabled = true; xp.Settings.ExcludedRoleIds = [4];
        Assert.False(await processor.GrantAsync(input));
        xp.Settings.ExcludedRoleIds.Clear();
        xp.Settings.Message.Enabled = xp.Settings.Reaction.Enabled = xp.Settings.Thread.Enabled = xp.Settings.EventInterest.Enabled = false;
        Assert.False(await processor.GrantAsync(input));
        Assert.Empty(xp.Grants);
    }

    [Theory]
    [InlineData("message")]
    [InlineData("thread_message")]
    [InlineData("thread_create")]
    [InlineData("reaction")]
    [InlineData("event_interest")]
    public async Task All_source_rates_apply_channel_and_booster_multipliers(string source)
    {
        var xp = new Xp();
        xp.Settings.ChannelMultipliers = [new() { ChannelId = 3, Multiplier = 2 }];
        xp.Settings.ServerBooster = new() { Enabled = true, Tiers = [new() { Multiplier = 1.5m }] };
        var processor = Processor(xp);
        var input = new ActivityXpInput(1, 100, source, Member, ChannelId: 3, MessageLength: 500, Emote: "emoji");
        await processor.GrantAsync(input);
        var request = Assert.Single(xp.Grants);
        var baseAmount = source switch { "message" => 50, "thread_message" => 5, "thread_create" => 15, "reaction" => 2, _ => 10 };
        Assert.Equal(baseAmount * 3, request.Amount);
        Assert.Equal(1.5m, request.AppliedServerBoosterMultiplier);
        xp.Settings.ChannelMultipliers[0].Multiplier = 0;
        Assert.False(await processor.GrantAsync(input with { EventId = 101 }));
        Assert.Single(xp.Grants);
    }

    [Theory]
    [InlineData("thread_message")]
    [InlineData("thread_create")]
    [InlineData("reaction")]
    public async Task Parent_channel_and_category_exclusions_apply_to_thread_activity(string source)
    {
        var xp = new Xp(); var processor = Processor(xp);
        var input = new ActivityXpInput(1, 100, source, Member, ChannelId: 3, CategoryId: 5, ParentChannelId: 6);
        xp.Settings.ExcludedChannelIds = [6];
        Assert.False(await processor.GrantAsync(input));
        xp.Settings.ExcludedChannelIds.Clear(); xp.Settings.ExcludedCategoryIds = [5];
        Assert.False(await processor.GrantAsync(input));
        Assert.Empty(xp.Grants);
    }

    [Fact]
    public async Task Short_and_empty_messages_and_zero_point_sources_never_write_a_grant()
    {
        var xp = new Xp(); var processor = Processor(xp);
        xp.Settings.Message.MinimumCharacters = 20;
        Assert.False(await processor.GrantAsync(new(1, 100, "message", Member, MessageLength: 19)));
        Assert.False(await processor.GrantAsync(new(1, 100, "message", Member)));
        xp.Settings.Reaction.Points = 0;
        Assert.False(await processor.GrantAsync(new(1, 100, "reaction", Member)));
        Assert.Empty(xp.Grants);
    }

    [Fact]
    public async Task External_events_without_a_channel_use_the_default_multiplier()
    {
        var xp = new Xp(); var processor = Processor(xp);
        Assert.True(await processor.GrantAsync(new(1, 100, "event_interest", Member)));
        Assert.Null(Assert.Single(xp.Grants).ChannelId);
        Assert.Equal(10, xp.Grants[0].Amount);
    }

    [Fact]
    public async Task Invalid_sources_fail_visibly_and_failed_ledger_acceptance_is_returned()
    {
        var xp = new Xp(); var processor = Processor(xp);
        await Assert.ThrowsAsync<ArgumentException>(() => processor.GrantAsync(new(1, 100, "unknown", Member)));
        xp.Accept = false;
        Assert.False(await processor.GrantAsync(new(1, 100, "event_interest", Member)));
    }

    private static ActivityXpGrantProcessor Processor(Xp xp) => new(xp, new(TimeProvider.System), TimeProvider.System);
    private sealed class Xp : IXpService
    {
        public GuildXpSettings Settings = new() { GuildId = 1 };
        public List<XpGrantRequest> Grants = [];
        public bool Accept = true;
        public Task<GuildXpSettings> GetSettingsAsync(ulong guild, CancellationToken ct = default) => Task.FromResult(Settings);
        public Task<bool> GrantAsync(XpGrantRequest request, CancellationToken ct = default) { Grants.Add(request); return Task.FromResult(Accept); }
        public Task<bool> GrantAsync(ulong guild, ulong user, string name, string source, decimal amount, string key, ulong? channel = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GuildXpSettings> SaveSettingsAsync(GuildXpSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ReverseGrantAsync(string original, string reversal, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ReverseGrantAsync(string original, string reversal, string source, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MemberXp>> GetLeaderboardAsync(ulong guild, int take, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MemberXp?> GetMemberAsync(ulong guild, ulong user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecalculateTotalAsync(ulong guild, ulong user, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
