using Discord;
using Rankoon.Data.Xp;

namespace Rankoon.Data.Discord;

internal sealed record ActivityXpMember(ulong UserId, string DisplayName, bool IsBot, IReadOnlyCollection<ulong> RoleIds, DateTimeOffset? PremiumSince)
{
    internal static ActivityXpMember From(IGuildUser member) => new(member.Id, member.DisplayName, member.IsBot, member.RoleIds.ToArray(), member.PremiumSince);
}

internal sealed record ActivityXpInput(ulong GuildId, ulong EventId, string Source, ActivityXpMember Member,
    ulong? ChannelId = null, ulong? CategoryId = null, ulong? ParentChannelId = null, int MessageLength = 0, string? Emote = null);

/// <summary>Source-to-ledger contract, independent of Discord's socket cache.</summary>
internal sealed class ActivityXpGrantProcessor(IXpService xp, ServerBoosterXpMultiplierResolver boosters, TimeProvider timeProvider)
{
    internal async Task<bool> GrantAsync(ActivityXpInput input)
    {
        if (input.Member.IsBot) return false;
        var settings = await xp.GetSettingsAsync(input.GuildId);
        if (ActivityXpPolicy.IsExcluded(settings, input.Member.RoleIds, input.ChannelId, input.CategoryId, input.ParentChannelId)) return false;
        var (enabled, amount, cooldown, key) = input.Source switch
        {
            "message" => (settings.Message.Enabled, ActivityXpPolicy.MessagePoints(input.MessageLength, settings.Message), (int?)settings.Message.CooldownSeconds, $"message:{input.EventId}"),
            "thread_message" => (settings.Thread.Enabled, (decimal)settings.Thread.MessagePoints, (int?)settings.Thread.CooldownSeconds, $"thread_message:{input.EventId}"),
            "thread_create" => (settings.Thread.Enabled, (decimal)settings.Thread.CreatePoints, null, $"thread:{input.EventId}"),
            "reaction" => (settings.Reaction.Enabled, (decimal)settings.Reaction.Points, (int?)settings.Reaction.CooldownSeconds, $"reaction:{input.EventId}:{input.Member.UserId}:{input.Emote}"),
            "event_interest" => (settings.EventInterest.Enabled, (decimal)settings.EventInterest.Points, null, $"event-interest:{input.EventId}:{input.Member.UserId}"),
            _ => throw new ArgumentException("Unsupported activity XP source.", nameof(input))
        };
        if (!enabled || amount <= 0) return false;
        var now = timeProvider.GetUtcNow();
        var multiplier = input.ChannelId is { } channelId ? ActivityXpPolicy.ChannelMultiplier(settings, channelId, input.ParentChannelId) : 1m;
        var award = boosters.Apply(input.Source, amount, multiplier, settings, input.Member.PremiumSince, now);
        if (award.Amount <= 0) return false;
        return await xp.GrantAsync(new XpGrantRequest(input.GuildId, input.Member.UserId, input.Member.DisplayName, input.Source, award.Amount, key,
            now.UtcDateTime, input.ChannelId, CooldownSeconds: cooldown, AppliedServerBoosterMultiplier: award.AppliedServerBoosterMultiplier));
    }
}
