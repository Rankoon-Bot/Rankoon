using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Discord;

public sealed class CustomBotIdentityOptions
{
    public const string SectionName = "CustomBotIdentity";
    public bool Enabled { get; set; }
    public int? MaxActiveGuilds { get; set; }
    public HashSet<ulong> AllowedGuildIds { get; set; } = [];
    public string FingerprintKey { get; set; } = string.Empty;
    public int StartupParallelism { get; set; } = 2;
}

public enum CustomBotAccessReason { Available, AlreadyReserved, FeatureDisabled, GuildNotAllowed, CapacityReached }
public sealed record CustomBotAccessDecision(bool IsEligible, bool CanActivate, bool HasReservation, bool HasConfiguredIdentity, int ActiveGuilds, int? MaximumActiveGuilds, CustomBotAccessReason Reason);
public interface ICustomBotIdentityAccessPolicy { Task<CustomBotAccessDecision> EvaluateAsync(ulong guildId, CancellationToken cancellationToken = default); }

public sealed class CustomBotIdentityAccessPolicy(RankoonDbContext database, IOptions<CustomBotIdentityOptions> options) : ICustomBotIdentityAccessPolicy
{
    public async Task<CustomBotAccessDecision> EvaluateAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var configuration = options.Value;
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        var reservation = await database.CustomBotCapacityReservations.Find(x => x.GuildId == guildId).AnyAsync(cancellationToken);
        var count = (int)await database.CustomBotCapacityReservations.CountDocumentsAsync(FilterDefinition<CustomBotCapacityReservation>.Empty, cancellationToken: cancellationToken);
        return Decide(configuration, guildId, identity?.EncryptedBotToken != null, reservation, count);
    }

    public static CustomBotAccessDecision Decide(CustomBotIdentityOptions configuration, ulong guildId, bool hasConfiguredIdentity, bool reservation, int count)
    {
        if (!configuration.Enabled) return new(false, false, reservation, hasConfiguredIdentity, count, configuration.MaxActiveGuilds, CustomBotAccessReason.FeatureDisabled);
        if (configuration.AllowedGuildIds.Count > 0 && !configuration.AllowedGuildIds.Contains(guildId)) return new(false, false, reservation, hasConfiguredIdentity, count, configuration.MaxActiveGuilds, CustomBotAccessReason.GuildNotAllowed);
        if (reservation) return new(true, true, true, hasConfiguredIdentity, count, configuration.MaxActiveGuilds, CustomBotAccessReason.AlreadyReserved);
        if (configuration.MaxActiveGuilds is int maximum && count >= maximum) return new(true, false, false, hasConfiguredIdentity, count, maximum, CustomBotAccessReason.CapacityReached);
        return new(true, true, false, hasConfiguredIdentity, count, configuration.MaxActiveGuilds, CustomBotAccessReason.Available);
    }
}

public interface ICustomBotTokenProtector { string Protect(string token); string Unprotect(string encryptedToken); string CreateFingerprint(string token); }
public sealed class CustomBotTokenProtector(IDataProtectionProvider provider, IOptions<CustomBotIdentityOptions> options) : ICustomBotTokenProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Rankoon.CustomBotIdentity.Token.v1");
    public string Protect(string token) => protector.Protect(token);
    public string Unprotect(string encryptedToken) => protector.Unprotect(encryptedToken);
    public string CreateFingerprint(string token)
    {
        if (string.IsNullOrWhiteSpace(options.Value.FingerprintKey)) throw new InvalidOperationException("CustomBotIdentity:FingerprintKey must be configured.");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.FingerprintKey), Encoding.UTF8.GetBytes(token)));
    }
}

public sealed record CustomBotValidationResult(bool IsValid, string? ErrorCode, ulong? ApplicationId, ulong? BotUserId, string? BotUsername, string? BotGlobalName, string? BotAvatarHash);
public sealed record CustomBotGuildValidationResult(bool IsValid, string? ErrorCode, IReadOnlyDictionary<string, bool> Checks);
public interface ICustomBotIdentityValidator { Task<CustomBotValidationResult> ValidateTokenAsync(ulong guildId, string token, CancellationToken cancellationToken = default); Task<CustomBotGuildValidationResult> ValidateGuildAsync(ulong guildId, string identityId, CancellationToken cancellationToken = default); }

public sealed class CustomBotIdentityValidator(RankoonDbContext database, ICustomBotTokenProtector tokens, IHttpClientFactory clients, IBotRuntimeManager runtimes) : ICustomBotIdentityValidator
{
    public async Task<CustomBotValidationResult> ValidateTokenAsync(ulong guildId, string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return new(false, "customBotIdentity.tokenInvalid", null, null, null, null, null);
        var fingerprint = tokens.CreateFingerprint(token);
        if (await database.GuildBotIdentities.Find(x => x.TokenFingerprint == fingerprint && x.GuildId != guildId).AnyAsync(cancellationToken)) return new(false, "customBotIdentity.tokenAlreadyAssigned", null, null, null, null, null);
        try
        {
            var client = clients.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
            using var userResponse = await client.GetAsync("https://discord.com/api/v10/users/@me", cancellationToken);
            using var applicationResponse = await client.GetAsync("https://discord.com/api/v10/oauth2/applications/@me", cancellationToken);
            if (!userResponse.IsSuccessStatusCode || !applicationResponse.IsSuccessStatusCode) return new(false, "customBotIdentity.tokenInvalid", null, null, null, null, null);
            using var user = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(cancellationToken));
            using var application = JsonDocument.Parse(await applicationResponse.Content.ReadAsStringAsync(cancellationToken));
            var root = user.RootElement;
            var applicationId = ulong.Parse(application.RootElement.GetProperty("id").GetString()!);
            if (await database.GuildBotIdentities.Find(x => x.ApplicationId == applicationId && x.GuildId != guildId).AnyAsync(cancellationToken))
                return new(false, "customBotIdentity.tokenAlreadyAssigned", null, null, null, null, null);
            return new(true, null, applicationId, ulong.Parse(root.GetProperty("id").GetString()!), root.GetProperty("username").GetString(), root.TryGetProperty("global_name", out var name) ? name.GetString() : null, root.TryGetProperty("avatar", out var avatar) ? avatar.GetString() : null);
        }
        catch (HttpRequestException) { return new(false, "customBotIdentity.tokenInvalid", null, null, null, null, null); }
        catch (JsonException) { return new(false, "customBotIdentity.tokenInvalid", null, null, null, null, null); }
    }
    public async Task<CustomBotGuildValidationResult> ValidateGuildAsync(ulong guildId, string identityId, CancellationToken cancellationToken = default)
    {
        var context = await runtimes.GetCustomRuntimeAsync(identityId, cancellationToken);
        if (context == null || context.Guild.Id != guildId)
            return new(false, "customBotIdentity.botNotInstalled", new Dictionary<string, bool> { ["gatewayConnected"] = false, ["guildVisible"] = false });

        var guild = context.Guild;
        // A successful Ready confirms Discord accepted both privileged intents requested by this runtime.
        var memberIntent = true;
        var contentIntent = true;
        var xpSettings = await database.GuildXpSettings.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        var panels = await database.SelfRolePanels.Find(x => x.GuildId == guildId && x.Enabled).ToListAsync(cancellationToken);
        var hubs = await database.VcHubs.Find(x => x.GuildId == guildId && x.Enabled).ToListAsync(cancellationToken);
        var roleIds = (xpSettings?.LevelRoles.Select(x => x.RoleId) ?? []).Concat(panels.SelectMany(x => x.Mappings).Select(x => x.RoleId)).Distinct();
        var rolesManageable = guild.CurrentUser.GuildPermissions.ManageRoles && roleIds.All(id => guild.GetRole(id) is { IsManaged: false, IsEveryone: false } role && role.Position < guild.CurrentUser.Hierarchy);
        var selfRoleChannelsUsable = panels.All(panel => guild.GetTextChannel(panel.ChannelId) is { } channel && HasPanelPermissions(guild.CurrentUser.GetPermissions(channel)));
        var voiceChannelsManageable = hubs.All(hub => guild.GetVoiceChannel(hub.JoinChannelId) is { } channel && guild.CurrentUser.GetPermissions(channel).ManageChannel);
        var configuredChannelIds = panels.Select(x => x.ChannelId).Concat(hubs.Select(x => x.JoinChannelId)).Distinct();
        var channelsVisible = configuredChannelIds.All(id => guild.GetChannel(id) is SocketGuildChannel channel && guild.CurrentUser.GetPermissions(channel).ViewChannel);
        var checks = new Dictionary<string, bool>
        {
            ["gatewayConnected"] = context.Client.ConnectionState == ConnectionState.Connected,
            ["guildVisible"] = true,
            ["guildMembersIntent"] = memberIntent,
            ["messageContentIntent"] = contentIntent,
            ["channelsVisible"] = channelsVisible,
            ["rolesManageable"] = rolesManageable,
            ["voiceChannelsManageable"] = voiceChannelsManageable,
            ["commandsRegisterable"] = guild.CurrentUser.GuildPermissions.UseApplicationCommands,
            ["selfRoleChannelsUsable"] = selfRoleChannelsUsable
        };
        var error = !memberIntent || !contentIntent ? "customBotIdentity.missingIntents" :
            !rolesManageable || !voiceChannelsManageable || !channelsVisible || !selfRoleChannelsUsable ? "customBotIdentity.missingPermissions" : null;
        return new(error == null, error, checks);
    }

    private static bool HasPanelPermissions(ChannelPermissions permissions) => permissions.ViewChannel && permissions.SendMessages && permissions.EmbedLinks && permissions.AddReactions && permissions.ReadMessageHistory && permissions.ManageMessages;
}

public sealed record BotRuntimeSnapshot(string RuntimeId, BotIdentityMode Mode, ulong? GuildId, ulong? ApplicationId, ulong? BotUserId, BotIdentityStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? LastReadyAt, DateTimeOffset? LastEventAt, string? LastErrorCode);
public sealed record BotRuntimeContext(string RuntimeId, BotIdentityMode Mode, DiscordShardedClient Client, SocketGuild Guild);
public sealed record CustomBotRuntimeStartResult(bool Succeeded, string? ErrorCode, BotRuntimeContext? Context);
public sealed record PlatformGuildDepartureResult(bool Succeeded, bool WasAlreadyAbsent, string? ErrorCode);
public interface IPlatformBotRuntime { DiscordShardedClient Client { get; } SocketGuild? GetGuild(ulong guildId); Task<PlatformGuildDepartureResult> LeaveGuildAsync(ulong guildId, CancellationToken cancellationToken = default); }
public sealed class PlatformBotRuntime(DiscordShardedClient client, ILogger<PlatformBotRuntime> logger) : IPlatformBotRuntime
{
    public DiscordShardedClient Client => client;
    public SocketGuild? GetGuild(ulong guildId) => client.GetGuild(guildId);
    public async Task<PlatformGuildDepartureResult> LeaveGuildAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var guild = client.GetGuild(guildId);
        if (guild == null) return new(true, true, null);
        try { await guild.LeaveAsync(new RequestOptions { CancelToken = cancellationToken }); return new(true, false, null); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            const string errorCode = "customBotIdentity.platformBotDepartureFailed";
            logger.LogWarning("Platform bot departure failed for guild {GuildId} with {ErrorCode}", guildId, errorCode);
            return new(false, false, errorCode);
        }
    }
}
public interface IBotRuntimeManager { IReadOnlyCollection<BotRuntimeSnapshot> GetRuntimeSnapshots(); ValueTask<BotRuntimeContext?> ResolveGuildAsync(ulong guildId, CancellationToken cancellationToken = default); ValueTask<BotRuntimeContext?> GetPlatformRuntimeAsync(ulong guildId, CancellationToken cancellationToken = default); ValueTask<BotRuntimeContext?> GetCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default); Task<CustomBotRuntimeStartResult> StartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default); Task StopCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default); Task<CustomBotRuntimeStartResult> RestartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default); }
public interface IGuildBotAuthority { bool IsAuthoritative(ulong guildId, string runtimeId); ValueTask<string?> GetAuthoritativeRuntimeIdAsync(ulong guildId, CancellationToken cancellationToken = default); Task SetCustomAuthorityAsync(ulong guildId, string runtimeId); Task RestorePlatformAuthorityAsync(ulong guildId); }

public sealed class GuildBotAuthority(DiscordShardedClient platform) : IGuildBotAuthority
{
    private readonly ConcurrentDictionary<ulong, string> custom = new();
    public bool IsAuthoritative(ulong guildId, string runtimeId) => string.Equals(custom.TryGetValue(guildId, out var value) ? value : "platform", runtimeId, StringComparison.Ordinal);
    public ValueTask<string?> GetAuthoritativeRuntimeIdAsync(ulong guildId, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(custom.TryGetValue(guildId, out var value) ? value : platform.GetGuild(guildId) == null ? null : "platform");
    public Task SetCustomAuthorityAsync(ulong guildId, string runtimeId) { custom[guildId] = runtimeId; return Task.CompletedTask; }
    public Task RestorePlatformAuthorityAsync(ulong guildId) { custom.TryRemove(guildId, out _); return Task.CompletedTask; }
}

public sealed class BotRuntimeManager(RankoonDbContext database, ICustomBotTokenProtector tokens, DiscordShardedClient platform, IGuildBotAuthority authority, IServiceProvider services, ILogger<BotRuntimeManager> logger) : IBotRuntimeManager
{
    private sealed record Entry(DiscordShardedClient Client, GuildBotIdentity Identity, DateTimeOffset StartedAt, DateTimeOffset? ReadyAt);
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> startLocks = new();
    public IReadOnlyCollection<BotRuntimeSnapshot> GetRuntimeSnapshots() => entries.Select(x => new BotRuntimeSnapshot("custom:" + x.Key, BotIdentityMode.Custom, x.Value.Identity.GuildId, x.Value.Identity.ApplicationId, x.Value.Identity.BotUserId, x.Value.Identity.Status, x.Value.StartedAt, x.Value.ReadyAt, null, x.Value.Identity.LastErrorCode)).Append(new("platform", BotIdentityMode.Rankoon, null, platform.CurrentUser?.Id, platform.CurrentUser?.Id, BotIdentityStatus.Active, null, null, null, null)).ToArray();
    public async ValueTask<BotRuntimeContext?> ResolveGuildAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var runtimeId = await authority.GetAuthoritativeRuntimeIdAsync(guildId, cancellationToken);
        if (runtimeId == null) return null;
        if (runtimeId == "platform") return await GetPlatformRuntimeAsync(guildId, cancellationToken);
        if (!runtimeId.StartsWith("custom:", StringComparison.Ordinal)) return null;
        return await GetCustomRuntimeAsync(runtimeId["custom:".Length..], cancellationToken);
    }
    public ValueTask<BotRuntimeContext?> GetCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default)
    {
        if (entries.TryGetValue(identityId, out var entry) && entry.Client.GetGuild(entry.Identity.GuildId) is { } guild)
            return ValueTask.FromResult<BotRuntimeContext?>(new("custom:" + identityId, BotIdentityMode.Custom, entry.Client, guild));
        return ValueTask.FromResult<BotRuntimeContext?>(null);
    }
    public ValueTask<BotRuntimeContext?> GetPlatformRuntimeAsync(ulong guildId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<BotRuntimeContext?>(platform.GetGuild(guildId) is { } guild ? new("platform", BotIdentityMode.Rankoon, platform, guild) : null);
    public async Task<CustomBotRuntimeStartResult> StartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default)
    {
        var gate = startLocks.GetOrAdd(identityId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await StartCustomRuntimeCoreAsync(identityId, cancellationToken); }
        finally { gate.Release(); }
    }
    private async Task<CustomBotRuntimeStartResult> StartCustomRuntimeCoreAsync(string identityId, CancellationToken cancellationToken)
    {
        if (entries.TryGetValue(identityId, out var existing) && existing.Client.GetGuild(existing.Identity.GuildId) is { } existingGuild) return new(true, null, new("custom:" + identityId, BotIdentityMode.Custom, existing.Client, existingGuild));
        var identity = await database.GuildBotIdentities.Find(x => x.Id == identityId).FirstOrDefaultAsync(cancellationToken);
        if (identity?.EncryptedBotToken == null) return new(false, "customBotIdentity.tokenInvalid", null);
        var client = new DiscordShardedClient(CreateConfig());
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Handler(DiscordSocketClient _) { ready.TrySetResult(true); return Task.CompletedTask; }
        client.ShardReady += Handler;
        try
        {
            await client.LoginAsync(TokenType.Bot, tokens.Unprotect(identity.EncryptedBotToken));
            await client.StartAsync();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            var guild = client.GetGuild(identity.GuildId);
            if (guild == null) { await client.StopAsync(); await client.LogoutAsync(); client.Dispose(); return new(false, "customBotIdentity.botNotInstalled", null); }
            identity.LastConnectedAt = identity.LastReadyAt = DateTime.UtcNow; identity.Status = BotIdentityStatus.Starting; identity.LastErrorCode = null;
            await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
            entries[identityId] = new(client, identity, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            services.GetRequiredService<IDiscordRuntimeEventDispatcher>().Attach("custom:" + identityId, BotIdentityMode.Custom, client, identity.GuildId);
            return new(true, null, new("custom:" + identityId, BotIdentityMode.Custom, client, guild));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Custom bot runtime {IdentityId} could not start", identityId);
            identity.Status = BotIdentityStatus.Degraded; identity.LastErrorCode = "customBotIdentity.runtimeStartFailed"; identity.LastErrorAt = DateTime.UtcNow;
            await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
            try { await client.StopAsync(); await client.LogoutAsync(); } catch { }
            client.Dispose();
            return new(false, identity.LastErrorCode, null);
        }
        finally { client.ShardReady -= Handler; }
    }
    public async Task StopCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default)
    {
        var gate = startLocks.GetOrAdd(identityId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await StopCustomRuntimeCoreAsync(identityId); }
        finally { gate.Release(); }
    }
    private async Task StopCustomRuntimeCoreAsync(string identityId)
    {
        services.GetRequiredService<IDiscordRuntimeEventDispatcher>().Detach("custom:" + identityId);
        if (!entries.TryRemove(identityId, out var entry)) return;
        try { await entry.Client.StopAsync(); await entry.Client.LogoutAsync(); entry.Client.Dispose(); } catch (Exception exception) { logger.LogWarning(exception, "Custom bot runtime {IdentityId} could not stop cleanly", identityId); }
    }
    public async Task<CustomBotRuntimeStartResult> RestartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default)
    {
        var gate = startLocks.GetOrAdd(identityId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await StopCustomRuntimeCoreAsync(identityId); return await StartCustomRuntimeCoreAsync(identityId, cancellationToken); }
        finally { gate.Release(); }
    }
    private static DiscordSocketConfig CreateConfig() => new() { LogLevel = LogSeverity.Warning, MessageCacheSize = 0, AuditLogCacheSize = 0, AlwaysDownloadUsers = true, AlwaysDownloadDefaultStickers = false, TotalShards = 1, GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.GuildMessages | GatewayIntents.GuildMessageReactions | GatewayIntents.GuildScheduledEvents | GatewayIntents.GuildMembers | GatewayIntents.MessageContent };
}

public interface IGuildDiscordContextResolver { ValueTask<GuildDiscordContext?> ResolveAsync(ulong guildId, CancellationToken cancellationToken = default); }
public sealed record GuildDiscordContext(string RuntimeId, BotIdentityMode IdentityMode, DiscordShardedClient Client, SocketGuild Guild);
public sealed class GuildDiscordContextResolver(IBotRuntimeManager runtimes) : IGuildDiscordContextResolver { public async ValueTask<GuildDiscordContext?> ResolveAsync(ulong guildId, CancellationToken cancellationToken = default) => await runtimes.ResolveGuildAsync(guildId, cancellationToken) is { } runtime ? new(runtime.RuntimeId, runtime.Mode, runtime.Client, runtime.Guild) : null; }

public sealed record GuildRuntimePresence(ulong GuildId, BotIdentityMode? AuthoritativeMode, bool AuthoritativeRuntimeAvailable, bool PlatformBotInstalled, bool CustomBotInstalled, bool HasConfiguredCustomIdentity, string? AuthoritativeRuntimeId);
public interface IGuildRuntimePresenceService { GuildRuntimePresence GetPresence(ulong guildId); }
public sealed class GuildRuntimePresenceService(RankoonDbContext database, IBotRuntimeManager runtimes, IGuildBotAuthority authority, IPlatformBotRuntime platform) : IGuildRuntimePresenceService
{
    public GuildRuntimePresence GetPresence(ulong guildId)
    {
        var identity = database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefault();
        var configured = identity?.EncryptedBotToken != null;
        var customId = identity?.Id;
        var customInstalled = customId != null && runtimes.GetCustomRuntimeAsync(customId).AsTask().GetAwaiter().GetResult() != null;
        var runtimeId = authority.GetAuthoritativeRuntimeIdAsync(guildId).AsTask().GetAwaiter().GetResult();
        var authoritativeAvailable = runtimeId == "platform" ? platform.GetGuild(guildId) != null : runtimeId?.StartsWith("custom:", StringComparison.Ordinal) == true && customInstalled;
        return new(guildId, runtimeId == "platform" ? BotIdentityMode.Rankoon : runtimeId?.StartsWith("custom:", StringComparison.Ordinal) == true ? BotIdentityMode.Custom : null, authoritativeAvailable, platform.GetGuild(guildId) != null, customInstalled, configured, runtimeId);
    }
}
public sealed record CustomBotIdentityView(ulong GuildId, BotIdentityMode Mode, BotIdentityStatus Status, ulong? ApplicationId, ulong? BotUserId, string? BotUsername, string? BotGlobalName, string? BotAvatarHash, bool HasStoredToken, DateTime? LastValidatedAt, DateTime? LastConnectedAt, DateTime? LastReadyAt, string? LastErrorCode, long Revision, bool PlatformBotInstalled, bool CustomBotInstalled, bool AuthoritativeRuntimeAvailable, PlatformBotDepartureState PlatformDepartureState, string? PlatformDepartureErrorCode, DateTime? PlatformDepartureAttemptedAt, DateTime? PlatformDepartedAt);
public sealed record CustomBotOperationResult(bool Succeeded, string? ErrorCode, CustomBotIdentityView? Identity = null, string? InstallUrl = null, IReadOnlyDictionary<string, bool>? Diagnostics = null, IReadOnlyList<string>? WarningCodes = null, string? RequiredAction = null);
public interface ICustomBotIdentityService
{
    Task<CustomBotIdentityView?> GetAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> StoreTokenAsync(ulong guildId, ulong userId, string token, long? revision, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> GetInstallUrlAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> ValidateAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> ActivateAsync(ulong guildId, ulong userId, long? revision, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> RestartAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> CompleteHandoverAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> DeactivateAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> DeleteAsync(ulong guildId, CancellationToken cancellationToken = default);
}

public sealed class CustomBotIdentityService(RankoonDbContext database, ICustomBotIdentityAccessPolicy policy, ICustomBotIdentityValidator validator, ICustomBotTokenProtector tokens, IBotRuntimeManager runtimes, IGuildBotAuthority authority, IPlatformBotRuntime platform, IGuildRuntimePresenceService presence, ApplicationCommandRegistrar commands, SelfRoleService selfRoles, IOptions<Rankoon.Data.Auth.DiscordSettings> discord, TimeProvider timeProvider) : ICustomBotIdentityService
{
    private static readonly SemaphoreSlim ActivationLock = new(1, 1);
    public async Task<CustomBotIdentityView?> GetAsync(ulong guildId, CancellationToken cancellationToken = default) => (await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken)) is { } identity ? ToView(identity, presence.GetPresence(guildId)) : null;
    public async Task<CustomBotOperationResult> StoreTokenAsync(ulong guildId, ulong userId, string token, long? revision, CancellationToken cancellationToken = default)
    {
        var access = await policy.EvaluateAsync(guildId, cancellationToken);
        if (!access.IsEligible) return Fail(access.Reason);
        var validation = await validator.ValidateTokenAsync(guildId, token, cancellationToken);
        if (!validation.IsValid) return new(false, validation.ErrorCode);
        var existing = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        var previous = existing == null ? null : BsonSerializer.Deserialize<GuildBotIdentity>(existing.ToBson());
        if (revision.HasValue && existing != null && existing.Revision != revision.Value) return new(false, "customBotIdentity.revisionConflict");
        if (access.HasReservation && existing?.ApplicationId != validation.ApplicationId) return new(false, "customBotIdentity.tokenApplicationMismatch");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var identity = existing ?? new GuildBotIdentity { GuildId = guildId, CreatedByUserId = userId, CreatedAt = now };
        identity.Mode = BotIdentityMode.Custom; identity.Status = BotIdentityStatus.AwaitingInstallation; identity.ApplicationId = validation.ApplicationId; identity.BotUserId = validation.BotUserId; identity.BotUsername = validation.BotUsername; identity.BotGlobalName = validation.BotGlobalName; identity.BotAvatarHash = validation.BotAvatarHash; identity.EncryptedBotToken = tokens.Protect(token); identity.TokenFingerprint = tokens.CreateFingerprint(token); identity.LastValidatedAt = now; identity.LastErrorCode = null; identity.UpdatedAt = now; identity.Revision++;
        if (existing == null) await database.GuildBotIdentities.InsertOneAsync(identity, cancellationToken: cancellationToken); else await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
        if (access.HasReservation && previous?.Id != null)
        {
            var restarted = await runtimes.RestartCustomRuntimeAsync(previous.Id, cancellationToken);
            var runtimeValidation = restarted.Succeeded ? await validator.ValidateGuildAsync(guildId, previous.Id, cancellationToken) : null;
            if (!restarted.Succeeded || runtimeValidation?.IsValid != true)
            {
                await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == previous.Id, previous, cancellationToken: cancellationToken);
                await runtimes.RestartCustomRuntimeAsync(previous.Id, cancellationToken);
                return new(false, restarted.ErrorCode ?? runtimeValidation?.ErrorCode ?? "customBotIdentity.runtimeStartFailed", ToView(previous, presence.GetPresence(guildId)), Diagnostics: runtimeValidation?.Checks);
            }
            await database.GuildBotIdentities.UpdateOneAsync(x => x.Id == previous.Id,
                Builders<GuildBotIdentity>.Update.Set(x => x.Status, BotIdentityStatus.Active).Set(x => x.LastErrorCode, null).Set(x => x.UpdatedAt, now).Inc(x => x.Revision, 1), cancellationToken: cancellationToken);
            identity = await database.GuildBotIdentities.Find(x => x.Id == previous.Id).FirstAsync(cancellationToken);
        }
            return new(true, null, ToView(identity, presence.GetPresence(guildId)));
    }
    public async Task<CustomBotOperationResult> GetInstallUrlAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var access = await policy.EvaluateAsync(guildId, cancellationToken);
        if (!access.IsEligible) return Fail(access.Reason);
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity?.ApplicationId is not ulong applicationId) return new(false, "customBotIdentity.tokenInvalid");
        var permissions = discord.Value.BotInvitePermissions;
        return new(true, null, ToView(identity, presence.GetPresence(guildId)), $"https://discord.com/oauth2/authorize?client_id={applicationId}&scope=bot%20applications.commands&permissions={Uri.EscapeDataString(permissions)}&guild_id={guildId}&disable_guild_select=true");
    }
    public async Task<CustomBotOperationResult> ValidateAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var access = await policy.EvaluateAsync(guildId, cancellationToken);
        if (!access.IsEligible) return Fail(access.Reason);
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity?.Id == null || identity.EncryptedBotToken == null) return new(false, "customBotIdentity.tokenInvalid");
        var alreadyRunning = await runtimes.GetCustomRuntimeAsync(identity.Id, cancellationToken) != null;
        var started = await runtimes.StartCustomRuntimeAsync(identity.Id, cancellationToken);
        if (!started.Succeeded) return new(false, started.ErrorCode);
        var diagnostic = await validator.ValidateGuildAsync(guildId, identity.Id, cancellationToken);
        var validatedAt = timeProvider.GetUtcNow().UtcDateTime;
        var status = diagnostic.IsValid ? access.HasReservation ? BotIdentityStatus.Active : BotIdentityStatus.AwaitingInstallation : diagnostic.ErrorCode switch { "customBotIdentity.missingIntents" => BotIdentityStatus.MissingIntents, "customBotIdentity.missingPermissions" => BotIdentityStatus.MissingPermissions, _ => BotIdentityStatus.RemovedFromGuild };
        var update = Builders<GuildBotIdentity>.Update.Set(x => x.LastValidatedAt, validatedAt).Set(x => x.Status, status).Set(x => x.LastErrorCode, diagnostic.ErrorCode).Set(x => x.UpdatedAt, validatedAt).Inc(x => x.Revision, 1);
        update = diagnostic.IsValid ? update.Unset(x => x.LastErrorAt) : update.Set(x => x.LastErrorAt, validatedAt);
        await database.GuildBotIdentities.UpdateOneAsync(x => x.Id == identity.Id, update, cancellationToken: cancellationToken);
        identity = await database.GuildBotIdentities.Find(x => x.Id == identity.Id).FirstAsync(cancellationToken);
        if (!alreadyRunning && !access.HasReservation) await runtimes.StopCustomRuntimeAsync(identity.Id!, cancellationToken);
            return new(diagnostic.IsValid, diagnostic.ErrorCode, ToView(identity, presence.GetPresence(guildId)), Diagnostics: diagnostic.Checks);
    }
    public async Task<CustomBotOperationResult> ActivateAsync(ulong guildId, ulong userId, long? revision, CancellationToken cancellationToken = default)
    {
        await ActivationLock.WaitAsync(cancellationToken);
        string? identityId = null; var reserved = false; var migrated = false; BotRuntimeContext? source = null; BotRuntimeContext? target = null;
        try
        {
            var access = await policy.EvaluateAsync(guildId, cancellationToken);
            if (!access.CanActivate) return Fail(access.Reason);
            var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
            if (identity?.Id == null || identity.EncryptedBotToken == null) return new(false, "customBotIdentity.tokenInvalid");
            if (revision.HasValue && identity.Revision != revision.Value) return new(false, "customBotIdentity.revisionConflict");
            identityId = identity.Id;
            source = await runtimes.ResolveGuildAsync(guildId, cancellationToken);
            var started = await runtimes.StartCustomRuntimeAsync(identityId, cancellationToken);
            if (!started.Succeeded) return new(false, started.ErrorCode);
            target = started.Context;
            var validation = await validator.ValidateGuildAsync(guildId, identityId, cancellationToken);
            if (!validation.IsValid) { await runtimes.StopCustomRuntimeAsync(identityId, cancellationToken); return new(false, validation.ErrorCode, Diagnostics: validation.Checks); }
            if (target == null || !await commands.RegisterAsync(target, cancellationToken)) { await runtimes.StopCustomRuntimeAsync(identityId, cancellationToken); return new(false, "customBotIdentity.commandRegistrationFailed"); }
            if (source != null && source.RuntimeId != target.RuntimeId)
            {
                try { await selfRoles.MigrateIdentityAsync(source.Guild, target.Guild, cancellationToken); migrated = true; }
                catch { await runtimes.StopCustomRuntimeAsync(identityId, cancellationToken); return new(false, "customBotIdentity.selfRoleMigrationFailed"); }
            }
            if (!access.HasReservation)
            {
                try { await database.CustomBotCapacityReservations.InsertOneAsync(new CustomBotCapacityReservation { GuildId = guildId, IdentityId = identityId, ReservedAtUtc = timeProvider.GetUtcNow().UtcDateTime, ReservedByUserId = userId }, cancellationToken: cancellationToken); reserved = true; }
                catch (MongoWriteException) { if (migrated && source != null && target != null) await selfRoles.MigrateIdentityAsync(target.Guild, source.Guild, cancellationToken); await runtimes.StopCustomRuntimeAsync(identityId, cancellationToken); return new(false, "customBotIdentity.capacityReached"); }
            }
            var now = timeProvider.GetUtcNow().UtcDateTime;
            await database.GuildBotIdentities.UpdateOneAsync(x => x.Id == identityId,
                Builders<GuildBotIdentity>.Update.Set(x => x.Status, BotIdentityStatus.Active).Set(x => x.LastErrorCode, null).Set(x => x.UpdatedAt, now).Inc(x => x.Revision, 1), cancellationToken: cancellationToken);
            await authority.SetCustomAuthorityAsync(guildId, "custom:" + identityId);
            if (await runtimes.ResolveGuildAsync(guildId, cancellationToken) == null) throw new InvalidOperationException("Authoritative custom runtime is unavailable.");
            await database.GuildBotIdentities.UpdateOneAsync(x => x.Id == identityId, Builders<GuildBotIdentity>.Update.Set(x => x.PlatformDepartureState, PlatformBotDepartureState.Pending), cancellationToken: cancellationToken);
            identity = await database.GuildBotIdentities.Find(x => x.Id == identityId).FirstAsync(cancellationToken);
            var departure = await CompleteHandoverAsync(guildId, cancellationToken);
            identity = await database.GuildBotIdentities.Find(x => x.Id == identityId).FirstAsync(cancellationToken);
            return new(true, null, ToView(identity, presence.GetPresence(guildId)), Diagnostics: validation.Checks, WarningCodes: departure.WarningCodes);
        }
        catch
        {
            if (reserved) await database.CustomBotCapacityReservations.DeleteOneAsync(x => x.GuildId == guildId, cancellationToken);
            await authority.RestorePlatformAuthorityAsync(guildId);
            if (migrated && source != null && target != null) try { await selfRoles.MigrateIdentityAsync(target.Guild, source.Guild, cancellationToken); } catch { }
            if (identityId != null) await runtimes.StopCustomRuntimeAsync(identityId, cancellationToken);
            return new(false, "customBotIdentity.runtimeStartFailed");
        }
        finally { ActivationLock.Release(); }
    }
    public async Task<CustomBotOperationResult> RestartAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var access = await policy.EvaluateAsync(guildId, cancellationToken);
        if (!access.IsEligible) return Fail(access.Reason);
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity?.Id == null || !access.HasReservation) return new(false, "customBotIdentity.tokenInvalid");
        var started = await runtimes.RestartCustomRuntimeAsync(identity.Id, cancellationToken);
        if (!started.Succeeded) return new(false, started.ErrorCode);
        var validation = await validator.ValidateGuildAsync(guildId, identity.Id, cancellationToken);
        if (!validation.IsValid)
        {
            await runtimes.StopCustomRuntimeAsync(identity.Id, cancellationToken);
            await database.GuildBotIdentities.UpdateOneAsync(x => x.Id == identity.Id, Builders<GuildBotIdentity>.Update.Set(x => x.Status, BotIdentityStatus.Degraded).Set(x => x.LastErrorCode, validation.ErrorCode).Set(x => x.LastErrorAt, timeProvider.GetUtcNow().UtcDateTime).Inc(x => x.Revision, 1), cancellationToken: cancellationToken);
            return new(false, validation.ErrorCode, ToView(identity, presence.GetPresence(guildId)), Diagnostics: validation.Checks);
        }
        await authority.SetCustomAuthorityAsync(guildId, "custom:" + identity.Id);
        identity.Status = BotIdentityStatus.Active; identity.LastErrorCode = null; identity.LastReadyAt = identity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime; identity.Revision++;
        await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
        await CompleteHandoverAsync(guildId, cancellationToken);
        return new(true, null, ToView(identity, presence.GetPresence(guildId)), Diagnostics: validation.Checks);
    }
    public async Task<CustomBotOperationResult> CompleteHandoverAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity?.Id == null || identity.Status != BotIdentityStatus.Active || !authority.IsAuthoritative(guildId, "custom:" + identity.Id)) return new(false, "customBotIdentity.authoritativeRuntimeUnavailable");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = await platform.LeaveGuildAsync(guildId, cancellationToken);
        var update = Builders<GuildBotIdentity>.Update.Set(x => x.PlatformDepartureAttemptedAt, now).Set(x => x.UpdatedAt, now);
        if (result.Succeeded) update = update.Set(x => x.PlatformDepartureState, PlatformBotDepartureState.Completed).Set(x => x.PlatformDepartureErrorCode, null).Set(x => x.PlatformDepartedAt, now);
        else update = update.Set(x => x.PlatformDepartureState, PlatformBotDepartureState.Failed).Set(x => x.PlatformDepartureErrorCode, "customBotIdentity.platformBotDepartureFailed");
        await database.GuildBotIdentities.UpdateOneAsync(x => x.Id == identity.Id, update, cancellationToken: cancellationToken);
        identity = await database.GuildBotIdentities.Find(x => x.Id == identity.Id).FirstAsync(cancellationToken);
        return new(true, null, ToView(identity, presence.GetPresence(guildId)), WarningCodes: result.Succeeded ? null : ["customBotIdentity.platformBotDepartureFailed"], RequiredAction: result.Succeeded ? null : "completeHandover");
    }
    public async Task<CustomBotOperationResult> DeactivateAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity == null) return new(true, null);
        var platform = await runtimes.GetPlatformRuntimeAsync(guildId, cancellationToken);
        if (platform == null) return new(false, "customBotIdentity.platformBotNotInstalled", ToView(identity, presence.GetPresence(guildId)), GetPlatformInstallUrl(guildId), RequiredAction: "installPlatformBot");
        var custom = identity.Id == null ? null : await runtimes.GetCustomRuntimeAsync(identity.Id, cancellationToken);
        if (custom != null && platform != null)
        {
            try { await selfRoles.MigrateIdentityAsync(custom.Guild, platform.Guild, cancellationToken); }
            catch { return new(false, "customBotIdentity.selfRoleMigrationFailed", ToView(identity, presence.GetPresence(guildId))); }
        }
        // The platform gateway is known to see the guild, so switching authority cannot strand it.
        await authority.RestorePlatformAuthorityAsync(guildId);
        identity.Status = BotIdentityStatus.Disabled; identity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime; identity.Revision++;
        await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
        if (identity.Id != null) await runtimes.StopCustomRuntimeAsync(identity.Id, cancellationToken);
        await database.CustomBotCapacityReservations.DeleteOneAsync(x => x.GuildId == guildId, cancellationToken);
        return new(true, null, ToView(identity, presence.GetPresence(guildId)));
    }
    public async Task<CustomBotOperationResult> DeleteAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity == null) return new(true, null);
        if (await database.CustomBotCapacityReservations.Find(x => x.GuildId == guildId).AnyAsync(cancellationToken))
        {
            var deactivated = await DeactivateAsync(guildId, cancellationToken);
            if (!deactivated.Succeeded) return deactivated;
        }
        else if (identity.Id != null) await runtimes.StopCustomRuntimeAsync(identity.Id, cancellationToken);
        await database.CustomBotCapacityReservations.DeleteOneAsync(x => x.GuildId == guildId, cancellationToken);
        await database.GuildBotIdentities.DeleteOneAsync(x => x.GuildId == guildId, cancellationToken);
        await authority.RestorePlatformAuthorityAsync(guildId);
        return new(true, null);
    }
    private static CustomBotOperationResult Fail(CustomBotAccessReason reason) => new(false, reason switch { CustomBotAccessReason.FeatureDisabled => "customBotIdentity.disabled", CustomBotAccessReason.GuildNotAllowed => "customBotIdentity.guildNotAllowed", CustomBotAccessReason.CapacityReached => "customBotIdentity.capacityReached", _ => "customBotIdentity.disabled" });
    private string GetPlatformInstallUrl(ulong guildId) => $"https://discord.com/oauth2/authorize?client_id={discord.Value.ClientId}&scope=bot%20applications.commands&permissions={Uri.EscapeDataString(discord.Value.BotInvitePermissions)}&guild_id={guildId}&disable_guild_select=true";
    private static CustomBotIdentityView ToView(GuildBotIdentity x, GuildRuntimePresence runtime) => new(x.GuildId, x.Mode, x.Status, x.ApplicationId, x.BotUserId, x.BotUsername, x.BotGlobalName, x.BotAvatarHash, x.EncryptedBotToken != null, x.LastValidatedAt, x.LastConnectedAt, x.LastReadyAt, x.LastErrorCode, x.Revision, runtime.PlatformBotInstalled, runtime.CustomBotInstalled, runtime.AuthoritativeRuntimeAvailable, x.PlatformDepartureState, x.PlatformDepartureErrorCode, x.PlatformDepartureAttemptedAt, x.PlatformDepartedAt);
}
