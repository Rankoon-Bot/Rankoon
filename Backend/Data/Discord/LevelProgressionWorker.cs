using Discord;
using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;
using Rankoon.Data.Xp;
using Rankoon.Data.Operations;
using Rankoon.Controllers;

namespace Rankoon.Data.Discord;

public interface IDiscordAnnouncementSender
{
    Task<ulong> SendAsync(SocketTextChannel channel, string content, ulong targetUserId, bool notifyUser, CancellationToken cancellationToken = default);
}

public sealed class DiscordAnnouncementSender : IDiscordAnnouncementSender
{
    public async Task<ulong> SendAsync(SocketTextChannel channel, string content, ulong targetUserId, bool notifyUser, CancellationToken cancellationToken = default)
    {
        // Only the transition user may be pinged. Role mentions remain visual text.
        var mentions = new AllowedMentions(AllowedMentionTypes.Users) { UserIds = notifyUser ? [targetUserId] : [] };
        var message = await channel.SendMessageAsync(content, allowedMentions: mentions, options: new RequestOptions { CancelToken = cancellationToken });
        return message.Id;
    }
}

public sealed class LevelProgressionWorker(RankoonDbContext database, IGuildDiscordContextResolver discord, LevelRoleService roles, SeasonLevelRoleService seasonRoles, ILevelUpTemplateRenderer renderer, LevelUpTemplateSelector selector, IDiscordAnnouncementSender sender, IReportWriter reports, IOperationalErrorRecorder errors, IWorkerHealthRegistry health, TimeProvider timeProvider, ILogger<LevelProgressionWorker> logger) : BackgroundService
{
    private readonly string owner = Guid.NewGuid().ToString("N");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var work = await database.LevelTransitionEvents.Find(x => (x.Status == LevelTransitionStatus.Pending || x.Status == LevelTransitionStatus.RetryScheduled) && x.NextAttemptAtUtc <= timeProvider.GetUtcNow().UtcDateTime).Limit(20).ToListAsync(stoppingToken);
                foreach (var item in work) await ProcessAsync(item, stoppingToken);
                health.Report("level-progression", WorkerHealthState.Healthy);
                await Task.Delay(work.Count == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Level progression worker failed"); health.Report("level-progression", WorkerHealthState.Degraded, exception.GetType().Name); await errors.RecordAsync(new(exception, "worker", "level.progression", Worker: "level-progression"), stoppingToken); await Task.Delay(TimeSpan.FromSeconds(10), timeProvider, stoppingToken); }
        }
    }

    private async Task ProcessAsync(LevelTransitionEvent item, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claimed = await database.LevelTransitionEvents.FindOneAndUpdateAsync(x => x.Id == item.Id && (x.Status == LevelTransitionStatus.Pending || x.Status == LevelTransitionStatus.RetryScheduled) && (x.LeaseExpiresAtUtc == null || x.LeaseExpiresAtUtc < now),
            Builders<LevelTransitionEvent>.Update.Set(x => x.Status, LevelTransitionStatus.Processing).Set(x => x.LeaseOwner, owner).Set(x => x.LeaseExpiresAtUtc, now.AddMinutes(2)), new FindOneAndUpdateOptions<LevelTransitionEvent> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        if (claimed == null) return;
        try
        {
            var roleResult = claimed.Scope == LevelProgressScope.Lifetime
                ? await roles.SynchronizeAsync(claimed.GuildId, claimed.UserId, cancellationToken)
                : !string.IsNullOrWhiteSpace(claimed.SeasonId) ? await seasonRoles.SynchronizeAsync(claimed.GuildId, claimed.SeasonId, claimed.UserId, cancellationToken) : new LevelRoleSynchronizationResult([], [], [], []);
            if (roleResult.Failed.Count > 0) await ReportAsync(claimed, ReportNames.LevelRolesPartiallyFailed, ReportOutcomes.Failed, cancellationToken);
            var settings = await database.GuildLevelUpAnnouncementSettings.Find(x => x.GuildId == claimed.GuildId).FirstOrDefaultAsync(cancellationToken);
            if (settings is { SchemaVersion: < 2 }) settings = LevelUpAnnouncementsController.Migrate(settings);
            var ledger = string.IsNullOrWhiteSpace(claimed.LedgerGrantKey) ? null : await database.XpLedger.Find(x => x.GrantKey == claimed.LedgerGrantKey).FirstOrDefaultAsync(cancellationToken);
            var cause = ResolveCause(claimed, ledger);
            var profile = claimed.Scope == LevelProgressScope.Season ? settings?.Season : settings?.Lifetime;
            var canAnnounce = profile?.Enabled == true && profile.ChannelId.HasValue && claimed.NewLevel > claimed.PreviousLevel && !cause.SuppressAnnouncement;
            if (!canAnnounce) { await CompleteAsync(claimed, LevelTransitionStatus.CompletedWithoutAnnouncement, null, cancellationToken); return; }
            var announcementSettings = profile!;
            var guild = (await discord.ResolveAsync(claimed.GuildId, cancellationToken))?.Guild; var user = guild?.GetUser(claimed.UserId); var channel = guild?.GetTextChannel(announcementSettings.ChannelId!.Value);
            if (guild == null || user == null || channel == null) { await FailAsync(claimed, "channelUnavailable", true, cancellationToken); return; }
            var member = await database.MemberXp.Find(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId).FirstOrDefaultAsync(cancellationToken) ?? new MemberXp();
            var context = new LevelUpRenderContext($"<@{claimed.UserId}>", user.DisplayName, user.Username, claimed.UserId, claimed.PreviousLevel, claimed.NewLevel, claimed.PreviousTotalXp, claimed.NewTotalXp, cause.GainedXp, claimed.Source, cause.ChannelId is { } sourceChannel ? $"<#{sourceChannel}>" : null, guild.Name, guild.MemberCount, member.MessageCount, member.VoiceSeconds, null, roleResult.Added.OrderBy(x => x.RequiredLevel).ToArray()) { Scope = claimed.Scope, SeasonId = claimed.SeasonId, SeasonName = claimed.SeasonNameSnapshot };
            var recentEvents = await database.LevelTransitionEvents.Find(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId && x.Scope == claimed.Scope && x.Status == LevelTransitionStatus.Delivered).SortByDescending(x => x.CompletedAtUtc).Limit(announcementSettings.AvoidRecentMessagesPerUser).Project(x => new { x.SelectedGroupId, x.SelectedMessageId }).ToListAsync(cancellationToken);
            var kind = roleResult.Added.Count > 0 ? LevelAnnouncementKind.Reward : LevelAnnouncementKind.LevelUp;
            var selection = selector.Select(announcementSettings, kind, context, recentEvents.Where(x => x.SelectedGroupId != null).Select(x => x.SelectedGroupId!).ToArray(), recentEvents.Where(x => x.SelectedMessageId != null).Select(x => x.SelectedMessageId!).ToArray());
            if (selection == null && kind == LevelAnnouncementKind.Reward) selection = selector.Select(announcementSettings, LevelAnnouncementKind.LevelUp, context, [], []);
            var contentTemplate = selection?.Message.Content ?? (announcementSettings.UseDefaultFallback ? LevelAnnouncementFallbacks.Content(announcementSettings.FallbackLocale, kind == LevelAnnouncementKind.Reward) : null);
            if (contentTemplate == null) { await ReportAsync(claimed, ReportNames.LevelAnnouncementNoTemplate, ReportOutcomes.Rejected, cancellationToken); await CompleteAsync(claimed, LevelTransitionStatus.CompletedWithoutAnnouncement, null, cancellationToken); return; }
            var rendered = renderer.Render(contentTemplate, context);
            var content = rendered.Content;
            if (content == null || content.Length > 2000) { await FailAsync(claimed, "templateRenderingFailed", false, cancellationToken); return; }
            var messageId = await sender.SendAsync(channel, content, claimed.UserId, announcementSettings.NotifyUser, cancellationToken);
            await database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == claimed.Id && x.LeaseOwner == owner, Builders<LevelTransitionEvent>.Update.Set(x => x.Status, LevelTransitionStatus.Delivered).Set(x => x.DiscordMessageId, messageId).Set(x => x.DeliveryChannelId, channel.Id).Set(x => x.SelectedKind, selection?.Kind).Set(x => x.SelectedGroupId, selection?.Group.Id).Set(x => x.SelectedMessageId, selection?.Message.Id).Set(x => x.RewardRoleIds, roleResult.Added.Select(x => x.RoleId).ToList()).Set(x => x.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime).Unset(x => x.LeaseOwner).Unset(x => x.LeaseExpiresAtUtc), cancellationToken: cancellationToken);
            await ReportAsync(claimed, ReportNames.LevelAnnouncementSent, ReportOutcomes.Succeeded, cancellationToken);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Level announcement delivery failed for {EventKey}", claimed.EventKey); await errors.RecordAsync(new(exception, "discord", "level.announcement", GuildId: claimed.GuildId, Worker: "level-progression", Context: new Dictionary<string, object?> { ["eventId"] = claimed.EventKey }), cancellationToken); await FailAsync(claimed, "discordTemporaryFailure", true, cancellationToken); }
    }
    private Task CompleteAsync(LevelTransitionEvent e, LevelTransitionStatus status, string? error, CancellationToken ct) => database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == e.Id && x.LeaseOwner == owner, Builders<LevelTransitionEvent>.Update.Set(x => x.Status, status).Set(x => x.LastErrorCode, error).Set(x => x.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime).Unset(x => x.LeaseOwner).Unset(x => x.LeaseExpiresAtUtc), cancellationToken: ct);
    private Task FailAsync(LevelTransitionEvent e, string code, bool temporary, CancellationToken ct)
    {
        var attempts = e.DeliveryAttempts + 1; var delays = new[] { 30, 120, 600, 1800 }; var dead = !temporary || attempts > delays.Length;
        return database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == e.Id && x.LeaseOwner == owner, Builders<LevelTransitionEvent>.Update.Set(x => x.DeliveryAttempts, attempts).Set(x => x.LastErrorCode, code).Set(x => x.Status, dead ? LevelTransitionStatus.DeadLetter : LevelTransitionStatus.RetryScheduled).Set(x => x.NextAttemptAtUtc, dead ? null : timeProvider.GetUtcNow().UtcDateTime.AddSeconds(delays[attempts - 1])).Set(x => x.CompletedAtUtc, dead ? timeProvider.GetUtcNow().UtcDateTime : null).Unset(x => x.LeaseOwner).Unset(x => x.LeaseExpiresAtUtc), cancellationToken: ct);
    }
    private Task ReportAsync(LevelTransitionEvent e, string name, string outcome, CancellationToken ct) => reports.WriteAsync(new(e.GuildId, ReportCategories.Activity, name, outcome, e.Source, e.UserId, Metadata: new Dictionary<string, object?> { ["userId"] = e.UserId, ["level"] = e.NewLevel, ["source"] = e.Source }, SubjectId: e.UserId), ct);

    internal static LevelTransitionCause ResolveCause(LevelTransitionEvent transition, XpLedgerEntry? ledger) => new(
        transition.Source,
        string.IsNullOrWhiteSpace(transition.CauseKey) ? transition.LedgerGrantKey ?? transition.EventKey : transition.CauseKey,
        transition.GainedXp != 0 ? transition.GainedXp : transition.NewTotalXp - transition.PreviousTotalXp,
        transition.SourceChannelId ?? ledger?.ChannelId,
        transition.LedgerGrantKey,
        transition.SuppressAnnouncement || ledger != null && XpLedgerSemantics.GetEffectiveKind(ledger) == XpLedgerEntryKind.SystemMigration);
}
