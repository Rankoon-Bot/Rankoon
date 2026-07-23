using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Discord;

/// <summary>Restores only policy-eligible custom runtimes after the platform gateway and indexes are available.</summary>
public sealed class CustomBotIdentityHostedService(RankoonDbContext database, ICustomBotIdentityAccessPolicy policy, ICustomBotIdentityValidator validator, ApplicationCommandRegistrar commands, IBotRuntimeManager runtimes, IGuildBotAuthority authority, IOptions<CustomBotIdentityOptions> options, TimeProvider timeProvider, ILogger<CustomBotIdentityHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) return;
        var identities = await database.GuildBotIdentities.Find(x => x.Status == BotIdentityStatus.Active).ToListAsync(cancellationToken);
        using var semaphore = new SemaphoreSlim(Math.Clamp(options.Value.StartupParallelism, 1, 4));
        await Task.WhenAll(identities.Select(async identity =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var access = await policy.EvaluateAsync(identity.GuildId, cancellationToken);
                if (!access.IsEligible)
                {
                    identity.Status = BotIdentityStatus.DisabledByPolicy; identity.LastErrorCode = "customBotIdentity.guildNotAllowed"; identity.UpdatedAt = DateTime.UtcNow; identity.Revision++;
                    await database.GuildBotIdentities.ReplaceOneAsync(x => x.Id == identity.Id, identity, cancellationToken: cancellationToken);
                    await database.CustomBotCapacityReservations.DeleteOneAsync(x => x.GuildId == identity.GuildId, cancellationToken);
                    await authority.RestorePlatformAuthorityAsync(identity.GuildId);
                    return;
                }
                var started = await runtimes.StartCustomRuntimeAsync(identity.Id!, cancellationToken);
                if (!started.Succeeded || started.Context == null)
                {
                    await MarkFailedAsync(identity, started.ErrorCode ?? "customBotIdentity.runtimeStartFailed", cancellationToken);
                    return;
                }
                var validation = await validator.ValidateGuildAsync(identity.GuildId, identity.Id!, cancellationToken);
                if (!validation.IsValid || !await commands.RegisterAsync(started.Context, cancellationToken))
                {
                    await runtimes.StopCustomRuntimeAsync(identity.Id!, cancellationToken);
                    await MarkFailedAsync(identity, validation.ErrorCode ?? "customBotIdentity.commandRegistrationFailed", cancellationToken);
                    return;
                }
                await authority.SetCustomAuthorityAsync(identity.GuildId, "custom:" + identity.Id);
                var now = timeProvider.GetUtcNow().UtcDateTime;
                await database.GuildBotIdentities.UpdateOneAsync(x => x.Id == identity.Id,
                    Builders<GuildBotIdentity>.Update.Set(x => x.Status, BotIdentityStatus.Active).Set(x => x.LastErrorCode, null).Set(x => x.LastReadyAt, now).Set(x => x.UpdatedAt, now).Inc(x => x.Revision, 1), cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogWarning(exception, "Custom bot identity {IdentityId} was not restored", identity.Id); }
            finally { semaphore.Release(); }
        }));
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var identities = await database.GuildBotIdentities.Find(x => x.Mode == BotIdentityMode.Custom).ToListAsync(cancellationToken);
        foreach (var identity in identities.Where(x => x.Id != null)) await runtimes.StopCustomRuntimeAsync(identity.Id!, cancellationToken);
    }
    private async Task MarkFailedAsync(GuildBotIdentity identity, string errorCode, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await database.GuildBotIdentities.UpdateOneAsync(x => x.Id == identity.Id,
            Builders<GuildBotIdentity>.Update.Set(x => x.Status, BotIdentityStatus.Degraded).Set(x => x.LastErrorCode, errorCode).Set(x => x.LastErrorAt, now).Set(x => x.UpdatedAt, now).Inc(x => x.Revision, 1), cancellationToken: cancellationToken);
        // Keep custom authority selected: startup failures must not silently fall back to platform identity.
        await authority.SetCustomAuthorityAsync(identity.GuildId, "custom:" + identity.Id);
    }
}
