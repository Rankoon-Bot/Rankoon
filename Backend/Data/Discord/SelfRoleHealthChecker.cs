using Discord;
using Discord.Net;
using Rankoon.Data.Model;
using System.Net;

namespace Rankoon.Data.Discord;

public sealed record SelfRoleHealthResult(SelfRolePanelState State, string? ErrorKey = null)
{
    public static SelfRoleHealthResult Healthy { get; } = new(SelfRolePanelState.Published);
    public static SelfRoleHealthResult Unknown(string reason = "unavailable") => new(SelfRolePanelState.Unknown, $"selfRoles.healthDetails.{reason}");
    public static SelfRoleHealthResult Degraded(string reason) => new(SelfRolePanelState.Degraded, $"selfRoles.healthDetails.{reason}");
}

public interface ISelfRoleHealthChecker
{
    Task<SelfRoleHealthResult> CheckAsync(IDiscordClient? client, SelfRolePanel panel, bool restoreMissingReactions, CancellationToken cancellationToken);
}

/// <summary>Checks the published panel without requiring permissions used only to edit it.</summary>
public sealed class SelfRoleHealthChecker(ILogger<SelfRoleHealthChecker> logger) : ISelfRoleHealthChecker
{
    public async Task<SelfRoleHealthResult> CheckAsync(IDiscordClient? client, SelfRolePanel panel, bool restoreMissingReactions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!panel.Enabled) return new(SelfRolePanelState.Disabled);
        if (client == null) return SelfRoleHealthResult.Unknown();
        if (panel.MessageId == 0) return SelfRoleHealthResult.Degraded("messageMissing");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var options = new RequestOptions { CancelToken = timeout.Token };
            // The caller supplies the authoritative runtime's REST client, avoiding partial gateway caches.
            var guild = await client.GetGuildAsync(panel.GuildId, CacheMode.AllowDownload, options);
            if (guild == null) return SelfRoleHealthResult.Unknown();
            SelfRoleMessageRenderer.Normalize(panel);
            if (panel.Mappings.Count == 0) return SelfRoleHealthResult.Degraded("outOfSync");
            var channel = await guild.GetTextChannelAsync(panel.ChannelId, CacheMode.AllowDownload, options);
            if (channel == null) return SelfRoleHealthResult.Degraded("channelMissing");
            var bot = await guild.GetCurrentUserAsync(CacheMode.AllowDownload, options);
            if (bot == null) return SelfRoleHealthResult.Unknown();
            if (!bot.GetPermissions(channel).ViewChannel) return SelfRoleHealthResult.Degraded("channelPermissions");
            if (!bot.GuildPermissions.ManageRoles) return SelfRoleHealthResult.Degraded("rolePermissions");
            foreach (var mapping in panel.Mappings)
            {
                var role = guild.GetRole(mapping.RoleId) ?? await guild.GetRoleAsync(mapping.RoleId, options);
                if (role == null || role.IsManaged || role.Id == guild.Id || role.Position >= bot.Hierarchy)
                    return SelfRoleHealthResult.Degraded("rolePermissions");
            }

            if (await channel.GetMessageAsync(panel.MessageId, CacheMode.AllowDownload, options) is not IUserMessage message)
                return SelfRoleHealthResult.Degraded("messageMissing");
            if (!EmbedsMatch(message.Embeds, SelfRoleMessageRenderer.BuildEmbeds(panel)))
                return SelfRoleHealthResult.Degraded("outOfSync");

            foreach (var mapping in panel.Mappings)
            {
                if (message.Reactions.Any(reaction => SelfRoleService.Matches(mapping.Emoji, reaction.Key) && reaction.Value.ReactionCount > 0)) continue;
                if (!restoreMissingReactions) return SelfRoleHealthResult.Degraded("reactionsMissing");
                await message.AddReactionAsync(SelfRoleMessageRenderer.ToEmote(mapping.Emoji), options);
            }
            return SelfRoleHealthResult.Healthy;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound && exception.DiscordCode == DiscordErrorCode.UnknownMessage)
        {
            return SelfRoleHealthResult.Degraded("messageMissing");
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound && exception.DiscordCode == DiscordErrorCode.UnknownChannel)
        {
            return SelfRoleHealthResult.Degraded("channelMissing");
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound && exception.DiscordCode == DiscordErrorCode.UnknownRole)
        {
            return SelfRoleHealthResult.Degraded("rolePermissions");
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.Forbidden)
        {
            // Missing history access can prevent inspection while gateway role assignment still works.
            return SelfRoleHealthResult.Unknown("checkPermissions");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not verify self-role panel {PanelId}", panel.Id);
            return SelfRoleHealthResult.Unknown();
        }
    }

    private static bool EmbedsMatch(IReadOnlyCollection<IEmbed> actual, IReadOnlyCollection<Embed> expected) =>
        actual.Count == expected.Count && actual.Zip(expected).All(pair =>
            pair.First.Title == pair.Second.Title && pair.First.Description == pair.Second.Description && pair.First.Color == pair.Second.Color &&
            pair.First.Fields.SequenceEqual(pair.Second.Fields));
}
