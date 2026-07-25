using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public sealed record VoiceActivityReadItem(ulong UserId, DateTime OccurredAtUtc, decimal AwardedXp, long EligibleSeconds, ulong ChannelId);

public static class VoiceActivityReadModel
{
    public static IReadOnlyList<VoiceActivityReadItem> Select(IEnumerable<VoiceActivityDay> days, bool compressedVoiceAuthoritative, DateTime fromUtc, DateTime toUtc) =>
        days.SelectMany(day => day.Segments
            .Where(segment => compressedVoiceAuthoritative || segment.SettingsRevision != VoiceLedgerMigrationService.LegacySettingsRevision)
            .Where(segment => segment.StartsAtUtc >= fromUtc && segment.StartsAtUtc < toUtc)
            .Select(segment => new VoiceActivityReadItem(day.UserId, segment.StartsAtUtc, segment.AwardedXp, segment.EligibleSeconds, segment.ChannelId)))
            .ToArray();
}
