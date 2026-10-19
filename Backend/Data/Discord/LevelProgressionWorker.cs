using Discord;
using Discord.Net;
using Discord.WebSocket;
using System.Net;
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
    internal static AllowedMentions CreateAllowedMentions(ulong targetUserId, bool notifyUser) => notifyUser
        ? new AllowedMentions(null) { UserIds = [targetUserId] }
        : AllowedMentions.None;

    public async Task<ulong> SendAsync(SocketTextChannel channel, string content, ulong targetUserId, bool notifyUser, CancellationToken cancellationToken = default)
    {
        // Only the transition user may be pinged. Role mentions remain visual text.
        var mentions = CreateAllowedMentions(targetUserId, notifyUser);
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
                var work = await database.LevelTransitionEvents.Find(EligibleWork(timeProvider.GetUtcNow().UtcDateTime)).SortBy(x => x.CreatedAtUtc).Limit(20).ToListAsync(stoppingToken);
                foreach (var item in work) await ProcessAsync(item, stoppingToken);
                health.Report("level-progression", WorkerHealthState.Healthy);
                await Task.Delay(work.Count == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Level progression worker failed");
                health.Report("level-progression", WorkerHealthState.Degraded, exception.GetType().Name);
                try { await errors.RecordAsync(new(exception, "worker", "level.progression", Worker: "level-progression"), stoppingToken); }
                catch (Exception recordingException) when (recordingException is not OperationCanceledException) { logger.LogWarning(recordingException, "Could not record level progression failure"); }
                await Task.Delay(TimeSpan.FromSeconds(10), timeProvider, stoppingToken);
            }
        }
    }

    internal async Task ProcessAsync(LevelTransitionEvent item, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // A unique token per claim fences a previous invocation, including one from this process.
        var claimOwner = $"{owner}:{Guid.NewGuid():N}";
        var claimed = await database.LevelTransitionEvents.FindOneAndUpdateAsync(Builders<LevelTransitionEvent>.Filter.Eq(x => x.Id, item.Id) & EligibleWork(now),
            Builders<LevelTransitionEvent>.Update.Set(x => x.Status, LevelTransitionStatus.Processing).Set(x => x.LeaseOwner, claimOwner).Set(x => x.LeaseExpiresAtUtc, now.AddMinutes(2)), new FindOneAndUpdateOptions<LevelTransitionEvent> { ReturnDocument = ReturnDocument.After }, cancellationToken);
        if (claimed == null) return;
        var stoppingToken = cancellationToken;
        ulong? deliveryChannelId = null;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(90));
        cancellationToken = attempt.Token;
        try
        {
            var roleResult = claimed.Scope == LevelProgressScope.Lifetime
                ? await roles.SynchronizeAsync(claimed.GuildId, claimed.UserId, cancellationToken)
                : !string.IsNullOrWhiteSpace(claimed.SeasonId) ? await seasonRoles.SynchronizeAsync(claimed.GuildId, claimed.SeasonId, claimed.UserId, cancellationToken) : new LevelRoleSynchronizationResult([], [], [], []);
            var rewarded = roleResult.Added.Concat(roleResult.AlreadyPresent.Where(x => claimed.RewardRoleIds.Contains(x.RoleId))).DistinctBy(x => x.RoleId).ToArray();
            claimed.RewardRoleIds = rewarded.Select(x => x.RoleId).ToList();
            var checkpoint = await database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == claimed.Id && x.LeaseOwner == claimed.LeaseOwner,
                Builders<LevelTransitionEvent>.Update.Set(x => x.RewardRoleIds, claimed.RewardRoleIds), cancellationToken: cancellationToken);
            if (checkpoint.MatchedCount == 0) return;
            if (roleResult.Failed.Count > 0)
            {
                var roleConfigurationFailed = roleResult.Failed.Any(x => x.ErrorCode is "roleNotFound" or "missingAccess" or "missingPermissions");
                var recoveryAction = roleResult.Failed.Any(x => x.ErrorCode == "missingPermissions")
                    ? "Grant Manage Roles and move the bot role above the configured reward roles."
                    : roleResult.Failed.Any(x => x.ErrorCode is "roleNotFound" or "missingAccess")
                        ? "Choose an existing reward role and make sure the bot can access the server."
                        : "Check the Discord connection and retry after it recovers.";
                logger.Log(roleConfigurationFailed ? LogLevel.Warning : LogLevel.Information,
                    "Level transition {EventKey} could not synchronize configured roles: {Failures}. {Disposition}. Recovery: {RecoveryAction}",
                    claimed.EventKey, string.Join(", ", roleResult.Failed.Select(x => $"{x.RoleId}:{x.ErrorCode}")),
                    roleConfigurationFailed ? "delivery stopped" : "will retry", recoveryAction);
                await ReportAsync(claimed, ReportNames.LevelRolesPartiallyFailed, ReportOutcomes.Failed, cancellationToken);
                await FailAsync(claimed, roleConfigurationFailed ? "roleConfigurationFailed" : "roleSynchronizationFailed", !roleConfigurationFailed, cancellationToken);
                return;
            }
            var settings = await new XpSettingsStore(database, timeProvider).LoadAnnouncementsAsync(claimed.GuildId, cancellationToken);
            var ledger = string.IsNullOrWhiteSpace(claimed.LedgerGrantKey) ? null : await database.XpLedger.Find(x => x.GrantKey == claimed.LedgerGrantKey).FirstOrDefaultAsync(cancellationToken);
            var cause = ResolveCause(claimed, ledger);
            var profile = claimed.Scope == LevelProgressScope.Season ? settings?.Season : settings?.Lifetime;
            var canAnnounce = profile?.Enabled == true && profile.ChannelId.HasValue && claimed.NewLevel > claimed.PreviousLevel && !cause.SuppressAnnouncement;
            if (!canAnnounce) { await CompleteAsync(claimed, LevelTransitionStatus.CompletedWithoutAnnouncement, null, cancellationToken); return; }
            var announcementSettings = profile!;
            var discordContext = await discord.ResolveAsync(claimed.GuildId, cancellationToken);
            var guild = discordContext?.Guild;
            if (guild == null)
            {
                logger.LogInformation("Level announcement deferred for {EventKey}; guild {GuildId} is not connected", claimed.EventKey, claimed.GuildId);
                await FailAsync(claimed, "discordGuildUnavailable", true, cancellationToken);
                return;
            }
            var user = await LevelRoleDiscordMember.FetchAsync(discordContext!, claimed.UserId, cancellationToken);
            if (user == null)
            {
                logger.LogInformation("Level announcement closed for {EventKey}; member {UserId} is no longer available in guild {GuildId}", claimed.EventKey, claimed.UserId, claimed.GuildId);
                await FailAsync(claimed, "announcementMemberUnavailable", false, cancellationToken);
                return;
            }
            var channel = guild.GetTextChannel(announcementSettings.ChannelId!.Value);
            if (channel == null)
            {
                logger.LogWarning("Level announcement closed for {EventKey}; configured channel {ChannelId} is unavailable in guild {GuildId}. Choose an available text channel in the announcement settings",
                    claimed.EventKey, announcementSettings.ChannelId.Value, claimed.GuildId);
                await FailAsync(claimed, "announcementChannelUnavailable", false, cancellationToken);
                return;
            }
            deliveryChannelId = channel.Id;
            var member = await database.MemberXp.Find(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId).FirstOrDefaultAsync(cancellationToken) ?? new MemberXp();
            var context = new LevelUpRenderContext($"<@{claimed.UserId}>", user.Nickname ?? user.GlobalName ?? user.Username, user.Username, claimed.UserId, claimed.PreviousLevel, claimed.NewLevel, claimed.PreviousTotalXp, claimed.NewTotalXp, cause.GainedXp, claimed.Source, cause.ChannelId is { } sourceChannel ? $"<#{sourceChannel}>" : null, guild.Name, guild.MemberCount, member.MessageCount, member.VoiceSeconds, null, rewarded.OrderBy(x => x.RequiredLevel).ToArray()) { Scope = claimed.Scope, SeasonId = claimed.SeasonId, SeasonName = claimed.SeasonNameSnapshot };
            var kind = rewarded.Length > 0 ? LevelAnnouncementKind.Reward : LevelAnnouncementKind.LevelUp;
            var recentUserEvents = announcementSettings.AvoidRecentMessagesPerUser == 0
                ? Array.Empty<LevelAnnouncementRecentSelection>()
                : (await database.LevelTransitionEvents.Find(x => x.GuildId == claimed.GuildId && x.UserId == claimed.UserId && x.Scope == claimed.Scope && x.Status == LevelTransitionStatus.Delivered && (claimed.Scope != LevelProgressScope.Season || x.SeasonId == claimed.SeasonId)).SortByDescending(x => x.CompletedAtUtc).Limit(announcementSettings.AvoidRecentMessagesPerUser).Project(x => new LevelAnnouncementRecentSelection(x.SelectedGroupId, x.SelectedMessageId)).ToListAsync(cancellationToken)).ToArray();
            var recentGuildEvents = announcementSettings.AvoidRecentMessagesPerGuild == 0
                ? Array.Empty<LevelAnnouncementRecentSelection>()
                : (await database.LevelTransitionEvents.Find(x => x.GuildId == claimed.GuildId && x.Scope == claimed.Scope && x.Status == LevelTransitionStatus.Delivered && (claimed.Scope != LevelProgressScope.Season || x.SeasonId == claimed.SeasonId)).SortByDescending(x => x.CompletedAtUtc).Limit(announcementSettings.AvoidRecentMessagesPerGuild).Project(x => new LevelAnnouncementRecentSelection(x.SelectedGroupId, x.SelectedMessageId)).ToListAsync(cancellationToken)).ToArray();
            var recentEvents = recentUserEvents.Concat(recentGuildEvents).ToArray();
            var recentGroups = recentEvents.Where(x => x.GroupId != null).Select(x => x.GroupId!).Distinct(StringComparer.Ordinal).ToArray();
            var recentMessages = recentEvents.Where(x => x.MessageId != null).Select(x => x.MessageId!).Distinct(StringComparer.Ordinal).ToArray();
            var selection = selector.Select(announcementSettings, kind, context, recentGroups, recentMessages);
            if (selection == null && kind == LevelAnnouncementKind.Reward) selection = selector.Select(announcementSettings, LevelAnnouncementKind.LevelUp, context, [], []);
            var contentTemplate = selection?.Message.Content ?? (announcementSettings.UseDefaultFallback ? LevelAnnouncementFallbacks.Content(announcementSettings.FallbackLocale, kind == LevelAnnouncementKind.Reward) : null);
            if (contentTemplate == null) { await ReportAsync(claimed, ReportNames.LevelAnnouncementNoTemplate, ReportOutcomes.Rejected, cancellationToken); await CompleteAsync(claimed, LevelTransitionStatus.CompletedWithoutAnnouncement, null, cancellationToken); return; }
            var rendered = renderer.Render(contentTemplate, context);
            var content = rendered.Content;
            if (content == null || content.Length > 2000) { await FailAsync(claimed, "templateRenderingFailed", false, cancellationToken); return; }
            var messageId = await sender.SendAsync(channel, content, claimed.UserId, announcementSettings.NotifyUser, cancellationToken);
            await database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == claimed.Id && x.LeaseOwner == claimed.LeaseOwner, Builders<LevelTransitionEvent>.Update.Set(x => x.Status, LevelTransitionStatus.Delivered).Set(x => x.DiscordMessageId, messageId).Set(x => x.DeliveryChannelId, channel.Id).Set(x => x.SelectedKind, selection?.Kind).Set(x => x.SelectedGroupId, selection?.Group.Id).Set(x => x.SelectedMessageId, selection?.Message.Id).Set(x => x.RewardRoleIds, claimed.RewardRoleIds).Set(x => x.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime).Unset(x => x.LeaseOwner).Unset(x => x.LeaseExpiresAtUtc), cancellationToken: cancellationToken);
            await ReportAsync(claimed, ReportNames.LevelAnnouncementSent, ReportOutcomes.Succeeded, cancellationToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { await FailAsync(claimed, "processingTimedOut", true, stoppingToken); }
        catch (DiscordGuildUnavailableException)
        {
            logger.LogInformation("Level transition {EventKey} deferred; guild {GuildId} is not connected", claimed.EventKey, claimed.GuildId);
            await FailAsync(claimed, "discordGuildUnavailable", true, stoppingToken);
        }
        catch (Exception exception)
        {
            var failure = ClassifyDeliveryFailure(exception);
            var attemptNumber = claimed.DeliveryAttempts + 1;
            var willRetry = failure.Retryable && attemptNumber <= 4;
            logger.Log(willRetry ? LogLevel.Information : LogLevel.Warning,
                "Level progression {Disposition} for event {EventKey} in guild {GuildId}, channel {ChannelId}; failure {FailureCode}, attempt {AttemptNumber}, Discord code {DiscordCode}, exception {ExceptionType}. Recovery: {RecoveryAction}",
                willRetry ? "will retry" : "stopped", claimed.EventKey, claimed.GuildId, deliveryChannelId, failure.Code, attemptNumber, failure.DiscordCode, exception.GetType().Name, failure.RecoveryAction);
            // Persist detailed diagnostics in the operational incident store. The application log
            // stays concise because the full exception is available on the incident occurrence.
            await FailAsync(claimed, failure.Code, failure.Retryable, stoppingToken);
            try
            {
                await errors.RecordAsync(new(exception, failure.Source, failure.Operation,
                    failure.IsConfigurationError ? OperationalSeverity.Warning : OperationalSeverity.Error,
                    GuildId: claimed.GuildId, ChannelId: deliveryChannelId, Worker: "level-progression",
                    Context: new Dictionary<string, object?> { ["eventId"] = claimed.EventKey, ["failureCode"] = failure.Code, ["discordCode"] = failure.DiscordCode, ["recoveryAction"] = failure.RecoveryAction }), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception recordingException)
            {
                logger.LogWarning("Could not persist announcement diagnostics for {EventKey}; exception type {ExceptionType}", claimed.EventKey, recordingException.GetType().Name);
            }
        }
    }
    private static DeliveryFailure ClassifyDeliveryFailure(Exception exception)
    {
        if (exception is not HttpException discordException)
            return new("levelProgressionTemporaryFailure", true, null, false, "worker", "level.progression.process", null);

        var code = (int)discordException.DiscordCode;
        return code switch
        {
            50001 => new("discordMissingAccess", false, code, true, "discord", "level.announcement", "Grant the bot View Channel and Send Messages permissions in the configured channel."),
            50013 => new("discordMissingPermissions", false, code, true, "discord", "level.announcement", "Grant the bot Send Messages permission and verify its role hierarchy."),
            10003 => new("announcementChannelUnavailable", false, code, true, "discord", "level.announcement", "Select an available text channel in the announcement settings."),
            10004 => new("discordGuildNotFound", false, code, true, "discord", "level.announcement", "Verify that the bot is still in the server and connected."),
            _ when discordException.HttpCode == HttpStatusCode.Forbidden => new("discordMissingAccess", false, code, true, "discord", "level.announcement", "Grant the bot View Channel and Send Messages permissions in the configured channel."),
            _ when discordException.HttpCode == HttpStatusCode.NotFound => new("announcementChannelUnavailable", false, code, true, "discord", "level.announcement", "Select an available text channel in the announcement settings."),
            _ when discordException.HttpCode == HttpStatusCode.BadRequest => new("discordRequestRejected", false, code, true, "discord", "level.announcement", "Review the announcement template and preview it before saving."),
            _ when discordException.HttpCode == HttpStatusCode.Unauthorized => new("discordAuthenticationFailed", false, code, true, "discord", "level.announcement", "Check the bot credentials and reconnect the bot."),
            _ => new("discordTemporaryFailure", true, code, false, "discord", "level.announcement", null)
        };
    }
    private sealed record DeliveryFailure(string Code, bool Retryable, int? DiscordCode, bool IsConfigurationError, string Source, string Operation, string? RecoveryAction);
    private Task CompleteAsync(LevelTransitionEvent e, LevelTransitionStatus status, string? error, CancellationToken ct) => database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == e.Id && x.LeaseOwner == e.LeaseOwner, Builders<LevelTransitionEvent>.Update.Set(x => x.Status, status).Set(x => x.LastErrorCode, error).Set(x => x.CompletedAtUtc, timeProvider.GetUtcNow().UtcDateTime).Unset(x => x.LeaseOwner).Unset(x => x.LeaseExpiresAtUtc), cancellationToken: ct);
    private Task FailAsync(LevelTransitionEvent e, string code, bool temporary, CancellationToken ct)
    {
        var attempts = e.DeliveryAttempts + 1; var delays = new[] { 30, 120, 600, 1800 }; var dead = !temporary || attempts > delays.Length;
        return database.LevelTransitionEvents.UpdateOneAsync(x => x.Id == e.Id && x.LeaseOwner == e.LeaseOwner, Builders<LevelTransitionEvent>.Update.Set(x => x.DeliveryAttempts, attempts).Set(x => x.LastErrorCode, code).Set(x => x.Status, dead ? LevelTransitionStatus.DeadLetter : LevelTransitionStatus.RetryScheduled).Set(x => x.NextAttemptAtUtc, dead ? null : timeProvider.GetUtcNow().UtcDateTime.AddSeconds(delays[attempts - 1])).Set(x => x.CompletedAtUtc, dead ? timeProvider.GetUtcNow().UtcDateTime : null).Unset(x => x.LeaseOwner).Unset(x => x.LeaseExpiresAtUtc), cancellationToken: ct);
    }
    private Task ReportAsync(LevelTransitionEvent e, string name, string outcome, CancellationToken ct) => reports.WriteAsync(new(e.GuildId, ReportCategories.Activity, name, outcome, e.Source, e.UserId, Metadata: new Dictionary<string, object?> { ["userId"] = e.UserId, ["level"] = e.NewLevel, ["source"] = e.Source }, SubjectId: e.UserId), ct);

    internal static System.Linq.Expressions.Expression<Func<LevelTransitionEvent, bool>> EligibleWork(DateTime now) => x =>
        ((x.Status == LevelTransitionStatus.Pending || x.Status == LevelTransitionStatus.RetryScheduled) &&
         (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now) &&
         (x.LeaseExpiresAtUtc == null || x.LeaseExpiresAtUtc <= now)) ||
        (x.Status == LevelTransitionStatus.Processing && (x.LeaseExpiresAtUtc == null || x.LeaseExpiresAtUtc <= now));

    internal static LevelTransitionCause ResolveCause(LevelTransitionEvent transition, XpLedgerEntry? ledger) => new(
        transition.Source,
        string.IsNullOrWhiteSpace(transition.CauseKey) ? transition.LedgerGrantKey ?? transition.EventKey : transition.CauseKey,
        transition.GainedXp != 0 ? transition.GainedXp : transition.NewTotalXp - transition.PreviousTotalXp,
        transition.SourceChannelId ?? ledger?.ChannelId,
        transition.LedgerGrantKey,
        transition.SuppressAnnouncement || ledger != null && XpLedgerSemantics.GetEffectiveKind(ledger) == XpLedgerEntryKind.SystemMigration);
}
