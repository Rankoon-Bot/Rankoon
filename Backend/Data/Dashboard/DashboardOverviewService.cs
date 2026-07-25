using System.Security.Claims;
using Discord.WebSocket;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Driver;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;

namespace Rankoon.Data.Dashboard;

public interface IDashboardOverviewService
{
    Task<DashboardOverviewResponse> GetAsync(ClaimsPrincipal user, ulong guildId, DashboardPeriod period, CancellationToken cancellationToken);
}

public sealed class DashboardOverviewService(RankoonDbContext database, IGuildDiscordContextResolver discord, IGuildAuthorizationService authorization, ICustomBotIdentityAccessPolicy botIdentityAccess, VoiceXpWatchdog watchdog, DiscordShardedClient discordClient, IMemoryCache cache, TimeProvider clock) : IDashboardOverviewService
{
    // Reporting and diagnostics remain operational views, not bot-function cards.
    private static readonly string[] ProductModules = ["xp", "seasons", "level-up-announcements", "voice-hubs", "self-roles"];

    public async Task<DashboardOverviewResponse> GetAsync(ClaimsPrincipal user, ulong guildId, DashboardPeriod period, CancellationToken cancellationToken)
    {
        var owner = await authorization.IsOwnerAsync(user, guildId, cancellationToken);
        var granted = owner ? ProductModules : (await authorization.GetAccessibleModuleIdsAsync(user, guildId, cancellationToken)).ToArray();
        var visible = owner ? ProductModules.ToList() : ProductModules.Where(granted.Contains).ToList();
        if (owner)
        {
            var access = await botIdentityAccess.EvaluateAsync(guildId, cancellationToken);
            // Mirror sidebar visibility: capacity alone does not expose this feature.
            if (access.IsEligible && (access.CanActivate || access.HasReservation || access.HasConfiguredIdentity)) visible.Add("bot-identity");
        }
        var cacheKey = $"dashboard:{guildId}:{period}:{owner}:{string.Join(',', visible.Order())}";
        if (cache.TryGetValue(cacheKey, out DashboardOverviewResponse? cached) && cached != null) return cached;
        var now = clock.GetUtcNow();
        var days = period == DashboardPeriod.SevenDays ? 7 : 30;
        var start = now.UtcDateTime.Date.AddDays(-(days - 1));
        var end = now.UtcDateTime;
        var context = await discord.ResolveAsync(guildId, cancellationToken) ?? throw new KeyNotFoundException("Guild unavailable.");
        var guild = context.Guild;
        var settingsTask = database.GuildXpSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        var hubsTask = database.VcHubs.Find(x => x.GuildId == guildId).ToListAsync(cancellationToken);
        var panelsTask = database.SelfRolePanels.Find(x => x.GuildId == guildId).ToListAsync(cancellationToken);
        var reportsTask = database.ReportEvents.Find(x => x.GuildId == guildId && x.OccurredAt >= start).SortByDescending(x => x.OccurredAt).Limit(100).ToListAsync(cancellationToken);
        var ledgerTask = database.XpLedger.Find(x => x.GuildId == guildId && x.OccurredAtUtc >= start && x.OccurredAtUtc <= end && x.ProjectionStatus == SeasonProjectionStatus.Applied && !x.CooldownDenied && !x.IsProjectionControl).ToListAsync(cancellationToken);
        await Task.WhenAll(settingsTask, hubsTask, panelsTask, reportsTask, ledgerTask);
        var settings = settingsTask.Result ?? new GuildXpSettings { GuildId = guildId, Enabled = false };
        var ledgers = ledgerTask.Result.Where(IsQualifiedGrant).ToArray();
        var activity = BuildActivity(ledgers, start, days, reportsTask.Result);
        var modules = BuildModules(visible, settings, hubsTask.Result, panelsTask.Result, watchdog.GetStatus(guildId), activity, reportsTask.Result, guild);
        var health = BuildHealth(modules);
        var botUser = discordClient.CurrentUser;
        var bot = new DashboardBotSummary(DashboardBotIdentityMode.Rankoon, botUser?.Username, botUser?.GetAvatarUrl(), botUser != null, botUser != null ? DashboardOperationalStatus.Healthy : DashboardOperationalStatus.Unknown, null, botUser == null ? "dashboard.status.botUnavailable" : "dashboard.status.healthy");
        var botCount = guild.Users.LongCount(x => x.IsBot);
        var response = new DashboardOverviewResponse(now, period, new DateTimeOffset(start, TimeSpan.Zero), now, new(guildId.ToString(), guild.Name, guild.IconUrl, Math.Max(0, guild.MemberCount - botCount), botCount, guild.VoiceChannels.Sum(x => x.ConnectedUsers.Count(u => !u.IsBot))), bot, health, activity, modules, BuildEvents(reportsTask.Result, visible, guild));
        cache.Set(cacheKey, response, TimeSpan.FromSeconds(20));
        return response;
    }

    private static bool IsQualifiedGrant(XpLedgerEntry x) => x.Amount > 0 && XpLedgerSemantics.GetEffectiveKind(x) == XpLedgerEntryKind.AutomaticGrant;
    private static DashboardActivitySummary BuildActivity(IReadOnlyList<XpLedgerEntry> rows, DateTime start, int days, IReadOnlyList<ReportEvent> reports)
    {
        var sources = rows.GroupBy(x => Source(x.Source)).Select(g => new DashboardActivitySource(g.Key, g.LongCount(), g.Sum(x => x.Amount), rows.Count == 0 ? 0 : Math.Round(g.Count() * 100d / rows.Count, 1))).OrderByDescending(x => x.XpAwarded).ToArray();
        var trend = Enumerable.Range(0, days).Select(i => {
            var date = start.AddDays(i); var day = rows.Where(x => x.OccurredAtUtc.Date == date.Date).ToArray();
            return new DashboardTrendPoint(new DateTimeOffset(date, TimeSpan.Zero), day.Sum(x => x.Amount), day.Select(x => x.UserId).Distinct().LongCount(), Voice(day));
        }).ToArray();
        var temporary = reports.LongCount(x => x.Name == ReportNames.VoiceChannelCreated && x.OccurredAt >= start);
        return new(rows.Select(x => x.UserId).Distinct().LongCount(), rows.Sum(x => x.Amount), Voice(rows), rows.LongCount(), temporary, null, sources, trend);
    }
    private static long Voice(IEnumerable<XpLedgerEntry> rows) => rows.Where(x => x.Source == "voice" && x.PeriodStartsAtUtc != null && x.PeriodEndsAtUtc != null && x.PeriodEndsAtUtc > x.PeriodStartsAtUtc).Sum(x => (long)(x.PeriodEndsAtUtc!.Value - x.PeriodStartsAtUtc!.Value).TotalSeconds);
    private static string Source(string source) => source switch { "thread_create" or "thread_message" => "thread", "event_interest" => "event", "message" or "reaction" or "voice" => source, _ => "other" };
    private static IReadOnlyList<DashboardModuleSummary> BuildModules(IEnumerable<string> visible, GuildXpSettings xp, IReadOnlyList<VcHub> hubs, IReadOnlyList<SelfRolePanel> panels, VoiceWatchdogStatus watchdog, DashboardActivitySummary activity, IReadOnlyList<ReportEvent> reports, SocketGuild guild) => visible.Select(id => id switch
    {
        "xp" => Xp(xp, watchdog, activity),
        "seasons" => Module(id, false, DashboardOperationalStatus.SetupRequired, "dashboard.status.seasonsSetup"),
        "level-up-announcements" => Module(id, xp.LevelUpChannelId != null, xp.LevelUpChannelId == null ? DashboardOperationalStatus.Disabled : DashboardOperationalStatus.SetupRequired, xp.LevelUpChannelId == null ? "dashboard.status.disabled" : "dashboard.status.announcementSetup"),
        "leaderboard" => Module(id, true, DashboardOperationalStatus.Healthy, "dashboard.status.healthy"),
        "xp-audit" => Module(id, true, DashboardOperationalStatus.Healthy, "dashboard.status.healthy"),
        "voice-hubs" => Hubs(hubs, activity),
        "self-roles" => Panels(panels),
        "reporting" => Reporting(reports),
        "diagnostics" => Module(id, true, DashboardOperationalStatus.SetupRequired, "dashboard.status.diagnosticsSetup"),
        "dashboard-access" => Module(id, true, DashboardOperationalStatus.Healthy, "dashboard.status.healthy"),
        "bot-identity" => Module(id, true, DashboardOperationalStatus.Healthy, "dashboard.status.healthy"),
        _ => Module(id, false, DashboardOperationalStatus.Unknown, "dashboard.status.unknown")
    }).ToArray();
    private static DashboardModuleSummary Xp(GuildXpSettings x, VoiceWatchdogStatus wd, DashboardActivitySummary a)
    {
        var sources = new[] { x.Message.Enabled, x.Voice.Enabled, x.Reaction.Enabled, x.Thread.Enabled, x.EventInterest.Enabled }.Count(v => v);
        var status = !x.Enabled ? DashboardOperationalStatus.Disabled : sources == 0 ? DashboardOperationalStatus.SetupRequired : x.Voice.Enabled && wd.State == VoiceWatchdogState.Faulted ? DashboardOperationalStatus.Critical : x.Voice.Enabled && wd.State is VoiceWatchdogState.Degraded or VoiceWatchdogState.Stale ? DashboardOperationalStatus.Warning : DashboardOperationalStatus.Healthy;
        return new("xp", x.Enabled, status, status == DashboardOperationalStatus.Healthy ? "dashboard.status.healthy" : "dashboard.status.xp", new Dictionary<string, string>(), [new("sources", $"{sources}/5"), new("xpAwarded", a.XpAwarded.ToString("0")), new("levelRoles", x.LevelRoles.Count.ToString())], null);
    }
    private static DashboardModuleSummary Hubs(IReadOnlyList<VcHub> hubs, DashboardActivitySummary a) => Module("voice-hubs", hubs.Any(x => x.Enabled), hubs.Count == 0 ? DashboardOperationalStatus.SetupRequired : hubs.All(x => !x.Enabled) ? DashboardOperationalStatus.Disabled : DashboardOperationalStatus.Healthy, hubs.Count == 0 ? "dashboard.status.hubsSetup" : "dashboard.status.healthy", [new("hubs", hubs.Count.ToString()), new("created", a.TemporaryChannelsCreated.ToString())]);
    private static DashboardModuleSummary Panels(IReadOnlyList<SelfRolePanel> panels) => Module("self-roles", panels.Any(x => x.Enabled), panels.Count == 0 ? DashboardOperationalStatus.SetupRequired : panels.Any(x => x.Enabled && x.State == SelfRolePanelState.Degraded) ? DashboardOperationalStatus.Critical : panels.All(x => !x.Enabled) ? DashboardOperationalStatus.Disabled : DashboardOperationalStatus.Healthy, panels.Count == 0 ? "dashboard.status.selfRolesSetup" : "dashboard.status.healthy", [new("panels", panels.Count.ToString()), new("mappings", panels.Sum(x => x.Mappings.Count).ToString())]);
    private static DashboardModuleSummary Reporting(IReadOnlyList<ReportEvent> reports) => Module("reporting", true, DashboardOperationalStatus.Healthy, "dashboard.status.healthy", [new("legacyEvents", reports.Count.ToString())]);
    private static DashboardModuleSummary Module(string id, bool enabled, DashboardOperationalStatus status, string reason, IReadOnlyList<DashboardModuleMetric>? metrics = null) => new(id, enabled, status, reason, new Dictionary<string, string>(), metrics ?? [], null);
    private static DashboardHealthSummary BuildHealth(IReadOnlyList<DashboardModuleSummary> modules) { var attention = modules.Where(x => x.Status is DashboardOperationalStatus.Critical or DashboardOperationalStatus.Warning or DashboardOperationalStatus.SetupRequired).OrderByDescending(x => x.Status == DashboardOperationalStatus.Critical).ThenByDescending(x => x.Status == DashboardOperationalStatus.Warning).Select(x => new DashboardAttentionItem($"{x.ModuleId}:{x.Status}", x.Status == DashboardOperationalStatus.Critical ? DashboardAttentionSeverity.Critical : x.Status == DashboardOperationalStatus.Warning ? DashboardAttentionSeverity.Warning : DashboardAttentionSeverity.Info, x.ModuleId, $"dashboard.attention.{x.ModuleId}.title", x.StatusReasonKey, new Dictionary<string, string>(), new("openSettings", x.ModuleId, null))).ToArray(); var overall = modules.Any(x => x.Status == DashboardOperationalStatus.Critical) ? DashboardOperationalStatus.Critical : modules.Any(x => x.Status == DashboardOperationalStatus.Warning) ? DashboardOperationalStatus.Warning : modules.Any(x => x.Status == DashboardOperationalStatus.SetupRequired) ? DashboardOperationalStatus.SetupRequired : DashboardOperationalStatus.Healthy; return new(overall, modules.Count(x => x.Status == DashboardOperationalStatus.Healthy), modules.Count(x => x.Status == DashboardOperationalStatus.Disabled), modules.Count(x => x.Status == DashboardOperationalStatus.SetupRequired), modules.Count(x => x.Status == DashboardOperationalStatus.Warning), modules.Count(x => x.Status == DashboardOperationalStatus.Critical), modules.Count(x => x.Status == DashboardOperationalStatus.Unknown), attention); }
    private static IReadOnlyList<DashboardRecentEvent> BuildEvents(IEnumerable<ReportEvent> reports, IEnumerable<string> modules, SocketGuild guild) => reports.Where(x => x.Name != ReportNames.XpGranted).Where(x => ModuleFor(x.Name) is { } m && modules.Contains(m)).Take(6).Select(x => new DashboardRecentEvent(x.Id ?? string.Empty, x.Name, x.Outcome, x.Severity, ModuleFor(x.Name), x.ActorId?.ToString(), null, x.SubjectId?.ToString(), null, x.ChannelId?.ToString(), x.ChannelId is { } channel && guild.GetChannel(channel) is { } c ? c.Name : null, x.Metadata?.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty) ?? new Dictionary<string, string>(), new DateTimeOffset(x.OccurredAt, TimeSpan.Zero))).ToArray();
    private static string? ModuleFor(string name) => name.StartsWith("xp.") ? "xp" : name.StartsWith("voice.") ? "voice-hubs" : name.StartsWith("leaderboard.") ? "leaderboard" : name.StartsWith("permissions.") ? "dashboard-access" : name.StartsWith("level.") ? "level-up-announcements" : null;
}
