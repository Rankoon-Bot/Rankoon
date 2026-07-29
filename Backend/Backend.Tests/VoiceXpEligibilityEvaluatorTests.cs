using Rankoon.Data.Discord;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using System.Text.Json;
using Xunit;

namespace Backend.Tests;

public sealed class VoiceXpEligibilityEvaluatorTests
{
    private readonly VoiceXpEligibilityEvaluator evaluator = new();

    [Theory]
    [InlineData(nameof(VoiceXpParticipantState.IsSelfMuted), nameof(VoiceXpEligibilitySettings.AwardWhileSelfMuted), VoiceXpIneligibilityReason.SelfMuted)]
    [InlineData(nameof(VoiceXpParticipantState.IsSelfDeafened), nameof(VoiceXpEligibilitySettings.AwardWhileSelfDeafened), VoiceXpIneligibilityReason.SelfDeafened)]
    [InlineData(nameof(VoiceXpParticipantState.IsGuildMuted), nameof(VoiceXpEligibilitySettings.AwardWhileGuildMuted), VoiceXpIneligibilityReason.GuildMuted)]
    [InlineData(nameof(VoiceXpParticipantState.IsGuildDeafened), nameof(VoiceXpEligibilitySettings.AwardWhileGuildDeafened), VoiceXpIneligibilityReason.GuildDeafened)]
    [InlineData(nameof(VoiceXpParticipantState.IsSuppressed), nameof(VoiceXpEligibilitySettings.AwardWhileSuppressed), VoiceXpIneligibilityReason.Suppressed)]
    public void Every_voice_status_can_be_allowed_or_rejected(string stateProperty, string settingProperty, VoiceXpIneligibilityReason reason)
    {
        var participant = ParticipantWith(stateProperty);
        var settings = Settings();
        typeof(VoiceXpEligibilitySettings).GetProperty(settingProperty)!.SetValue(settings, false);
        Assert.Contains(reason, Evaluate(participant, settings).Reasons);

        typeof(VoiceXpEligibilitySettings).GetProperty(settingProperty)!.SetValue(settings, true);
        Assert.DoesNotContain(reason, Evaluate(participant, settings).Reasons);
    }

    [Fact]
    public void Afk_channel_can_be_allowed_or_rejected()
    {
        var settings = Settings();
        var context = Context(new(1), isAfk: true);
        Assert.Contains(VoiceXpIneligibilityReason.AfkChannel, evaluator.Evaluate(context, settings).Reasons);
        settings.AwardInAfkChannel = true;
        Assert.DoesNotContain(VoiceXpIneligibilityReason.AfkChannel, evaluator.Evaluate(context, settings).Reasons);
    }

    [Theory]
    [InlineData("channel", VoiceXpIneligibilityReason.ExcludedChannel)]
    [InlineData("category", VoiceXpIneligibilityReason.ExcludedCategory)]
    [InlineData("role", VoiceXpIneligibilityReason.ExcludedRole)]
    public void Exclusions_are_reported_individually(string exclusion, VoiceXpIneligibilityReason reason)
    {
        var participant = new VoiceXpParticipantState(1, HasExcludedRole: exclusion == "role");
        var context = Context(participant, excludedChannel: exclusion == "channel", excludedCategory: exclusion == "category");
        Assert.Contains(reason, evaluator.Evaluate(context, Settings()).Reasons);
    }

    [Fact]
    public void Minimum_participant_count_supports_one_through_ninety_nine()
    {
        Assert.True(Evaluate(new(1), Settings(minimum: 1)).Qualifies);
        var insufficient = Evaluate(new(1), Settings(minimum: 2));
        Assert.False(insufficient.Qualifies);
        Assert.Contains(VoiceXpIneligibilityReason.InsufficientParticipants, insufficient.Reasons);
        Assert.True(evaluator.Evaluate(Context(new(1), participants: [new(1), new(2), new(3)]), Settings(minimum: 3)).Qualifies);
    }

    [Fact]
    public void Bots_never_qualify_or_count()
    {
        var bot = new VoiceXpParticipantState(2, IsBot: true);
        var result = evaluator.Evaluate(Context(new(1), participants: [new(1), bot]), Settings(minimum: 2));
        Assert.Equal(1, result.HumanParticipantCount);
        Assert.Contains(VoiceXpIneligibilityReason.InsufficientParticipants, result.Reasons);
        Assert.Contains(VoiceXpIneligibilityReason.Bot, Evaluate(bot, Settings(minimum: 1)).Reasons);
    }

    [Fact]
    public void Counting_modes_distinguish_connected_and_personally_eligible_humans()
    {
        var deafened = new VoiceXpParticipantState(2, IsGuildDeafened: true);
        var participants = new[] { new VoiceXpParticipantState(1), deafened };
        var all = evaluator.Evaluate(Context(participants[0], participants: participants), Settings(minimum: 2, VoiceXpParticipantCountingMode.AllConnectedHumans));
        var eligible = evaluator.Evaluate(Context(participants[0], participants: participants), Settings(minimum: 2, VoiceXpParticipantCountingMode.EligibleHumansOnly));
        Assert.True(all.Qualifies);
        Assert.False(eligible.Qualifies);
        Assert.Equal(1, eligible.HumanParticipantCount);
    }

    [Fact]
    public void Role_excluded_humans_do_not_count_in_eligible_mode()
    {
        var excluded = new VoiceXpParticipantState(2, HasExcludedRole: true);
        var settings = Settings(minimum: 2, VoiceXpParticipantCountingMode.EligibleHumansOnly);
        Assert.Equal(1, evaluator.Evaluate(Context(new(1), participants: [new(1), excluded]), settings).HumanParticipantCount);
    }

    [Fact]
    public void All_matching_reasons_are_returned_in_stable_analytics_priority()
    {
        var participant = new VoiceXpParticipantState(1, HasExcludedRole: true, IsSelfMuted: true, IsGuildDeafened: true);
        var settings = Settings(minimum: 2);
        settings.AwardWhileSelfMuted = false;
        var result = evaluator.Evaluate(Context(participant, excludedChannel: true, isAfk: true), settings);
        Assert.Equal(new[]
        {
            VoiceXpIneligibilityReason.ExcludedChannel, VoiceXpIneligibilityReason.ExcludedRole, VoiceXpIneligibilityReason.AfkChannel,
            VoiceXpIneligibilityReason.SelfMuted, VoiceXpIneligibilityReason.GuildDeafened, VoiceXpIneligibilityReason.InsufficientParticipants
        }, result.Reasons);
        Assert.Equal(VoiceXpIneligibilityReason.ExcludedChannel, result.PrimaryReason);
    }

    [Theory]
    [InlineData(59, 0, false)]
    [InlineData(59, 1, true)]
    [InlineData(60, 0, true)]
    public void Minimum_duration_uses_accumulated_qualifying_time(long progress, long interval, bool eligible)
    {
        var result = evaluator.Evaluate(Context(new(1), qualifying: progress, interval: interval, minimumSeconds: 60), Settings(minimum: 1));
        Assert.Equal(eligible, result.EligibleAfterMinimumDuration);
        Assert.Equal(!eligible, result.Reasons.Contains(VoiceXpIneligibilityReason.MinimumSessionDuration));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Invalid_minimum_participants_is_rejected(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Evaluate(new(1), Settings(minimum: value)));

    [Fact]
    public void Unknown_counting_mode_is_rejected() => Assert.Throws<ArgumentOutOfRangeException>(() =>
        Evaluate(new(1), Settings(mode: (VoiceXpParticipantCountingMode)999)));

    [Theory]
    [InlineData(true, 2, false)]
    [InlineData(false, 1, true)]
    public void Legacy_settings_preserve_participant_and_afk_semantics(bool multiple, int minimum, bool awardAfk)
    {
        var voice = new VoiceXpSettings { Eligibility = null, SettingsVersion = null, RequireMultipleHumans = multiple, ExcludeAfkChannel = !awardAfk };
        VoiceXpSettingsNormalizer.NormalizeLegacy(voice);
        Assert.Equal(minimum, voice.Eligibility!.MinimumHumanParticipants);
        Assert.Equal(awardAfk, voice.Eligibility.AwardInAfkChannel);
        Assert.Equal(VoiceXpParticipantCountingMode.EligibleHumansOnly, voice.Eligibility.ParticipantCountingMode);
        Assert.True(voice.Eligibility.AwardWhileSelfDeafened);
        Assert.False(voice.Eligibility.AwardWhileGuildDeafened);
        Assert.True(voice.Eligibility.ResetMinimumSessionWhenIneligible);
    }

    [Theory]
    [InlineData(true, true, 2, false)]
    [InlineData(false, false, 1, true)]
    public void Legacy_json_payloads_remain_deserializable(bool requireMultiple, bool excludeAfk, int minimum, bool awardAfk)
    {
        var json = $$"""{"requireMultipleHumans":{{requireMultiple.ToString().ToLowerInvariant()}},"excludeAfkChannel":{{excludeAfk.ToString().ToLowerInvariant()}}}""";
        var voice = JsonSerializer.Deserialize<VoiceXpSettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        VoiceXpSettingsNormalizer.NormalizeLegacy(voice);
        Assert.Equal(minimum, voice.Eligibility!.MinimumHumanParticipants);
        Assert.Equal(awardAfk, voice.Eligibility.AwardInAfkChannel);
        Assert.DoesNotContain("requireMultipleHumans", JsonSerializer.Serialize(voice, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private VoiceXpEligibilityResult Evaluate(VoiceXpParticipantState participant, VoiceXpEligibilitySettings settings) => evaluator.Evaluate(Context(participant), settings);
    private static VoiceXpEvaluationContext Context(VoiceXpParticipantState participant, IReadOnlyList<VoiceXpParticipantState>? participants = null,
        bool excludedChannel = false, bool excludedCategory = false, bool isAfk = false, long qualifying = 0, long interval = 60, int minimumSeconds = 0) =>
        new(participant, participants ?? [participant], excludedChannel, excludedCategory, isAfk, qualifying, interval, minimumSeconds);
    private static VoiceXpEligibilitySettings Settings(int minimum = 1, VoiceXpParticipantCountingMode mode = VoiceXpParticipantCountingMode.AllConnectedHumans) => new()
    {
        AwardWhileSelfMuted = true,
        AwardWhileSelfDeafened = true,
        AwardWhileGuildMuted = true,
        AwardWhileGuildDeafened = false,
        AwardWhileSuppressed = true,
        MinimumHumanParticipants = minimum,
        ParticipantCountingMode = mode
    };
    private static VoiceXpParticipantState ParticipantWith(string property)
    {
        var values = new Dictionary<string, bool> { [property] = true };
        return new(1, IsSelfMuted: values.ContainsKey(nameof(VoiceXpParticipantState.IsSelfMuted)), IsSelfDeafened: values.ContainsKey(nameof(VoiceXpParticipantState.IsSelfDeafened)),
            IsGuildMuted: values.ContainsKey(nameof(VoiceXpParticipantState.IsGuildMuted)), IsGuildDeafened: values.ContainsKey(nameof(VoiceXpParticipantState.IsGuildDeafened)), IsSuppressed: values.ContainsKey(nameof(VoiceXpParticipantState.IsSuppressed)));
    }
}
