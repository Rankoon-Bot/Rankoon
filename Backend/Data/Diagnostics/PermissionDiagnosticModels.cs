namespace Rankoon.Data.Diagnostics;

public enum DiagnosticStatus { Healthy, Warning, Critical, Unknown, NotApplicable }
public enum PermissionDiagnosticScope { ConfiguredFeatures, AllChannels, Full }
public enum DiagnosticCompleteness { Complete, Partial, Unavailable }

public sealed record PermissionDiagnosticScanRequest(PermissionDiagnosticScope Scope = PermissionDiagnosticScope.ConfiguredFeatures, bool IncludePermissionTrace = true);
public sealed record PermissionTraceStep(string Source, bool? Allowed, string DetailKey);
public sealed record PermissionTrace(string Permission, bool Effective, IReadOnlyList<PermissionTraceStep> Steps, string? DecisiveSource);
public sealed record PermissionCheckResult(
    string CheckKey, DiagnosticStatus Status, string FeatureKey, string? ResourceId, string ResourceType,
    string ResourceName, IReadOnlyList<string> RequiredPermissions, IReadOnlyList<string> PresentPermissions,
    IReadOnlyList<string> MissingPermissions, IReadOnlyList<PermissionTrace> PermissionTraces,
    string TitleKey, string DescriptionKey, string RemediationKey, IReadOnlyDictionary<string, string> Parameters,
    string Impact);
public sealed record FeatureDiagnosticResult(string FeatureKey, DiagnosticStatus Status, bool Enabled, IReadOnlyList<PermissionCheckResult> Checks);
public sealed record RoleHierarchyDiagnosticResult(string RoleId, string RoleName, string FeatureKey, DiagnosticStatus Status, string ReasonKey, string RemediationKey);
public sealed record ChannelDiagnosticResult(string ChannelId, string ChannelName, string ChannelType, string? CategoryId, string? CategoryName, IReadOnlyList<string> Features, IReadOnlyList<PermissionCheckResult> Checks);
public sealed record DiagnosticStatistics(int FeaturesChecked, int ChannelsChecked, int Critical, int Warnings, int Unknown);
public sealed record PermissionDiagnosticReport(
    string RunId, string GuildId, DateTimeOffset GeneratedAt, DiagnosticStatus OverallStatus, DiagnosticCompleteness Completeness,
    IReadOnlyList<PermissionCheckResult> GlobalChecks, IReadOnlyList<FeatureDiagnosticResult> FeatureChecks,
    IReadOnlyList<RoleHierarchyDiagnosticResult> RoleChecks, IReadOnlyList<ChannelDiagnosticResult> ChannelChecks,
    DiagnosticStatistics Statistics);

public sealed record PermissionRequirementDefinition(string FeatureKey, string RequirementKey, IReadOnlyList<string> RequiredPermissions, IReadOnlyList<string> ChannelKinds, string TitleKey, string DescriptionKey, string RemediationKey);
public static class DiagnosticFeatureKeys
{
    public const string TextXp = "text-xp";
    public const string ReactionXp = "reaction-xp";
    public const string ThreadXp = "thread-xp";
    public const string VoiceXp = "voice-xp";
    public const string LevelUpAnnouncements = "level-up-announcements";
    public const string LevelRoles = "level-roles";
    public const string SelfRoles = "self-roles";
    public const string VoiceHubs = "voice-hubs";
}

public sealed record GatewayIntentState(global::Discord.GatewayIntents Value);
