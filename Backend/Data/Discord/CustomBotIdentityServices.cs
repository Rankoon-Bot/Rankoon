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
        if (!configuration.Enabled) return new(false, false, reservation, identity?.EncryptedBotToken != null, count, configuration.MaxActiveGuilds, CustomBotAccessReason.FeatureDisabled);
        if (configuration.AllowedGuildIds.Count > 0 && !configuration.AllowedGuildIds.Contains(guildId)) return new(false, false, reservation, identity?.EncryptedBotToken != null, count, configuration.MaxActiveGuilds, CustomBotAccessReason.GuildNotAllowed);
        if (reservation) return new(true, true, true, identity?.EncryptedBotToken != null, count, configuration.MaxActiveGuilds, CustomBotAccessReason.AlreadyReserved);
        if (configuration.MaxActiveGuilds is int maximum && count >= maximum) return new(true, false, false, identity?.EncryptedBotToken != null, count, maximum, CustomBotAccessReason.CapacityReached);
        return new(true, true, false, identity?.EncryptedBotToken != null, count, configuration.MaxActiveGuilds, CustomBotAccessReason.Available);
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
            return new(true, null, ulong.Parse(application.RootElement.GetProperty("id").GetString()!), ulong.Parse(root.GetProperty("id").GetString()!), root.GetProperty("username").GetString(), root.TryGetProperty("global_name", out var name) ? name.GetString() : null, root.TryGetProperty("avatar", out var avatar) ? avatar.GetString() : null);
        }
        catch (HttpRequestException) { return new(false, "customBotIdentity.tokenInvalid", null, null, null, null, null); }
        catch (JsonException) { return new(false, "customBotIdentity.tokenInvalid", null, null, null, null, null); }
    }
    public async Task<CustomBotGuildValidationResult> ValidateGuildAsync(ulong guildId, string identityId, CancellationToken cancellationToken = default)
    {
        var context = await runtimes.ResolveGuildAsync(guildId, cancellationToken);
        var checks = new Dictionary<string, bool> { ["gatewayConnected"] = context != null, ["guildVisible"] = context != null, ["guildMembersIntent"] = context != null, ["messageContentIntent"] = context != null, ["channelsVisible"] = context != null, ["rolesManageable"] = context != null, ["voiceChannelsManageable"] = context != null, ["commandsRegisterable"] = context != null, ["selfRoleChannelsUsable"] = context != null };
        return new(context != null, context == null ? "customBotIdentity.botNotInstalled" : null, checks);
    }
}

public sealed record BotRuntimeSnapshot(string RuntimeId, BotIdentityMode Mode, ulong? GuildId, ulong? ApplicationId, ulong? BotUserId, BotIdentityStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? LastReadyAt, DateTimeOffset? LastEventAt, string? LastErrorCode);
public sealed record BotRuntimeContext(string RuntimeId, BotIdentityMode Mode, DiscordShardedClient Client, SocketGuild Guild);
public sealed record CustomBotRuntimeStartResult(bool Succeeded, string? ErrorCode, BotRuntimeContext? Context);
public interface IBotRuntimeManager { IReadOnlyCollection<BotRuntimeSnapshot> GetRuntimeSnapshots(); ValueTask<BotRuntimeContext?> ResolveGuildAsync(ulong guildId, CancellationToken cancellationToken = default); Task<CustomBotRuntimeStartResult> StartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default); Task StopCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default); Task RestartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default); }
public interface IGuildBotAuthority { bool IsAuthoritative(ulong guildId, string runtimeId); ValueTask<string?> GetAuthoritativeRuntimeIdAsync(ulong guildId, CancellationToken cancellationToken = default); Task SetCustomAuthorityAsync(ulong guildId, string runtimeId); Task RestorePlatformAuthorityAsync(ulong guildId); }

public sealed class GuildBotAuthority(DiscordShardedClient platform) : IGuildBotAuthority
{
    private readonly ConcurrentDictionary<ulong, string> custom = new();
    public bool IsAuthoritative(ulong guildId, string runtimeId) => string.Equals(custom.TryGetValue(guildId, out var value) ? value : "platform", runtimeId, StringComparison.Ordinal);
    public ValueTask<string?> GetAuthoritativeRuntimeIdAsync(ulong guildId, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(custom.TryGetValue(guildId, out var value) ? value : platform.GetGuild(guildId) == null ? null : "platform");
    public Task SetCustomAuthorityAsync(ulong guildId, string runtimeId) { custom[guildId] = runtimeId; return Task.CompletedTask; }
    public Task RestorePlatformAuthorityAsync(ulong guildId) { custom.TryRemove(guildId, out _); return Task.CompletedTask; }
}

public sealed class BotRuntimeManager(RankoonDbContext database, ICustomBotTokenProtector tokens, DiscordShardedClient platform, IGuildBotAuthority authority, ILogger<BotRuntimeManager> logger) : IBotRuntimeManager
{
    private sealed record Entry(DiscordShardedClient Client, GuildBotIdentity Identity, DateTimeOffset StartedAt, DateTimeOffset? ReadyAt);
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    public IReadOnlyCollection<BotRuntimeSnapshot> GetRuntimeSnapshots() => entries.Select(x => new BotRuntimeSnapshot("custom:" + x.Key, BotIdentityMode.Custom, x.Value.Identity.GuildId, x.Value.Identity.ApplicationId, x.Value.Identity.BotUserId, x.Value.Identity.Status, x.Value.StartedAt, x.Value.ReadyAt, null, x.Value.Identity.LastErrorCode)).Append(new("platform", BotIdentityMode.Rankoon, null, platform.CurrentUser?.Id, platform.CurrentUser?.Id, BotIdentityStatus.Active, null, null, null, null)).ToArray();
    public ValueTask<BotRuntimeContext?> ResolveGuildAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        if (entries.Values.FirstOrDefault(x => x.Identity.GuildId == guildId) is { } custom && authority.IsAuthoritative(guildId, "custom:" + custom.Identity.Id) && custom.Client.GetGuild(guildId) is { } guild) return ValueTask.FromResult<BotRuntimeContext?>(new("custom:" + custom.Identity.Id, BotIdentityMode.Custom, custom.Client, guild));
        return ValueTask.FromResult<BotRuntimeContext?>(platform.GetGuild(guildId) is { } platformGuild ? new("platform", BotIdentityMode.Rankoon, platform, platformGuild) : null);
    }
    public async Task<CustomBotRuntimeStartResult> StartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default)
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
            if (guild == null) { await client.StopAsync(); await client.LogoutAsync(); return new(false, "customBotIdentity.botNotInstalled", null); }
            identity.LastConnectedAt = identity.LastReadyAt = DateTime.UtcNow; identity.Status = BotIdentityStatus.Starting; identity.LastErrorCode = null;
            await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
            entries[identityId] = new(client, identity, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return new(true, null, new("custom:" + identityId, BotIdentityMode.Custom, client, guild));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Custom bot runtime {IdentityId} could not start", identityId);
            identity.Status = BotIdentityStatus.Degraded; identity.LastErrorCode = "customBotIdentity.runtimeStartFailed"; identity.LastErrorAt = DateTime.UtcNow;
            await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
            try { await client.StopAsync(); await client.LogoutAsync(); } catch { }
            return new(false, identity.LastErrorCode, null);
        }
        finally { client.ShardReady -= Handler; }
    }
    public async Task StopCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default)
    {
        if (!entries.TryRemove(identityId, out var entry)) return;
        try { await entry.Client.StopAsync(); await entry.Client.LogoutAsync(); entry.Client.Dispose(); } catch (Exception exception) { logger.LogWarning(exception, "Custom bot runtime {IdentityId} could not stop cleanly", identityId); }
    }
    public async Task RestartCustomRuntimeAsync(string identityId, CancellationToken cancellationToken = default) { await StopCustomRuntimeAsync(identityId, cancellationToken); await StartCustomRuntimeAsync(identityId, cancellationToken); }
    private static DiscordSocketConfig CreateConfig() => new() { LogLevel = LogSeverity.Warning, MessageCacheSize = 0, AuditLogCacheSize = 0, AlwaysDownloadUsers = false, AlwaysDownloadDefaultStickers = false, TotalShards = 1, GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.GuildMessages | GatewayIntents.GuildMessageReactions | GatewayIntents.GuildScheduledEvents | GatewayIntents.GuildMembers | GatewayIntents.MessageContent };
}

public interface IGuildDiscordContextResolver { ValueTask<GuildDiscordContext?> ResolveAsync(ulong guildId, CancellationToken cancellationToken = default); }
public sealed record GuildDiscordContext(string RuntimeId, BotIdentityMode IdentityMode, DiscordShardedClient Client, SocketGuild Guild);
public sealed class GuildDiscordContextResolver(IBotRuntimeManager runtimes) : IGuildDiscordContextResolver { public async ValueTask<GuildDiscordContext?> ResolveAsync(ulong guildId, CancellationToken cancellationToken = default) => await runtimes.ResolveGuildAsync(guildId, cancellationToken) is { } runtime ? new(runtime.RuntimeId, runtime.Mode, runtime.Client, runtime.Guild) : null; }

public sealed record CustomBotIdentityView(ulong GuildId, BotIdentityMode Mode, BotIdentityStatus Status, ulong? ApplicationId, ulong? BotUserId, string? BotUsername, string? BotGlobalName, string? BotAvatarHash, bool HasStoredToken, DateTime? LastValidatedAt, DateTime? LastConnectedAt, DateTime? LastReadyAt, string? LastErrorCode, long Revision);
public sealed record CustomBotOperationResult(bool Succeeded, string? ErrorCode, CustomBotIdentityView? Identity = null, string? InstallUrl = null, IReadOnlyDictionary<string, bool>? Diagnostics = null);
public interface ICustomBotIdentityService
{
    Task<CustomBotIdentityView?> GetAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> StoreTokenAsync(ulong guildId, ulong userId, string token, long? revision, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> GetInstallUrlAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> ValidateAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> ActivateAsync(ulong guildId, ulong userId, long? revision, CancellationToken cancellationToken = default);
    Task<CustomBotOperationResult> DeactivateAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task DeleteAsync(ulong guildId, CancellationToken cancellationToken = default);
}

public sealed class CustomBotIdentityService(RankoonDbContext database, ICustomBotIdentityAccessPolicy policy, ICustomBotIdentityValidator validator, ICustomBotTokenProtector tokens, IBotRuntimeManager runtimes, IGuildBotAuthority authority, IOptions<Rankoon.Data.Auth.DiscordSettings> discord, TimeProvider timeProvider) : ICustomBotIdentityService
{
    private static readonly SemaphoreSlim ActivationLock = new(1, 1);
    public async Task<CustomBotIdentityView?> GetAsync(ulong guildId, CancellationToken cancellationToken = default) => (await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken)) is { } identity ? ToView(identity) : null;
    public async Task<CustomBotOperationResult> StoreTokenAsync(ulong guildId, ulong userId, string token, long? revision, CancellationToken cancellationToken = default)
    {
        var access = await policy.EvaluateAsync(guildId, cancellationToken);
        if (!access.IsEligible) return Fail(access.Reason);
        var validation = await validator.ValidateTokenAsync(guildId, token, cancellationToken);
        if (!validation.IsValid) return new(false, validation.ErrorCode);
        var existing = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (revision.HasValue && existing != null && existing.Revision != revision.Value) return new(false, "customBotIdentity.revisionConflict");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var identity = existing ?? new GuildBotIdentity { GuildId = guildId, CreatedByUserId = userId, CreatedAt = now };
        identity.Mode = BotIdentityMode.Custom; identity.Status = BotIdentityStatus.AwaitingInstallation; identity.ApplicationId = validation.ApplicationId; identity.BotUserId = validation.BotUserId; identity.BotUsername = validation.BotUsername; identity.BotGlobalName = validation.BotGlobalName; identity.BotAvatarHash = validation.BotAvatarHash; identity.EncryptedBotToken = tokens.Protect(token); identity.TokenFingerprint = tokens.CreateFingerprint(token); identity.LastValidatedAt = now; identity.LastErrorCode = null; identity.UpdatedAt = now; identity.Revision++;
        if (existing == null) await database.GuildBotIdentities.InsertOneAsync(identity, cancellationToken: cancellationToken); else await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
        return new(true, null, ToView(identity));
    }
    public async Task<CustomBotOperationResult> GetInstallUrlAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity?.ApplicationId is not ulong applicationId) return new(false, "customBotIdentity.tokenInvalid");
        var permissions = discord.Value.BotInvitePermissions;
        return new(true, null, ToView(identity), $"https://discord.com/oauth2/authorize?client_id={applicationId}&scope=bot%20applications.commands&permissions={Uri.EscapeDataString(permissions)}&guild_id={guildId}&disable_guild_select=true");
    }
    public async Task<CustomBotOperationResult> ValidateAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity?.Id == null || identity.EncryptedBotToken == null) return new(false, "customBotIdentity.tokenInvalid");
        var started = await runtimes.StartCustomRuntimeAsync(identity.Id, cancellationToken);
        var diagnostic = await validator.ValidateGuildAsync(guildId, identity.Id, cancellationToken);
        identity.LastValidatedAt = timeProvider.GetUtcNow().UtcDateTime;
        identity.Status = diagnostic.IsValid ? BotIdentityStatus.AwaitingInstallation : BotIdentityStatus.RemovedFromGuild;
        identity.LastErrorCode = diagnostic.ErrorCode; identity.LastErrorAt = diagnostic.IsValid ? null : identity.LastValidatedAt; identity.UpdatedAt = identity.LastValidatedAt.Value; identity.Revision++;
        await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
        if (!diagnostic.IsValid && !started.Succeeded) await runtimes.StopCustomRuntimeAsync(identity.Id, cancellationToken);
        return new(diagnostic.IsValid, diagnostic.ErrorCode, ToView(identity), Diagnostics: diagnostic.Checks);
    }
    public async Task<CustomBotOperationResult> ActivateAsync(ulong guildId, ulong userId, long? revision, CancellationToken cancellationToken = default)
    {
        await ActivationLock.WaitAsync(cancellationToken);
        string? identityId = null; var reserved = false;
        try
        {
            var access = await policy.EvaluateAsync(guildId, cancellationToken);
            if (!access.CanActivate) return Fail(access.Reason);
            var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
            if (identity?.Id == null || identity.EncryptedBotToken == null) return new(false, "customBotIdentity.tokenInvalid");
            if (revision.HasValue && identity.Revision != revision.Value) return new(false, "customBotIdentity.revisionConflict");
            identityId = identity.Id;
            var started = await runtimes.StartCustomRuntimeAsync(identityId, cancellationToken);
            if (!started.Succeeded) return new(false, started.ErrorCode);
            var validation = await validator.ValidateGuildAsync(guildId, identityId, cancellationToken);
            if (!validation.IsValid) { await runtimes.StopCustomRuntimeAsync(identityId, cancellationToken); return new(false, validation.ErrorCode, Diagnostics: validation.Checks); }
            if (!access.HasReservation)
            {
                try { await database.CustomBotCapacityReservations.InsertOneAsync(new CustomBotCapacityReservation { GuildId = guildId, IdentityId = identityId, ReservedAtUtc = timeProvider.GetUtcNow().UtcDateTime, ReservedByUserId = userId }, cancellationToken: cancellationToken); reserved = true; }
                catch (MongoWriteException) { return new(false, "customBotIdentity.capacityReached"); }
            }
            await authority.SetCustomAuthorityAsync(guildId, "custom:" + identityId);
            identity.Status = BotIdentityStatus.Active; identity.LastErrorCode = null; identity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime; identity.Revision++;
            await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identityId, identity, cancellationToken: cancellationToken);
            return new(true, null, ToView(identity), Diagnostics: validation.Checks);
        }
        catch
        {
            if (reserved) await database.CustomBotCapacityReservations.DeleteOneAsync(x => x.GuildId == guildId, cancellationToken);
            if (identityId != null) await runtimes.StopCustomRuntimeAsync(identityId, cancellationToken);
            return new(false, "customBotIdentity.runtimeStartFailed");
        }
        finally { ActivationLock.Release(); }
    }
    public async Task<CustomBotOperationResult> DeactivateAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity == null) return new(true, null);
        if (identity.Id != null) await runtimes.StopCustomRuntimeAsync(identity.Id, cancellationToken);
        await database.CustomBotCapacityReservations.DeleteOneAsync(x => x.GuildId == guildId, cancellationToken);
        await authority.RestorePlatformAuthorityAsync(guildId);
        identity.Status = BotIdentityStatus.Disabled; identity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime; identity.Revision++;
        await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
        return new(true, null, ToView(identity));
    }
    public async Task DeleteAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        var identity = await database.GuildBotIdentities.Find(x => x.GuildId == guildId).FirstOrDefaultAsync(cancellationToken);
        if (identity?.Id != null) await runtimes.StopCustomRuntimeAsync(identity.Id, cancellationToken);
        await database.CustomBotCapacityReservations.DeleteOneAsync(x => x.GuildId == guildId, cancellationToken);
        await database.GuildBotIdentities.DeleteOneAsync(x => x.GuildId == guildId, cancellationToken);
        await authority.RestorePlatformAuthorityAsync(guildId);
    }
    private static CustomBotOperationResult Fail(CustomBotAccessReason reason) => new(false, reason switch { CustomBotAccessReason.FeatureDisabled => "customBotIdentity.disabled", CustomBotAccessReason.GuildNotAllowed => "customBotIdentity.guildNotAllowed", CustomBotAccessReason.CapacityReached => "customBotIdentity.capacityReached", _ => "customBotIdentity.disabled" });
    private static CustomBotIdentityView ToView(GuildBotIdentity x) => new(x.GuildId, x.Mode, x.Status, x.ApplicationId, x.BotUserId, x.BotUsername, x.BotGlobalName, x.BotAvatarHash, x.EncryptedBotToken != null, x.LastValidatedAt, x.LastConnectedAt, x.LastReadyAt, x.LastErrorCode, x.Revision);
}
