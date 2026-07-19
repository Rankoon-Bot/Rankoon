using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using Rankoon.Data.Auth;
using Rankoon.Data.Xp;
using Rankoon.Data.Reporting;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;

namespace Rankoon.Data.Discord;

public sealed class RankoonCommandService(DiscordShardedClient client, IXpService xp, VcHubService hubs, IReportWriter reports, IOptions<DiscordSettings> settings, IHttpClientFactory httpClientFactory, ILogger<RankoonCommandService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) { client.InteractionCreated += OnInteractionAsync; client.ShardReady += RegisterAsync; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { client.InteractionCreated -= OnInteractionAsync; client.ShardReady -= RegisterAsync; return Task.CompletedTask; }
    private async Task RegisterAsync(DiscordSocketClient _)
    {
        try
        {
            if (!ulong.TryParse(settings.Value.ClientId, out var applicationId))
            {
                logger.LogError("Discord commands were not synchronized because Discord:ClientId is not a valid application ID");
                return;
            }

            var commands = new object[]
            {
                new { name = "rank", description = "Zeigt deinen Rankoon-Rang", type = 1 },
                new { name = "leaderboard", description = "Zeigt die Rankoon-Rangliste", type = 1 },
                new
                {
                    name = "voice",
                    description = "Verwaltet deinen temporaeren Voice-Kanal",
                    type = 1,
                    options = new object[]
                    {
                        new { name = "action", type = 3, description = "name, limit, kick oder transfer", required = true, choices = new object[]
                        {
                            new { name = "name", value = "name" },
                            new { name = "limit", value = "limit" },
                            new { name = "kick", value = "kick" },
                            new { name = "transfer", value = "transfer" }
                        } },
                        new { name = "value", type = 3, description = "Name oder Limit", required = false },
                        new { name = "member", type = 6, description = "Mitglied fuer kick oder transfer", required = false }
                    }
                }
            };

            // Discord.Net 3.18 builds an invalid application-command path for this client.
            using var request = new HttpRequestMessage(HttpMethod.Put, $"https://discord.com/api/v10/applications/{applicationId}/commands")
            {
                Content = JsonContent.Create(commands)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.Value.BotToken);
            using var response = await httpClientFactory.CreateClient().SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                logger.LogError("Unable to synchronize global Rankoon slash commands (HTTP {StatusCode}): {ResponseBody}", (int)response.StatusCode, responseBody);
                return;
            }

            logger.LogInformation("Synchronized {CommandCount} global Rankoon slash commands", commands.Length);
        }
        catch (Exception exception) { logger.LogError(exception, "Unable to synchronize global Rankoon slash commands"); }
    }
    private async Task OnInteractionAsync(SocketInteraction interaction)
    {
        if (interaction is not SocketSlashCommand command || command.GuildId is not ulong guildId) return;
        var stopwatch = Stopwatch.StartNew();
        var action = command.Data.Options.FirstOrDefault(x => x.Name == "action")?.Value?.ToString()?.ToLowerInvariant();
        try
        {
            var outcome = await HandleInteractionAsync(command);
            await reports.WriteAsync(new(guildId, ReportCategories.Command, command.Data.Name.ToLowerInvariant(), outcome, action, command.User.Id, stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["command"] = command.Data.Name }, ChannelId: command.ChannelId, CorrelationId: command.Id.ToString()));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Discord interaction {InteractionId} failed", interaction.Id);
            await reports.WriteAsync(new(guildId, ReportCategories.Command, command.Data.Name.ToLowerInvariant(), ReportOutcomes.Failed, action, command.User.Id, stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["command"] = command.Data.Name }, ChannelId: command.ChannelId, CorrelationId: command.Id.ToString()));
            await reports.WriteErrorAsync(guildId, "discord.command", exception, command.User.Id, new Dictionary<string, object?> { ["command"] = command.Data.Name, ["eventId"] = command.Id, ["channelId"] = command.ChannelId });
        }
    }

    private async Task<string> HandleInteractionAsync(SocketSlashCommand command)
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
        try
        {
            switch (action)
            {
                case "name" when !string.IsNullOrWhiteSpace(value): await channel.ModifyAsync(x => x.Name = value[..Math.Min(value.Length, 100)]); break;
                case "limit" when int.TryParse(value, out var limit): await channel.ModifyAsync(x => x.UserLimit = Math.Clamp(limit, 0, 99)); break;
                case "kick" when target != null: await target.ModifyAsync(x => x.Channel = null); break;
                case "transfer" when target != null: await hubs.TransferOwnershipAsync(guildId, channel.Id, member.Id, target.Id); break;
                default: await command.FollowupAsync("Nutze `name`, `limit`, `kick` oder `transfer` mit den benoetigten Optionen.", ephemeral: true); return ReportOutcomes.Rejected;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Voice command {Action} failed for channel {ChannelId}", action, channel.Id);
            await command.FollowupAsync("Der Voice-Kanal konnte nicht aktualisiert werden. Pruefe, ob Rankoon die Berechtigung `Kanaele verwalten` besitzt.", ephemeral: true);
            throw;
        }
        await command.FollowupAsync("Voice-Kanal aktualisiert.", ephemeral: true);
        return ReportOutcomes.Succeeded;
    }
}
