using Rankoon.Data.Model;

namespace Rankoon.Data.Xp;

public static class VoiceXpSettingsNormalizer
{
    public static void NormalizeLegacy(VoiceXpSettings voice)
    {
        if (voice.Eligibility == null && voice.SettingsVersion is null or < VoiceXpSettings.CurrentVersion)
        {
            voice.Eligibility = new VoiceXpEligibilitySettings
            {
                AwardWhileSelfMuted = true,
                AwardWhileSelfDeafened = true,
                AwardWhileGuildMuted = true,
                AwardWhileGuildDeafened = false,
                AwardWhileSuppressed = true,
                AwardInAfkChannel = !(voice.ExcludeAfkChannel ?? true),
                MinimumHumanParticipants = voice.RequireMultipleHumans == false ? 1 : 2,
                ParticipantCountingMode = VoiceXpParticipantCountingMode.EligibleHumansOnly,
                ResetMinimumSessionWhenIneligible = true
            };
        }

        if (voice.Eligibility != null)
        {
            voice.SettingsVersion = VoiceXpSettings.CurrentVersion;
            voice.RequireMultipleHumans = null;
            voice.ExcludeAfkChannel = null;
        }
    }

    public static VoiceXpSettings CloneNormalized(VoiceXpSettings? source)
    {
        source ??= new VoiceXpSettings();
        NormalizeLegacy(source);
        var eligibility = source.Eligibility;
        return new VoiceXpSettings
        {
            Enabled = source.Enabled,
            PointsPerMinute = source.PointsPerMinute,
            MinimumSessionSeconds = source.MinimumSessionSeconds,
            SettingsVersion = source.SettingsVersion,
            Eligibility = eligibility == null ? null : new VoiceXpEligibilitySettings
            {
                AwardWhileSelfMuted = eligibility.AwardWhileSelfMuted,
                AwardWhileSelfDeafened = eligibility.AwardWhileSelfDeafened,
                AwardWhileGuildMuted = eligibility.AwardWhileGuildMuted,
                AwardWhileGuildDeafened = eligibility.AwardWhileGuildDeafened,
                AwardWhileSuppressed = eligibility.AwardWhileSuppressed,
                AwardInAfkChannel = eligibility.AwardInAfkChannel,
                MinimumHumanParticipants = eligibility.MinimumHumanParticipants,
                ParticipantCountingMode = eligibility.ParticipantCountingMode,
                ResetMinimumSessionWhenIneligible = eligibility.ResetMinimumSessionWhenIneligible
            }
        };
    }
}
