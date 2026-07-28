using Rankoon.Data.Model;

namespace Rankoon.Data.Discord;

public enum VoiceXpIneligibilityReason
{
    Bot,
    ExcludedChannel,
    ExcludedCategory,
    ExcludedRole,
    AfkChannel,
    SelfMuted,
    SelfDeafened,
    GuildMuted,
    GuildDeafened,
    Suppressed,
    InsufficientParticipants,
    MinimumSessionDuration
}

public sealed record VoiceXpParticipantState(
    ulong UserId,
    bool IsBot = false,
    bool HasExcludedRole = false,
    bool IsSelfMuted = false,
    bool IsSelfDeafened = false,
    bool IsGuildMuted = false,
    bool IsGuildDeafened = false,
    bool IsSuppressed = false);

public sealed record VoiceXpEvaluationContext(
    VoiceXpParticipantState Member,
    IReadOnlyList<VoiceXpParticipantState> ConnectedParticipants,
    bool IsExcludedChannel = false,
    bool IsExcludedCategory = false,
    bool IsAfkChannel = false,
    long QualifyingSeconds = 0,
    long CurrentIntervalSeconds = 0,
    int MinimumSessionSeconds = 0,
    int? AllConnectedHumanCount = null,
    int? EligibleHumanCount = null);

public sealed record VoiceXpEligibilityResult(
    bool Qualifies,
    bool EligibleAfterMinimumDuration,
    int HumanParticipantCount,
    int RequiredHumanParticipantCount,
    IReadOnlyCollection<VoiceXpIneligibilityReason> Reasons)
{
    public VoiceXpIneligibilityReason? PrimaryReason => Reasons.FirstOrDefault() is var reason && Reasons.Count > 0 ? reason : null;
}

public sealed class VoiceXpEligibilityEvaluator
{
    private static readonly VoiceXpIneligibilityReason[] Priority =
    [
        VoiceXpIneligibilityReason.Bot,
        VoiceXpIneligibilityReason.ExcludedChannel,
        VoiceXpIneligibilityReason.ExcludedCategory,
        VoiceXpIneligibilityReason.ExcludedRole,
        VoiceXpIneligibilityReason.AfkChannel,
        VoiceXpIneligibilityReason.SelfMuted,
        VoiceXpIneligibilityReason.SelfDeafened,
        VoiceXpIneligibilityReason.GuildMuted,
        VoiceXpIneligibilityReason.GuildDeafened,
        VoiceXpIneligibilityReason.Suppressed,
        VoiceXpIneligibilityReason.InsufficientParticipants,
        VoiceXpIneligibilityReason.MinimumSessionDuration
    ];

    public VoiceXpEligibilityResult Evaluate(VoiceXpEvaluationContext context, VoiceXpEligibilitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MinimumHumanParticipants is < 1 or > 99) throw new ArgumentOutOfRangeException(nameof(settings.MinimumHumanParticipants));
        if (!Enum.IsDefined(settings.ParticipantCountingMode)) throw new ArgumentOutOfRangeException(nameof(settings.ParticipantCountingMode));

        var reasons = PersonalReasons(context.Member, context, settings);
        var humans = settings.ParticipantCountingMode == VoiceXpParticipantCountingMode.AllConnectedHumans
            ? context.AllConnectedHumanCount ?? context.ConnectedParticipants.Count(participant => !participant.IsBot)
            : context.EligibleHumanCount ?? context.ConnectedParticipants.Count(participant => !participant.IsBot && PersonalReasons(participant, context, settings).Count == 0);
        if (humans < settings.MinimumHumanParticipants) reasons.Add(VoiceXpIneligibilityReason.InsufficientParticipants);
        var qualifies = reasons.Count == 0;
        if (qualifies && context.QualifyingSeconds + context.CurrentIntervalSeconds < context.MinimumSessionSeconds)
            reasons.Add(VoiceXpIneligibilityReason.MinimumSessionDuration);
        var ordered = Priority.Where(reasons.Contains).ToArray();
        return new(qualifies, qualifies && !ordered.Contains(VoiceXpIneligibilityReason.MinimumSessionDuration), humans, settings.MinimumHumanParticipants, ordered);
    }

    public bool IsPersonallyEligible(VoiceXpParticipantState participant, VoiceXpEvaluationContext context, VoiceXpEligibilitySettings settings) =>
        PersonalReasons(participant, context, settings).Count == 0;

    private static HashSet<VoiceXpIneligibilityReason> PersonalReasons(VoiceXpParticipantState participant, VoiceXpEvaluationContext context, VoiceXpEligibilitySettings settings)
    {
        var reasons = new HashSet<VoiceXpIneligibilityReason>();
        if (participant.IsBot) reasons.Add(VoiceXpIneligibilityReason.Bot);
        if (context.IsExcludedChannel) reasons.Add(VoiceXpIneligibilityReason.ExcludedChannel);
        if (context.IsExcludedCategory) reasons.Add(VoiceXpIneligibilityReason.ExcludedCategory);
        if (participant.HasExcludedRole) reasons.Add(VoiceXpIneligibilityReason.ExcludedRole);
        if (context.IsAfkChannel && !settings.AwardInAfkChannel) reasons.Add(VoiceXpIneligibilityReason.AfkChannel);
        if (participant.IsSelfMuted && !settings.AwardWhileSelfMuted) reasons.Add(VoiceXpIneligibilityReason.SelfMuted);
        if (participant.IsSelfDeafened && !settings.AwardWhileSelfDeafened) reasons.Add(VoiceXpIneligibilityReason.SelfDeafened);
        if (participant.IsGuildMuted && !settings.AwardWhileGuildMuted) reasons.Add(VoiceXpIneligibilityReason.GuildMuted);
        if (participant.IsGuildDeafened && !settings.AwardWhileGuildDeafened) reasons.Add(VoiceXpIneligibilityReason.GuildDeafened);
        if (participant.IsSuppressed && !settings.AwardWhileSuppressed) reasons.Add(VoiceXpIneligibilityReason.Suppressed);
        return reasons;
    }
}
