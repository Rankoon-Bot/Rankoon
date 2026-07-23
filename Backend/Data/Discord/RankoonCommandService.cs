using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Auth;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;
using Rankoon.Data.Xp;

namespace Rankoon.Data.Discord;

public sealed class RankoonCommandSchemaProvider
{
    public const string Version = "1";

    public IReadOnlyList<ApplicationCommandProperties> GetCommands() =>
    [
        new SlashCommandBuilder().WithName("rank").WithDescription("Zeigt deinen Rankoon-Rang").Build(),
        new SlashCommandBuilder().WithName("leaderboard").WithDescription("Zeigt die Rankoon-Rangliste").Build(),
        new SlashCommandBuilder().WithName("voice").WithDescription("Verwaltet deinen temporaeren Voice-Kanal")
            .AddOption(new SlashCommandOptionBuilder().WithName("action").WithDescription("name, limit, kick oder transfer").WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .AddChoice("name", "name").AddChoice("limit", "limit").AddChoice("kick", "kick").AddChoice("transfer", "transfer"))
            .AddOption("value", ApplicationCommandOptionType.String, "Name oder Limit")
            .AddOption("member", ApplicationCommandOptionType.User, "Mitglied fuer kick oder transfer")
            .Build()
    ];

    public object[] GetRestPayload() =>
    [
        new { name = "rank", description = "Zeigt deinen Rankoon-Rang", type = 1 },
        new { name = "leaderboard", description = "Zeigt die Rankoon-Rangliste", type = 1 },
        new { name = "voice", description = "Verwaltet deinen temporaeren Voice-Kanal", type = 1, options = new object[]
        {
            new { name = "action", type = 3, description = "name, limit, kick oder transfer", required = true, choices = new object[] { new { name = "name", value = "name" }, new { name = "limit", value = "limit" }, new { name = "kick", value = "kick" }, new { name = "transfer", value = "transfer" } } },
            new { name = "value", type = 3, description = "Name oder Limit", required = false },
            new { name = "member", type = 6, description = "Mitglied fuer kick oder transfer", required = false }
        }}
    ];
}

public sealed class ApplicationCommandRegistrar(RankoonCommandSchemaProvider schema, IOptions<DiscordSettings> settings, IHttpClientFactory clients, RankoonDbContext database, TimeProvider timeProvider, ILogger<ApplicationCommandRegistrar> logger)
{
    private int platformRegistered;

    public async Task<bool> RegisterAsync(BotRuntimeContext runtime, CancellationToken cancellationToken = default)
    {
        try
        {
            if (runtime.Mode == Data.Model.BotIdentityMode.Rankoon)
            {
                if (Interlocked.Exchange(ref platformRegistered, 1) == 1) return true;
                if (!ulong.TryParse(settings.Value.ClientId, out var applicationId)) return false;
                using var request = new HttpRequestMessage(HttpMethod.Put, $"https://discord.com/api/v10/applications/{applicationId}/commands") { Content = JsonContent.Create(schema.GetRestPayload()) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.Value.BotToken);
                using var response = await clients.CreateClient().SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) { Interlocked.Exchange(ref platformRegistered, 0); logger.LogError("Unable to synchronize platform commands (HTTP {StatusCode})", (int)response.StatusCode); return false; }
                return true;
            }

            var identityId = runtime.RuntimeId["custom:".Length..];
            var identity = await database.GuildBotIdentities.Find(x => x.Id == identityId).FirstOrDefaultAsync(cancellationToken);
            if (identity?.CommandSchemaVersion == RankoonCommandSchemaProvider.Version) return true;
            await runtime.Guild.BulkOverwriteApplicationCommandAsync(schema.GetCommands().ToArray(), new RequestOptions { CancelToken = cancellationToken });
            await database.GuildBotIdentities.UpdateOneAsync(x => x.Id == identityId,
                Builders<Data.Model.GuildBotIdentity>.Update.Set(x => x.CommandSchemaVersion, RankoonCommandSchemaProvider.Version).Set(x => x.UpdatedAt, timeProvider.GetUtcNow().UtcDateTime).Inc(x => x.Revision, 1), cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Command registration failed for runtime {RuntimeId} guild {GuildId}", runtime.RuntimeId, runtime.Guild.Id);
            return false;
        }
    }
}

public sealed class RankoonInteractionHandler(IXpService xp, VcHubService hubs, IReportWriter reports, ILogger<RankoonInteractionHandler> logger)
{
    public async Task HandleAsync(SocketInteraction interaction)
    {
        if (interaction is not SocketSlashCommand command || command.GuildId is not ulong guildId) return;
        var stopwatch = Stopwatch.StartNew();
        var action = command.Data.Options.FirstOrDefault(x => x.Name == "action")?.Value?.ToString()?.ToLowerInvariant();
        try
        {
            var outcome = await HandleCommandAsync(command);
            await reports.WriteAsync(new(guildId, ReportCategories.Command, command.Data.Name.ToLowerInvariant(), outcome, action, command.User.Id, stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["command"] = command.Data.Name }, ChannelId: command.ChannelId, CorrelationId: command.Id.ToString()));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Discord interaction {InteractionId} failed", interaction.Id);
            await reports.WriteErrorAsync(guildId, "discord.command", exception, command.User.Id, new Dictionary<string, object?> { ["command"] = command.Data.Name, ["eventId"] = command.Id, ["channelId"] = command.ChannelId });
        }
    }

    private async Task<string> HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.GuildId is not ulong guildId || command.User is not SocketGuildUser member) return ReportOutcomes.Rejected;
        if (command.Data.Name == "rank") { await SendRankAsync(command, guildId, member); return ReportOutcomes.Succeeded; }
        if (command.Data.Name == "leaderboard") { var entries = await xp.GetLeaderboardAsync(guildId, 10); await command.RespondAsync(string.Join("\n", entries.Select((entry, index) => $"**{index + 1}.** {entry.DisplayName} - Level {Mee6LevelCurve.GetLevel(entry.ImportedMee6Xp + entry.EarnedXp + entry.ManualAdjustment)} ({entry.ImportedMee6Xp + entry.EarnedXp + entry.ManualAdjustment:0} XP)")), ephemeral: true); return ReportOutcomes.Succeeded; }
        if (command.Data.Name == "voice") return await HandleVoiceAsync(command, guildId, member);
        return ReportOutcomes.Rejected;
    }

    private async Task SendRankAsync(SocketSlashCommand command, ulong guildId, SocketGuildUser member)
    {
        var rank = await xp.GetMemberAsync(guildId, member.Id);
        if (rank == null) { await command.RespondAsync("Du hast noch keine XP gesammelt.", ephemeral: true); return; }
        var total = rank.ImportedMee6Xp + rank.EarnedXp + rank.ManualAdjustment;
        var level = Mee6LevelCurve.GetLevel(total);
        await command.RespondAsync($"**{member.DisplayName}** ist Level **{level}** mit **{total:0} XP**. Naechste Stufe: {Mee6LevelCurve.RequiredXpForLevel(level + 1)} XP.", ephemeral: true);
    }

    private async Task<string> HandleVoiceAsync(SocketSlashCommand command, ulong guildId, SocketGuildUser member)
    {
        var channel = member.VoiceChannel;
        if (channel == null || !await hubs.IsOwnerAsync(guildId, channel.Id, member.Id)) { await command.RespondAsync("Du musst Owner eines von Rankoon erstellten Voice-Kanals sein.", ephemeral: true); return ReportOutcomes.Rejected; }
        var action = command.Data.Options.First(x => x.Name == "action").Value?.ToString()?.ToLowerInvariant();
        var value = command.Data.Options.FirstOrDefault(x => x.Name == "value")?.Value?.ToString();
        var target = command.Data.Options.FirstOrDefault(x => x.Name == "member")?.Value as SocketGuildUser;
        await command.DeferAsync(ephemeral: true);
        switch (action)
        {
            case "name" when !string.IsNullOrWhiteSpace(value): await channel.ModifyAsync(x => x.Name = value[..Math.Min(value.Length, 100)]); break;
            case "limit" when int.TryParse(value, out var limit): await channel.ModifyAsync(x => x.UserLimit = Math.Clamp(limit, 0, 99)); break;
            case "kick" when target != null: await target.ModifyAsync(x => x.Channel = null); break;
            case "transfer" when target != null: await hubs.TransferOwnershipAsync(guildId, channel.Id, member.Id, target.Id); break;
            default: await command.FollowupAsync("Nutze `name`, `limit`, `kick` oder `transfer` mit den benoetigten Optionen.", ephemeral: true); return ReportOutcomes.Rejected;
        }
        await command.FollowupAsync("Voice-Kanal aktualisiert.", ephemeral: true);
        return ReportOutcomes.Succeeded;
    }
}
