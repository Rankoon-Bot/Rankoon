using System.Globalization;
using System.Text;
using Discord;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;

namespace Rankoon.Data.Discord;

/// <summary>Compact, native Discord cards. Colors mirror the Rankoon theme tokens.</summary>
public static class RankoonCommandPresentation
{
    public static readonly Color Brand = new(0xef3e3a);
    public static readonly Color Info = new(0x4c9dff);
    public static readonly Color Success = new(0x36c875);
    public static readonly Color Warning = new(0xf5aa3f);
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static EmbedBuilder Card(string title, string description, Color color) => new EmbedBuilder()
        .WithAuthor("RANKOON")
        .WithTitle(title)
        .WithDescription(description)
        .WithColor(color)
        .WithFooter("Rankoon • Nur für dich sichtbar • /help für alle Befehle");

    public static MessageComponent Links() => new ComponentBuilder()
        .WithButton("Dashboard öffnen", style: ButtonStyle.Link, url: "https://rankoon.fun/dashboard")
        .Build();

    public static Embed Help() => Card("Dein Server. Dein nächstes Level.",
            "Starte mit **`/rank`** und sieh, wie weit du vom nächsten Level entfernt bist.", Brand)
        .AddField("◈ Dein Fortschritt", "`/rank` · Level, XP und Fortschritt\n`/leaderboard` · Die Top 10 dieses Servers")
        .AddField("◇ Dein Voice-Kanal", "`/voice status` · Kanal und Einstellungen ansehen\n" +
            "`/voice name value:Gaming Lounge` · Kanal umbenennen\n" +
            "`/voice limit value:5` · Plätze festlegen; `0` = unbegrenzt\n" +
            "`/voice kick member:@Mitglied` · Mitglied aus deinem Kanal trennen\n" +
            "`/voice transfer member:@Mitglied` · Besitz und Kontrolle abgeben")
        .AddField("So funktioniert Voice", "Betritt einen eingerichteten Rankoon-Voice-Hub. Deinen persönlichen Kanal verwaltest du, solange du darin bist und ihn besitzt. Das Zielmitglied muss für Kick und Übergabe im selben Kanal sein.")
        .AddField("Für die Serververwaltung", "XP-Regeln und Voice-Hubs richtest du im Dashboard ein.")
        .Build();

    public static Embed Rank(string name, string? avatarUrl, MemberXp? rank)
    {
        var total = rank == null ? 0 : rank.ImportedMee6Xp + rank.EarnedXp + rank.ManualAdjustment;
        var level = Mee6LevelCurve.GetLevel(total);
        var floor = level == 0 ? 0 : Mee6LevelCurve.RequiredXpForLevel(level);
        var next = Mee6LevelCurve.RequiredXpForLevel(level + 1);
        var progress = Math.Clamp((total - floor) / (next - floor), 0, 1);
        var filled = (int)decimal.Floor(progress * 10);
        var bar = new string('▰', filled) + new string('▱', 10 - filled);
        var card = Card("Dein nächstes Level wartet.", $"**{Text(name, 80)}**\n" +
            (total <= 0 ? "Deine Reise beginnt hier. Sammle XP durch die auf diesem Server aktivierten Aktivitäten." : "Jeder Schritt zählt. Hier steht dein Fortschritt."), Brand)
            .AddField("Level", $"**{level}**", true)
            .AddField("Gesammelte XP", $"**{Number(total)}**", true)
            .AddField($"Nächstes Ziel · Level {level + 1}", $"{bar} **{decimal.Floor(progress * 100)} %**\nNoch **{Number(decimal.Ceiling(Math.Max(0, next - total)))} XP** · Ziel: {Number(next)} XP")
            .AddField("Weiter geht’s", "Mit `/leaderboard` siehst du die Top 10 deines Servers.");
        if (!string.IsNullOrWhiteSpace(avatarUrl)) card.WithThumbnailUrl(avatarUrl);
        return card.Build();
    }

    public static Embed Leaderboard(string guildName, IReadOnlyList<MemberXp> entries, ulong viewerId)
    {
        var card = Card("Die Top 10", $"**{Text(guildName, 80)}** · Server-Rangliste", Info);
        if (entries.Count == 0)
            return card.AddField("Die Bühne ist noch frei", "Hier wurden noch keine XP gesammelt. Mit `/rank` siehst du deinen Startpunkt. Die Serververwaltung legt im Dashboard fest, welche Aktivitäten XP geben.").Build();

        var rows = entries.Take(10).Select((entry, index) =>
        {
            var xp = entry.ImportedMee6Xp + entry.EarnedXp + entry.ManualAdjustment;
            var place = index switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"`{index + 1:00}`" };
            return $"{place} **{Text(entry.DisplayName, 48)}**{(entry.UserId == viewerId ? " · du" : "")}\n" +
                $"Level **{Mee6LevelCurve.GetLevel(xp)}** · {Number(xp)} XP";
        });
        return card.WithDescription(card.Description + "\n\n" + string.Join("\n\n", rows))
            .AddField("Dein Fortschritt", "`/rank` zeigt deine XP und den Weg zum nächsten Level.").Build();
    }

    public static Embed VoiceStatus(string name, ulong channelId, ulong ownerId, int limit, int members) =>
        Card("Dein Voice-Kanal", $"**{Text(name, 100)}** · <#{channelId}>", Info)
            .AddField("Besitzer", $"<@{ownerId}>", true)
            .AddField("Plätze", limit == 0 ? $"{members} · unbegrenzt" : $"{members} / {limit}", true)
            .AddField("Nächster Schritt", "`/voice name` zum Umbenennen · `/voice limit` für die Plätze\n`/voice kick` trennt ein Mitglied · `/voice transfer` gibt deine Kontrolle ab.")
            .Build();

    public static Embed Done(string title, string description) => Card($"✓ {title}", description, Success).Build();
    public static Embed Error(string title, string recovery) => Card($"! {title}", recovery, Warning).Build();
    public static string Number(decimal value) => value.ToString("N0", German);

    // User-controlled labels must never become links, mentions, or Markdown structure.
    public static string Text(string? value, int maxLength)
    {
        var clean = string.Concat((value ?? "").Where(c => !char.IsControl(c))).Trim();
        if (clean.Length == 0) clean = "Unbekannt";
        if (clean.Length > maxLength) clean = clean[..(maxLength - 1)] + "…";
        var result = new StringBuilder();
        foreach (var c in clean)
        {
            if ("\\`*_~|[]()<>#".Contains(c)) result.Append('\\');
            result.Append(c == '@' ? "＠" : c.ToString());
        }
        return result.ToString();
    }
}
