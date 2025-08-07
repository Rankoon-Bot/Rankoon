using System.Security.Claims;
using Discord.WebSocket;
using MongoDB.Driver;
using Rankoon.Data.Model;
using Rankoon.Data.MongoDb;

namespace Rankoon.Data.Auth;

public interface IGuildAuthorizationService { Task<bool> CanManageAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default); }

public sealed class GuildAuthorizationService(RankoonDbContext database, DiscordShardedClient discord) : IGuildAuthorizationService
{
    public async Task<bool> CanManageAsync(ClaimsPrincipal user, ulong guildId, CancellationToken cancellationToken = default)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id == null) return false;
        var account = await database.DiscordUsers.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
        if (account == null || !ulong.TryParse(account.DiscordId, out var discordUserId)) return false;
        var guild = discord.GetGuild(guildId);
        var member = guild?.GetUser(discordUserId);
        return guild != null && member != null && (guild.OwnerId == discordUserId || member.GuildPermissions.Administrator || member.GuildPermissions.ManageGuild);
    }
}
