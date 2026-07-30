using Rankoon.Controllers;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Operations;
using Xunit;

namespace Backend.Tests;

public sealed class GuildRolePermissionsTests
{
    [Fact]
    public void Registry_contains_exactly_the_nine_canonical_modules()
    {
        var modules = new GuildModuleRegistry().Modules;

        Assert.Equal([
            "xp", "leaderboard", "voice-hubs", "analytics", "self-roles", "xp-audit",
            "xp-adjustments", "xp-announcements", "diagnostics"
        ], modules.Select(module => module.Id));
        Assert.All(modules, module =>
        {
            Assert.False(string.IsNullOrWhiteSpace(module.Category));
            Assert.False(string.IsNullOrWhiteSpace(module.Impact));
        });
        Assert.Equal([GuildModuleIds.XpAudit], modules.Single(module => module.Id == GuildModuleIds.XpAdjustments).RequiredModuleIds);
        Assert.All(modules.Where(module => module.Id != GuildModuleIds.XpAdjustments), module => Assert.Empty(module.RequiredModuleIds));
        Assert.Equal((GuildModuleCategories.Progression, GuildModuleImpacts.Sensitive), Metadata(GuildModuleIds.Xp));
        Assert.Equal((GuildModuleCategories.XpModeration, GuildModuleImpacts.ReadOnly), Metadata(GuildModuleIds.XpAudit));
        Assert.Equal((GuildModuleCategories.Community, GuildModuleImpacts.Sensitive), Metadata(GuildModuleIds.SelfRoles));
        Assert.Equal((GuildModuleCategories.Insights, GuildModuleImpacts.ReadOnly), Metadata(GuildModuleIds.Diagnostics));

        (string, string) Metadata(string id)
        {
            var module = modules.Single(item => item.Id == id);
            return (module.Category, module.Impact);
        }
    }

    [Fact]
    public void Normalization_migrates_and_cleans_legacy_policy_authoritatively()
    {
        var service = new GuildRolePermissionService(null!, new GuildModuleRegistry());
        GuildRoleModuleGrant[] input =
        [
            new() { RoleId = 30, ModuleIds = [GuildModuleIds.XpAdjustments, GuildModuleIds.XpAdjustments, "unknown"] },
            new() { RoleId = 10, ModuleIds = ["reporting", GuildModuleIds.Xp, GuildModuleIds.Analytics] },
            new() { RoleId = 20, ModuleIds = [] },
            new() { RoleId = 40, ModuleIds = [GuildModuleIds.Xp] },
            new() { RoleId = 50, ModuleIds = [GuildModuleIds.Xp] },
            new() { RoleId = 10, ModuleIds = [GuildModuleIds.Xp] }
        ];

        // 40 represents a deleted/non-selectable role and 50 an administrator role.
        var normalized = service.Normalize(input, new HashSet<ulong> { 10, 20, 30 });

        Assert.Equal([10UL, 30UL], normalized.Select(grant => grant.RoleId));
        Assert.Equal([GuildModuleIds.Xp, GuildModuleIds.Analytics], normalized[0].ModuleIds);
        Assert.Equal([GuildModuleIds.XpAudit, GuildModuleIds.XpAdjustments], normalized[1].ModuleIds);
    }

    [Fact]
    public void Permission_audit_diff_contains_ids_and_survives_metadata_sanitization()
    {
        GuildRoleModuleGrant[] oldGrants = [new() { RoleId = 10, ModuleIds = [GuildModuleIds.Xp] }, new() { RoleId = 20, ModuleIds = [GuildModuleIds.Analytics] }];
        GuildRoleModuleGrant[] newGrants = [new() { RoleId = 10, ModuleIds = [GuildModuleIds.Xp, GuildModuleIds.XpAudit] }, new() { RoleId = 30, ModuleIds = [GuildModuleIds.Diagnostics] }];
        var metadata = GuildAuditWriter.SanitizeMetadata(new Dictionary<string, object?>
        {
            ["oldRevision"] = 4,
            ["newRevision"] = 5,
            ["addedRoles"] = GuildPermissionsController.AddedRoleIds(oldGrants, newGrants).Count(),
            ["removedRoles"] = GuildPermissionsController.AddedRoleIds(newGrants, oldGrants).Count(),
            ["addedModules"] = GuildPermissionsController.AddedModuleAssignments(oldGrants, newGrants).Count(),
            ["removedModules"] = GuildPermissionsController.AddedModuleAssignments(newGrants, oldGrants).Count()
        });

        Assert.Equal("4", metadata["oldRevision"]);
        Assert.Equal("5", metadata["newRevision"]);
        Assert.Equal("1", metadata["addedRoles"]);
        Assert.Equal("1", metadata["removedRoles"]);
        Assert.Equal("2", metadata["addedModules"]);
        Assert.Equal("1", metadata["removedModules"]);
        Assert.DoesNotContain(metadata.Values, value => value.Contains("role name", StringComparison.OrdinalIgnoreCase));
    }
}
