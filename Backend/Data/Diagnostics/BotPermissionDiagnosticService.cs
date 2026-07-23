using Discord;
using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Diagnostics;

public interface IBotPermissionDiagnosticService
{
    Task<PermissionDiagnosticReport> ScanAsync(ulong guildId, PermissionDiagnosticScanRequest request, CancellationToken cancellationToken = default);
    PermissionDiagnosticReport? GetLatest(ulong guildId);
    Task<ChannelDiagnosticResult?> GetChannelAsync(ulong guildId, ulong channelId, bool includeTrace, CancellationToken cancellationToken = default);
    void Invalidate(ulong guildId);
}

public sealed class BotPermissionDiagnosticService(DiscordShardedClient discord, GatewayIntentState gatewayIntents, RankoonDbContext database, IPermissionRequirementCatalog catalog, IDiagnosticReportCache cache, ILogger<BotPermissionDiagnosticService> logger) : IBotPermissionDiagnosticService
{
    public Task<PermissionDiagnosticReport> ScanAsync(ulong guildId, PermissionDiagnosticScanRequest request, CancellationToken cancellationToken = default) =>
        cache.GetOrCreateAsync(guildId, request.Scope, token => BuildAsync(guildId, request, token), cancellationToken);
    public PermissionDiagnosticReport? GetLatest(ulong guildId) => cache.GetLatest(guildId);
    public void Invalidate(ulong guildId) => cache.Invalidate(guildId);

    public async Task<ChannelDiagnosticResult?> GetChannelAsync(ulong guildId, ulong channelId, bool includeTrace, CancellationToken cancellationToken = default)
    {
        var guild = discord.GetGuild(guildId);
        var channel = guild?.GetChannel(channelId);
        if (guild?.CurrentUser == null || channel == null) return null;
        var report = await BuildAsync(guildId, new(PermissionDiagnosticScope.AllChannels, includeTrace), cancellationToken);
        return report.ChannelChecks.FirstOrDefault(check => check.ChannelId == channelId.ToString());
    }

    private async Task<PermissionDiagnosticReport> BuildAsync(ulong guildId, PermissionDiagnosticScanRequest request, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var guild = discord.GetGuild(guildId);
        if (guild?.CurrentUser == null)
            return Unavailable(guildId, guild == null ? "guild-cache" : "bot-member");

        var bot = guild.CurrentUser;
        var settings = await database.GuildXpSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken) ?? new GuildXpSettings { GuildId = guildId, Enabled = false };
        var announcements = await database.GuildLevelUpAnnouncementSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        var panels = await database.SelfRolePanels.Find(x => x.GuildId == guildId && x.Enabled).ToListAsync(cancellationToken);
        var hubs = await database.VcHubs.Find(x => x.GuildId == guildId && x.Enabled).ToListAsync(cancellationToken);
        var global = GlobalChecks(bot);
        var featureChecks = new List<FeatureDiagnosticResult>();
        var roles = new List<RoleHierarchyDiagnosticResult>();
        var channelUses = new Dictionary<ulong, List<(string Feature, PermissionRequirementDefinition Requirement)>>();

        void AddChannels(string feature, PermissionRequirementDefinition requirement, IEnumerable<SocketGuildChannel> channels)
        {
            foreach (var channel in channels)
            {
                if (!channelUses.TryGetValue(channel.Id, out var uses)) channelUses[channel.Id] = uses = [];
                uses.Add((feature, requirement));
            }
        }
        var definitions = catalog.Definitions;
        bool IsExcluded(SocketGuildChannel channel) => settings.ExcludedChannelIds.Contains(channel.Id) || (CategoryId(channel) is ulong categoryId && settings.ExcludedCategoryIds.Contains(categoryId));
        var guildChannels = guild.Channels.OfType<SocketGuildChannel>().ToList();
        var text = guildChannels.Where(channel => Kind(channel) == "Text" && !IsExcluded(channel)).ToList();
        var voice = guildChannels.Where(channel => (Kind(channel) is "Voice" or "Stage") && !IsExcluded(channel) && (!settings.Voice.ExcludeAfkChannel || channel.Id != guild.AFKChannel?.Id)).ToList();

        AddFeature(DiagnosticFeatureKeys.TextXp, settings.Enabled && settings.Message.Enabled, text);
        AddFeature(DiagnosticFeatureKeys.ReactionXp, settings.Enabled && settings.Reaction.Enabled, text);
        AddFeature(DiagnosticFeatureKeys.ThreadXp, settings.Enabled && settings.Thread.Enabled, guildChannels.Where(channel => Kind(channel) is "Text" or "Thread").Where(channel => !IsExcluded(channel)));
        AddFeature(DiagnosticFeatureKeys.VoiceXp, settings.Enabled && settings.Voice.Enabled, voice);
        AddFeature(DiagnosticFeatureKeys.LevelUpAnnouncements, announcements?.Enabled == true && announcements.ChannelId.HasValue,
            announcements?.ChannelId is ulong channelId && guild.GetChannel(channelId) is SocketGuildChannel output ? [output] : []);
        AddFeature(DiagnosticFeatureKeys.SelfRoles, panels.Count > 0, panels.Select(panel => guild.GetChannel(panel.ChannelId)).OfType<SocketGuildChannel>());
        AddFeature(DiagnosticFeatureKeys.VoiceHubs, hubs.Count > 0, hubs.Select(hub => guild.GetChannel(hub.JoinChannelId)).OfType<SocketGuildChannel>());

        foreach (var role in settings.LevelRoles)
            roles.Add(RoleCheck(DiagnosticFeatureKeys.LevelRoles, role.RoleId, guild, bot));
        foreach (var role in panels.SelectMany(panel => panel.Mappings).Select(mapping => mapping.RoleId).Distinct())
            roles.Add(RoleCheck(DiagnosticFeatureKeys.SelfRoles, role, guild, bot));

        var channelResults = new List<ChannelDiagnosticResult>();
        var channelsForReport = request.Scope == PermissionDiagnosticScope.ConfiguredFeatures
            ? guildChannels.Where(channel => channelUses.ContainsKey(channel.Id)) : guildChannels;
        foreach (var channel in channelsForReport)
        {
            var uses = channelUses.GetValueOrDefault(channel.Id) ?? [];
            var checks = uses.Select(use => ChannelCheck(bot, channel, use.Feature, use.Requirement, request.IncludePermissionTrace)).ToList();
            if (request.Scope != PermissionDiagnosticScope.ConfiguredFeatures && checks.Count == 0)
                checks.Add(NotRequired(channel));
            var category = CategoryId(channel) is ulong categoryId ? guild.GetCategoryChannel(categoryId) : null;
            channelResults.Add(new(channel.Id.ToString(), channel.Name, Kind(channel), category?.Id.ToString(), category?.Name, uses.Select(x => x.Feature).Distinct().ToList(), checks));
        }

        foreach (var definition in definitions)
        {
            var enabled = FeatureEnabled(definition.FeatureKey);
            var checks = channelResults.SelectMany(x => x.Checks).Where(x => x.FeatureKey == definition.FeatureKey).Concat(roles.Where(x => x.FeatureKey == definition.FeatureKey).Select(RoleAsCheck)).ToList();
            if (enabled && checks.Count == 0 && definition.FeatureKey is DiagnosticFeatureKeys.LevelUpAnnouncements or DiagnosticFeatureKeys.VoiceHubs)
                checks.Add(MissingConfiguration(definition.FeatureKey));
            featureChecks.Add(new(definition.FeatureKey, Aggregate(checks), enabled, checks));
        }

        var allChecks = global.Concat(featureChecks.SelectMany(x => x.Checks)).ToList();
        var statistics = new DiagnosticStatistics(featureChecks.Count(x => x.Enabled), channelResults.Count, allChecks.Count(x => x.Status == DiagnosticStatus.Critical) + roles.Count(x => x.Status == DiagnosticStatus.Critical), allChecks.Count(x => x.Status == DiagnosticStatus.Warning), allChecks.Count(x => x.Status == DiagnosticStatus.Unknown));
        var overall = Aggregate(allChecks.Concat(roles.Select(RoleAsCheck)));
        var report = new PermissionDiagnosticReport(Guid.NewGuid().ToString("N"), guildId.ToString(), DateTimeOffset.UtcNow, overall, DiagnosticCompleteness.Complete, global, featureChecks, roles, channelResults, statistics);
        logger.LogInformation("Permission diagnostic completed for {GuildId}: scope {Scope}, channels {Channels}, critical {Critical}, warnings {Warnings}, duration {DurationMs}ms", guildId, request.Scope, statistics.ChannelsChecked, statistics.Critical, statistics.Warnings, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
        return report;

        void AddFeature(string feature, bool enabled, IEnumerable<SocketGuildChannel> channels)
        {
            if (!enabled) return;
            var requirement = definitions.First(x => x.FeatureKey == feature);
            AddChannels(feature, requirement, channels);
        }
        bool FeatureEnabled(string feature) => feature switch
        {
            DiagnosticFeatureKeys.TextXp => settings.Enabled && settings.Message.Enabled,
            DiagnosticFeatureKeys.ReactionXp => settings.Enabled && settings.Reaction.Enabled,
            DiagnosticFeatureKeys.ThreadXp => settings.Enabled && settings.Thread.Enabled,
            DiagnosticFeatureKeys.VoiceXp => settings.Enabled && settings.Voice.Enabled,
            DiagnosticFeatureKeys.LevelUpAnnouncements => announcements?.Enabled == true,
            DiagnosticFeatureKeys.LevelRoles => settings.LevelRoles.Count > 0,
            DiagnosticFeatureKeys.SelfRoles => panels.Count > 0,
            DiagnosticFeatureKeys.VoiceHubs => hubs.Count > 0,
            _ => false
        };
    }

    private static PermissionDiagnosticReport Unavailable(ulong guildId, string reason) => new(Guid.NewGuid().ToString("N"), guildId.ToString(), DateTimeOffset.UtcNow, DiagnosticStatus.Unknown, DiagnosticCompleteness.Unavailable,
        [new("discord." + reason, DiagnosticStatus.Unknown, "global", null, "Guild", "Discord", [], [], [], [], "diagnostics.global.unavailable", "diagnostics.global.unavailable", "diagnostics.remediation.reconnect", new Dictionary<string, string>(), "The bot cache is not available.")], [], [], [], new(0, 0, 0, 0, 1));

    private List<PermissionCheckResult> GlobalChecks(SocketGuildUser bot)
    {
        var checks = new List<PermissionCheckResult>();
        AddIntent("GuildMessages", GatewayIntents.GuildMessages, DiagnosticFeatureKeys.TextXp);
        AddIntent("MessageContent", GatewayIntents.MessageContent, DiagnosticFeatureKeys.TextXp);
        AddIntent("GuildVoiceStates", GatewayIntents.GuildVoiceStates, DiagnosticFeatureKeys.VoiceXp);
        if (bot.GuildPermissions.Administrator)
            checks.Add(new("global.administrator", DiagnosticStatus.Warning, "global", null, "Guild", "Guild", ["Administrator"], ["Administrator"], [], [], "diagnostics.global.administrator", "diagnostics.global.administrator", "diagnostics.remediation.removeAdministrator", new Dictionary<string, string>(), "Channel overrides are bypassed, but the bot has more access than required."));
        return checks;
        void AddIntent(string name, GatewayIntents required, string feature) => checks.Add(new("intent." + name, gatewayIntents.Value.HasFlag(required) ? DiagnosticStatus.Healthy : DiagnosticStatus.Critical, feature, null, "GatewayIntent", name, [name], gatewayIntents.Value.HasFlag(required) ? [name] : [], gatewayIntents.Value.HasFlag(required) ? [] : [name], [], "diagnostics.global.intent", "diagnostics.global.intent", "diagnostics.remediation.enableIntent", new Dictionary<string, string>(), "The gateway cannot receive the events needed by this feature."));
    }

    private static PermissionCheckResult ChannelCheck(SocketGuildUser bot, SocketGuildChannel channel, string feature, PermissionRequirementDefinition requirement, bool includeTrace)
    {
        var permissions = bot.GetPermissions(channel);
        var missing = requirement.RequiredPermissions.Where(name => !Has(permissions, name)).ToList();
        var traces = includeTrace ? requirement.RequiredPermissions.Select(name => new PermissionTrace(name, Has(permissions, name), [new("Discord.Net", Has(permissions, name), "diagnostics.trace.discordNet")], "Discord.Net effective permission")).ToList() : [];
        return new(feature + "." + requirement.RequirementKey + "." + channel.Id, missing.Count == 0 ? DiagnosticStatus.Healthy : DiagnosticStatus.Critical, feature, channel.Id.ToString(), "Channel", channel.Name, requirement.RequiredPermissions, requirement.RequiredPermissions.Except(missing).ToList(), missing, traces, requirement.TitleKey, requirement.DescriptionKey, requirement.RemediationKey, new Dictionary<string, string> { ["channelName"] = channel.Name }, missing.Count == 0 ? "The configured resource is usable." : "This feature cannot use the configured channel until the missing permission is granted.");
    }
    private static PermissionCheckResult NotRequired(SocketGuildChannel channel) => new("channel.not-required." + channel.Id, DiagnosticStatus.NotApplicable, "", channel.Id.ToString(), "Channel", channel.Name, [], [], [], [], "diagnostics.channel.notRequired", "diagnostics.channel.notRequired", "", new Dictionary<string, string>(), "No enabled Rankoon feature uses this channel.");
    private static PermissionCheckResult MissingConfiguration(string feature) => new(feature + ".configuration", DiagnosticStatus.Critical, feature, null, "Configuration", feature, [], [], [], [], "diagnostics.configuration.missing", "diagnostics.configuration.missing", "diagnostics.remediation.configure", new Dictionary<string, string>(), "The feature is enabled but has no usable configured resource.");
    private static RoleHierarchyDiagnosticResult RoleCheck(string feature, ulong id, SocketGuild guild, SocketGuildUser bot)
    {
        var role = guild.GetRole(id);
        if (role == null) return new(id.ToString(), "Unknown role", feature, DiagnosticStatus.Critical, "diagnostics.role.missing", "diagnostics.remediation.removeDeletedRole");
        if (!bot.GuildPermissions.ManageRoles) return new(id.ToString(), role.Name, feature, DiagnosticStatus.Critical, "diagnostics.role.manageRoles", "diagnostics.remediation.grantManageRoles");
        if (role.IsEveryone || role.IsManaged) return new(id.ToString(), role.Name, feature, DiagnosticStatus.Critical, "diagnostics.role.unmanageable", "diagnostics.remediation.selectManageableRole");
        var highest = bot.Roles.OrderByDescending(x => x.Position).FirstOrDefault();
        return highest == null || highest.Position <= role.Position
            ? new(id.ToString(), role.Name, feature, DiagnosticStatus.Critical, "diagnostics.role.hierarchy", "diagnostics.remediation.moveBotRole")
            : new(id.ToString(), role.Name, feature, DiagnosticStatus.Healthy, "diagnostics.role.assignable", "");
    }
    private static PermissionCheckResult RoleAsCheck(RoleHierarchyDiagnosticResult role) => new("role." + role.RoleId, role.Status, role.FeatureKey, role.RoleId, "Role", role.RoleName, [], [], [], [], role.ReasonKey, role.ReasonKey, role.RemediationKey, new Dictionary<string, string> { ["roleName"] = role.RoleName }, "Role assignment requires Manage Roles and a higher bot role.");
    private static DiagnosticStatus Aggregate(IEnumerable<PermissionCheckResult> checks)
    {
        var statuses = checks.Select(x => x.Status).ToList();
        if (statuses.Contains(DiagnosticStatus.Critical)) return DiagnosticStatus.Critical;
        if (statuses.Contains(DiagnosticStatus.Unknown)) return DiagnosticStatus.Unknown;
        if (statuses.Contains(DiagnosticStatus.Warning)) return DiagnosticStatus.Warning;
        return statuses.Count == 0 ? DiagnosticStatus.NotApplicable : DiagnosticStatus.Healthy;
    }
    private static bool Has(ChannelPermissions permissions, string name) => name switch { "ViewChannel" => permissions.ViewChannel, "ReadMessageHistory" => permissions.ReadMessageHistory, "SendMessages" => permissions.SendMessages, "EmbedLinks" => permissions.EmbedLinks, "AddReactions" => permissions.AddReactions, "ManageMessages" => permissions.ManageMessages, "ManageChannels" => permissions.ManageChannel, "MoveMembers" => permissions.MoveMembers, _ => false };
    private static ulong? CategoryId(SocketGuildChannel channel) => channel is SocketVoiceChannel voice ? voice.CategoryId : channel is SocketTextChannel text ? text.CategoryId : null;
    private static string Kind(SocketGuildChannel channel) => channel switch { SocketCategoryChannel => "Category", SocketStageChannel => "Stage", SocketVoiceChannel => "Voice", SocketThreadChannel => "Thread", SocketTextChannel => "Text", _ => channel.GetType().Name };
}
