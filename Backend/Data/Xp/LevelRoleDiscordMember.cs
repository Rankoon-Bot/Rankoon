using System.Net;
using Discord;
using Discord.Net;
using Rankoon.Data.Discord;

namespace Rankoon.Data.Xp;

internal static class LevelRoleDiscordMember
{
    internal static async Task<IGuildUser?> FetchAsync(GuildDiscordContext context, ulong userId, CancellationToken ct)
    {
        try { return await context.Client.Rest.GetGuildUserAsync(context.Guild.Id, userId, new RequestOptions { CancelToken = ct }); }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound) { return null; }
    }
}
