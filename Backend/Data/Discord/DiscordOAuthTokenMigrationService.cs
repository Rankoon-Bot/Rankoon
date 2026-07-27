using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Discord;

/// <summary>Converts legacy plaintext Discord OAuth fields in small, independently atomic batches.</summary>
public sealed class DiscordOAuthTokenMigrationService(RankoonDbContext database, IDiscordOAuthTokenProtector tokens) : BackgroundService
{
    public const int DefaultBatchSize = 100;
    public const int MaximumBatchSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var migrated = await MigrateBatchAsync(cancellationToken: stoppingToken);
            if (migrated == 0) return;
        }
    }

    public async Task<int> MigrateBatchAsync(int batchSize = DefaultBatchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var legacy = Builders<DiscordUser>.Filter.Eq(user => user.OAuthTokenProtectionVersion, null);
        var users = await database.DiscordUsers.Find(legacy).Limit(batchSize).ToListAsync(cancellationToken);
        var migrated = 0;
        foreach (var user in users)
        {
            if (!ProtectLegacyUserTokens(user, tokens)) continue;
            var update = Builders<DiscordUser>.Update
                .Set(x => x.ProtectedAccessToken, user.ProtectedAccessToken)
                .Set(x => x.ProtectedRefreshToken, user.ProtectedRefreshToken)
                .Set(x => x.OAuthTokenProtectionVersion, user.OAuthTokenProtectionVersion);
            var result = await database.DiscordUsers.UpdateOneAsync(
                Builders<DiscordUser>.Filter.And(
                    Builders<DiscordUser>.Filter.Eq(x => x.Id, user.Id),
                    legacy),
                update,
                cancellationToken: cancellationToken);
            migrated += (int)result.ModifiedCount;
        }
        return migrated;
    }

    /// <summary>Prepares a legacy document for its atomic replacement. Calling it again is a no-op.</summary>
    public static bool ProtectLegacyUserTokens(DiscordUser user, IDiscordOAuthTokenProtector tokens)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tokens);
        if (user.OAuthTokenProtectionVersion is not null) return false;
        if (!string.IsNullOrEmpty(user.ProtectedAccessToken)) user.ProtectedAccessToken = tokens.ProtectAccessToken(user.ProtectedAccessToken);
        if (!string.IsNullOrEmpty(user.ProtectedRefreshToken)) user.ProtectedRefreshToken = tokens.ProtectRefreshToken(user.ProtectedRefreshToken);
        user.OAuthTokenProtectionVersion = DiscordOAuthTokenProtector.CurrentVersion;
        return true;
    }
}
