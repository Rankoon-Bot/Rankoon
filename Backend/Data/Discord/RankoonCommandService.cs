using System.Diagnostics;
using System.Collections.Concurrent;
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
using Rankoon.Data.Operations;

namespace Rankoon.Data.Discord;

public sealed class RankoonCommandSchemaProvider
{
    public const string Version = "3";

    public IReadOnlyList<ApplicationCommandProperties> GetCommands() =>
    [
        new SlashCommandBuilder().WithName("help").WithDescription("Zeigt die Rankoon-Befehle und ihre Anwendung").Build(),
        new SlashCommandBuilder().WithName("rank").WithDescription("Zeigt deinen Rankoon-Rang").Build(),
        new SlashCommandBuilder().WithName("leaderboard").WithDescription("Zeigt die Rankoon-Rangliste").Build(),
        new SlashCommandBuilder().WithName("voice").WithDescription("Verwalte deinen persönlichen Voice-Kanal")
            .AddOption(VoiceOption("status", "Zeigt deinen Kanal, den Besitzer und die verfügbaren Plätze"))
            .AddOption(VoiceOption("name", "Benennt deinen Kanal um", new SlashCommandOptionBuilder()
                .WithName("value").WithDescription("Neuer Kanalname, zum Beispiel Gaming Lounge (1–100 Zeichen)").WithType(ApplicationCommandOptionType.String).WithMinLength(1).WithMaxLength(100).WithRequired(true)))
            .AddOption(VoiceOption("limit", "Legt die Plätze in deinem Kanal fest", new SlashCommandOptionBuilder()
                .WithName("value").WithDescription("0 = unbegrenzt, sonst 1–99 Plätze").WithType(ApplicationCommandOptionType.Integer).WithMinValue(0).WithMaxValue(99).WithRequired(true)))
            .AddOption(VoiceOption("kick", "Trennt ein Mitglied von deinem Kanal", MemberOption()))
            .AddOption(VoiceOption("transfer", "Übergibt den Kanal; du gibst damit die Kontrolle ab", MemberOption()))
            .Build()
    ];

    private static SlashCommandOptionBuilder VoiceOption(string name, string description, SlashCommandOptionBuilder? option = null)
    {
        var builder = new SlashCommandOptionBuilder().WithName(name).WithDescription(description).WithType(ApplicationCommandOptionType.SubCommand);
        return option == null ? builder : builder.AddOption(option);
    }

    private static SlashCommandOptionBuilder MemberOption() => new SlashCommandOptionBuilder().WithName("member")
        .WithDescription("Mitglied in deinem Voice-Kanal").WithType(ApplicationCommandOptionType.User).WithRequired(true);

    // Both platform and custom bots register the same schema.
    public object[] GetRestPayload() => GetCommands().Cast<SlashCommandProperties>().Select(command => (object)new
    {
        name = command.Name.Value, description = command.Description.Value, type = 1,
        contexts = new[] { (int)InteractionContextType.Guild },
        options = command.Options.IsSpecified ? command.Options.Value.Select(RestOption).ToArray() : []
    }).ToArray();

    private static object RestOption(ApplicationCommandOptionProperties option)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = option.Name, ["description"] = option.Description, ["type"] = (int)option.Type
        };
        if (option.IsRequired.HasValue) payload["required"] = option.IsRequired.Value;
        if (option.MinValue.HasValue) payload["min_value"] = option.MinValue.Value;
        if (option.MaxValue.HasValue) payload["max_value"] = option.MaxValue.Value;
        if (option.MinLength.HasValue) payload["min_length"] = option.MinLength.Value;
        if (option.MaxLength.HasValue) payload["max_length"] = option.MaxLength.Value;
        if (option.Options is { Count: > 0 }) payload["options"] = option.Options.Select(RestOption).ToArray();
        return payload;
    }
}

public sealed class ApplicationCommandRegistrar(RankoonCommandSchemaProvider schema, IOptions<DiscordSettings> settings, IHttpClientFactory clients, RankoonDbContext database, IOperationalErrorRecorder errors, TimeProvider timeProvider, ILogger<ApplicationCommandRegistrar> logger)
{
    private int platformRegistered;
    private readonly SemaphoreSlim platformRegistrationGate = new(1, 1);

    public async Task<bool> RegisterAsync(BotRuntimeContext runtime, CancellationToken cancellationToken = default)
    {
        try
        {
            if (runtime.Mode == Data.Model.BotIdentityMode.Rankoon)
            {
                await platformRegistrationGate.WaitAsync(cancellationToken);
                try
                {
                    if (platformRegistered == 1) return true;
                    if (!ulong.TryParse(settings.Value.ClientId, out var applicationId)) return false;
                    using var request = new HttpRequestMessage(HttpMethod.Put, $"https://discord.com/api/v10/applications/{applicationId}/commands") { Content = JsonContent.Create(schema.GetRestPayload()) };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.Value.BotToken);
                    using var response = await clients.CreateClient().SendAsync(request, cancellationToken);
                    if (!response.IsSuccessStatusCode) { logger.LogError("Unable to synchronize platform commands (HTTP {StatusCode})", (int)response.StatusCode); return false; }
                    platformRegistered = 1;
                    return true;
                }
                finally { platformRegistrationGate.Release(); }
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
            await errors.RecordAsync(new(exception, "discord", "command.registration", GuildId: runtime.Guild.Id, Build: RankoonCommandSchemaProvider.Version), cancellationToken);
            return false;
        }
    }
}

public sealed class RankoonInteractionHandler(IXpService xp, VcHubService hubs, IReportWriter reports, Rankoon.Data.Operations.IOperationalErrorRecorder errors, ILogger<RankoonInteractionHandler> logger)
{
    private readonly ConcurrentDictionary<ulong, byte> activeVoiceChannels = new();

    public async Task HandleAsync(SocketInteraction interaction)
    {
        if (interaction is not SocketSlashCommand command || command.GuildId is not ulong guildId) return;
        var stopwatch = Stopwatch.StartNew();
        var subcommand = command.Data.Options.FirstOrDefault(x => x.Type == ApplicationCommandOptionType.SubCommand);
        // Old globally registered commands can remain visible briefly during schema propagation.
        var action = subcommand?.Name ?? command.Data.Options.FirstOrDefault(x => x.Name == "action")?.Value?.ToString()?.ToLowerInvariant();
        var outcome = ReportOutcomes.Failed;
        var acknowledged = false;
        try
        {
            await command.DeferAsync(ephemeral: true);
            acknowledged = true;
            outcome = await HandleCommandAsync(command, guildId, action, subcommand?.Options ?? command.Data.Options);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Discord interaction {InteractionId} failed", interaction.Id);
            try
            {
                var permissionError = exception is global::Discord.Net.HttpException http && http.HttpCode == System.Net.HttpStatusCode.Forbidden;
                var embed = RankoonCommandPresentation.Error(permissionError ? "Discord-Berechtigung fehlt" : "Das hat gerade nicht geklappt",
                    permissionError
                        ? "Bitte die Serververwaltung, Rankoons Berechtigungen für diesen Kanal zu prüfen. Danach kannst du den Befehl erneut ausführen."
                        : "Prüfe zuerst den aktuellen Stand mit `/rank`, `/leaderboard` oder `/voice status`. Falls die Änderung noch fehlt, führe deinen Befehl erneut aus.");
                if (acknowledged) await ReplyAsync(command, embed);
                else if (!command.HasResponded) await command.RespondAsync(embed: embed, ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            catch (Exception responseException) { logger.LogWarning(responseException, "Unable to send recovery response for interaction {InteractionId}", command.Id); }
            try
            {
                await errors.RecordAsync(new(exception, "discord", "command", GuildId: guildId, ActorUserId: command.User.Id, ChannelId: command.ChannelId, Command: command.Data.Name, CorrelationId: command.Id.ToString()));
            }
            catch (Exception recordingException) { logger.LogWarning(recordingException, "Unable to record interaction error {InteractionId}", command.Id); }
        }

        try
        {
            await reports.WriteAsync(new(guildId, ReportCategories.Command, command.Data.Name.ToLowerInvariant(), outcome, action, command.User.Id, stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["command"] = command.Data.Name }, ChannelId: command.ChannelId, CorrelationId: command.Id.ToString()));
        }
        catch (Exception exception) { logger.LogWarning(exception, "Unable to record command report {InteractionId}", command.Id); }
    }

    private static Task ReplyAsync(SocketSlashCommand command, Embed embed, bool showLinks = false) => command.ModifyOriginalResponseAsync(message =>
    {
        message.Embed = embed;
        message.AllowedMentions = AllowedMentions.None;
        message.Components = showLinks ? RankoonCommandPresentation.Links() : new ComponentBuilder().Build();
    });

    private async Task<string> HandleCommandAsync(SocketSlashCommand command, ulong guildId, string? action, IReadOnlyCollection<SocketSlashCommandDataOption> options)
    {
        if (command.User is not SocketGuildUser member)
            return await RejectAsync(command, "Nur auf einem Server verfügbar", "Führe diesen Befehl auf deinem Discord-Server aus.");
        switch (command.Data.Name)
        {
            case "help":
                await ReplyAsync(command, RankoonCommandPresentation.Help(), showLinks: true);
                break;
            case "rank":
                var rank = await xp.GetMemberAsync(guildId, member.Id);
                await ReplyAsync(command, RankoonCommandPresentation.Rank(member.DisplayName, member.GetDisplayAvatarUrl(), rank));
                break;
            case "leaderboard":
                var entries = await xp.GetLeaderboardAsync(guildId, 10);
                await ReplyAsync(command, RankoonCommandPresentation.Leaderboard(member.Guild.Name, entries, member.Id));
                break;
            case "voice":
                return await HandleVoiceAsync(command, guildId, member, action, options);
            default:
                return await RejectAsync(command, "Befehl nicht verfügbar", "Öffne `/help` und wähle einen der aktuellen Befehle.");
        }
        return ReportOutcomes.Succeeded;
    }

    private static async Task<string> RejectAsync(SocketSlashCommand command, string title, string recovery)
    {
        await ReplyAsync(command, RankoonCommandPresentation.Error(title, recovery));
        return ReportOutcomes.Rejected;
    }

    private async Task<string> HandleVoiceAsync(SocketSlashCommand command, ulong guildId, SocketGuildUser member, string? action, IReadOnlyCollection<SocketSlashCommandDataOption> options)
    {
        var channel = member.VoiceChannel;
        if (channel == null)
            return await RejectAsync(command, "Du bist in keinem Voice-Kanal", "Betritt einen Rankoon-Voice-Hub, um deinen persönlichen Kanal zu erhalten. Führe dort `/voice status` aus.");
        if (!activeVoiceChannels.TryAdd(channel.Id, 0))
            return await RejectAsync(command, "Eine Kanalaktion läuft bereits", "Warte auf deren Abschluss und prüfe mit `/voice status` den aktuellen Stand.");
        try
        {
            if (!await hubs.IsOwnerAsync(guildId, channel.Id, member.Id) || member.VoiceChannel?.Id != channel.Id)
                return await RejectAsync(command, "Dieser Kanal gehört dir nicht", "Voice-Befehle funktionieren in deinem eigenen Rankoon-Kanal. Betritt einen eingerichteten Voice-Hub, um einen eigenen Kanal zu erhalten.");
            var value = options.FirstOrDefault(x => x.Name == "value")?.Value?.ToString();
            var target = options.FirstOrDefault(x => x.Name == "member")?.Value as SocketGuildUser;
            var validation = RankoonVoiceCommandValidation.Validate(action, value, member.Id, target?.Id, target?.VoiceChannel?.Id == channel.Id);
            if (validation != null) return await RejectAsync(command, "Bitte prüfe deine Eingabe", validation);

            Embed response;
            switch (action)
            {
                case "status":
                    response = RankoonCommandPresentation.VoiceStatus(channel.Name, channel.Id, member.Id, channel.UserLimit ?? 0, channel.ConnectedUsers.Count);
                    break;
                case "name":
                    var name = value!.Trim();
                    await channel.ModifyAsync(x => x.Name = name);
                    response = RankoonCommandPresentation.Done("Kanal umbenannt", $"<#{channel.Id}> heißt jetzt **{RankoonCommandPresentation.Text(name, 100)}**.");
                    break;
                case "limit":
                    var limit = int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture);
                    await channel.ModifyAsync(x => x.UserLimit = limit);
                    response = RankoonCommandPresentation.Done("Plätze aktualisiert", $"<#{channel.Id}> · " + (limit == 0 ? "**Unbegrenzt** viele Plätze." : $"**{limit}** Plätze.") + "\nBereits verbundene Mitglieder bleiben im Kanal.");
                    break;
                case "kick":
                    await target!.ModifyAsync(x => x.Channel = null);
                    response = RankoonCommandPresentation.Done("Mitglied getrennt", $"<@{target.Id}> wurde aus <#{channel.Id}> getrennt. Das Mitglied kann den Kanal wieder betreten.");
                    break;
                case "transfer":
                    await hubs.TransferOwnershipAsync(guildId, channel.Id, member.Id, target!.Id);
                    response = RankoonCommandPresentation.Done("Kanal übergeben", $"<@{target.Id}> besitzt jetzt <#{channel.Id}>.\nDu hast die Kontrolle abgegeben und kannst hier keine Besitzer-Aktionen mehr ausführen.");
                    break;
                default:
                    return await RejectAsync(command, "Aktion nicht verfügbar", "Nutze `/voice status`, `/voice name`, `/voice limit`, `/voice kick` oder `/voice transfer`.");
            }
            await ReplyAsync(command, response);
            return ReportOutcomes.Succeeded;
        }
        finally { activeVoiceChannels.TryRemove(channel.Id, out _); }
    }
}

public static class RankoonVoiceCommandValidation
{
    public static string? Validate(string? action, string? value, ulong actorId, ulong? targetId, bool targetInChannel)
    {
        switch (action)
        {
            case "status": return null;
            case "name":
                var name = value?.Trim();
                return string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)
                    ? "Gib einen Namen mit 1–100 Zeichen ohne Zeilenumbrüche ein, zum Beispiel `/voice name value:Gaming Lounge`." : null;
            case "limit":
                return !int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var limit) || limit is < 0 or > 99
                    ? "Wähle eine ganze Zahl von 0 bis 99, zum Beispiel `/voice limit value:5`. Mit `0` entfernst du das Limit." : null;
            case "kick":
            case "transfer":
                if (targetId == null) return $"Wähle ein Mitglied, zum Beispiel `/voice {action} member:@Mitglied`.";
                if (targetId == actorId) return action == "kick" ? "Um selbst zu gehen, verlasse deinen Voice-Kanal in Discord." : "Du besitzt diesen Kanal bereits. Wähle ein anderes Mitglied für die Übergabe.";
                return !targetInChannel ? "Wähle ein Mitglied, das gerade in deinem Voice-Kanal verbunden ist." : null;
            default: return "Wähle `/voice status`, `/voice name`, `/voice limit`, `/voice kick` oder `/voice transfer`.";
        }
    }
}
