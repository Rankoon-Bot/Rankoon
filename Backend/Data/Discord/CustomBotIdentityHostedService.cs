using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Discord;

/// <summary>Restores only policy-eligible custom runtimes after the platform gateway and indexes are available.</summary>
public sealed class CustomBotIdentityHostedService(RankoonDbContext database, ICustomBotIdentityAccessPolicy policy, IBotRuntimeManager runtimes, IGuildBotAuthority authority, IOptions<CustomBotIdentityOptions> options, ILogger<CustomBotIdentityHostedService> logger) : IHostedService
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
                if (started.Succeeded) await authority.SetCustomAuthorityAsync(identity.GuildId, "custom:" + identity.Id);
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
}
