namespace Rankoon.Data.Diagnostics;

public interface IPermissionRequirementCatalog { IReadOnlyList<PermissionRequirementDefinition> Definitions { get; } }

// Feature modules add definitions here; the resolver deliberately owns no permission lists.
public sealed class PermissionRequirementCatalog : IPermissionRequirementCatalog
{
    public IReadOnlyList<PermissionRequirementDefinition> Definitions { get; } =
    [
        new(DiagnosticFeatureKeys.TextXp, "view", ["ViewChannel"], ["Text"], "diagnostics.feature.textXp", "diagnostics.permission.missing", "diagnostics.remediation.channelPermission"),
        new(DiagnosticFeatureKeys.ReactionXp, "read-history", ["ViewChannel", "ReadMessageHistory"], ["Text"], "diagnostics.feature.reactionXp", "diagnostics.permission.missing", "diagnostics.remediation.channelPermission"),
        new(DiagnosticFeatureKeys.ThreadXp, "view", ["ViewChannel"], ["Text", "Thread"], "diagnostics.feature.threadXp", "diagnostics.permission.missing", "diagnostics.remediation.channelPermission"),
        new(DiagnosticFeatureKeys.VoiceXp, "view", ["ViewChannel"], ["Voice", "Stage"], "diagnostics.feature.voiceXp", "diagnostics.permission.missing", "diagnostics.remediation.channelPermission"),
        new(DiagnosticFeatureKeys.LevelUpAnnouncements, "send", ["ViewChannel", "SendMessages", "EmbedLinks"], ["Text"], "diagnostics.feature.levelUp", "diagnostics.permission.missing", "diagnostics.remediation.channelPermission"),
        new(DiagnosticFeatureKeys.LevelRoles, "role", [], [], "diagnostics.feature.levelRoles", "diagnostics.permission.missing", "diagnostics.remediation.moveBotRole"),
        new(DiagnosticFeatureKeys.SelfRoles, "panel", ["ViewChannel", "SendMessages", "EmbedLinks", "AddReactions", "ReadMessageHistory", "ManageMessages"], ["Text"], "diagnostics.feature.selfRoles", "diagnostics.permission.missing", "diagnostics.remediation.channelPermission"),
        new(DiagnosticFeatureKeys.VoiceHubs, "hub", ["ViewChannel", "ManageChannels", "MoveMembers"], ["Voice"], "diagnostics.feature.voiceHubs", "diagnostics.permission.missing", "diagnostics.remediation.channelPermission")
    ];
}
