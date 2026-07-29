using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Rankoon.Api;
using Rankoon.Controllers;
using Rankoon.Data.Model;
using Rankoon.Data.Xp;
using Xunit;

namespace Backend.Tests;

public sealed class XpAuditMemberPagingTests
{
    private static readonly XpAuditService Service = new(
        null!, null!, null!, null!, TimeProvider.System,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = new string('x', 64)
        }).Build(),
        null!);

    [Fact]
    public void Member_endpoint_defaults_to_total_xp_descending()
    {
        var parameter = typeof(XpAuditController).GetMethod(nameof(XpAuditController.Members))!
            .GetParameters().Single(x => x.Name == "sort");

        Assert.Equal(XpAuditMemberSort.TotalXpDescending, parameter.DefaultValue);
        Assert.Equal(
            [XpAuditMemberSort.TotalXpDescending, XpAuditMemberSort.NameAscending, XpAuditMemberSort.NameDescending],
            Enum.GetValues<XpAuditMemberSort>());
    }

    [Fact]
    public void Undefined_sort_is_rejected_with_a_specific_bad_request()
    {
        var exception = Assert.Throws<XpAuditValidationException>(() =>
            XpAuditService.BuildMemberSort((XpAuditMemberSort)99));
        var error = ApiErrorCatalog.Get("xpAudit.invalidSort");

        Assert.Equal("xpAudit.invalidSort", exception.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, error.StatusCode);
    }

    [Fact]
    public void Total_xp_cursor_filter_keeps_equal_xp_members_after_the_stable_name_and_user_keys()
    {
        var rendered = Render(XpAuditService.BuildMemberCursorFilter(
            XpAuditMemberSort.TotalXpDescending, "same", 20, 100m));

        Assert.Contains("total_xp", rendered);
        Assert.Contains("$lt", rendered);
        Assert.Contains("normalized_display_name", rendered);
        Assert.Contains("$gt", rendered);
        Assert.Contains("user_id", rendered);
        Assert.Contains("100", rendered);
        Assert.Contains("20", rendered);
    }

    [Fact]
    public void Name_cursor_direction_matches_sort()
    {
        var ascendingSort = Render(XpAuditService.BuildMemberSort(XpAuditMemberSort.NameAscending));
        var ascendingFilter = Render(XpAuditService.BuildMemberCursorFilter(XpAuditMemberSort.NameAscending, "member", 42, null));
        var descendingSort = Render(XpAuditService.BuildMemberSort(XpAuditMemberSort.NameDescending));
        var descendingFilter = Render(XpAuditService.BuildMemberCursorFilter(XpAuditMemberSort.NameDescending, "member", 42, null));

        Assert.Contains("$gt", ascendingFilter);
        Assert.Contains("$lt", descendingFilter);
        Assert.Contains("normalized_display_name", ascendingSort);
        Assert.Contains("user_id", ascendingSort);
        Assert.Contains("normalized_display_name", descendingSort);
        Assert.Contains("user_id", descendingSort);
    }

    [Fact]
    public void Member_cursor_is_bound_to_guild_query_membership_scope_and_sort()
    {
        var cursor = Service.CreateMemberCursor(1, "  AlIce ", false, XpAuditMemberSort.TotalXpDescending, "alice", 10, 50m);
        var decoded = Service.ReadMemberCursor(cursor, 1, "alice", false, XpAuditMemberSort.TotalXpDescending);

        Assert.NotNull(decoded);
        Assert.Equal("alice", decoded.Name);
        Assert.Equal(10UL, decoded.UserId);
        Assert.Equal(50m, decoded.TotalXp);
        AssertInvalid(() => Service.ReadMemberCursor(cursor, 2, "alice", false, XpAuditMemberSort.TotalXpDescending));
        AssertInvalid(() => Service.ReadMemberCursor(cursor, 1, "bob", false, XpAuditMemberSort.TotalXpDescending));
        AssertInvalid(() => Service.ReadMemberCursor(cursor, 1, "alice", true, XpAuditMemberSort.TotalXpDescending));
        AssertInvalid(() => Service.ReadMemberCursor(cursor, 1, "alice", false, XpAuditMemberSort.NameAscending));
    }

    [Fact]
    public void Member_cursor_rejects_tampering()
    {
        var cursor = Service.CreateMemberCursor(1, null, true, XpAuditMemberSort.NameDescending, "z", 99, 1m);
        var tampered = (cursor[0] == 'A' ? "B" : "A") + cursor[1..];

        AssertInvalid(() => Service.ReadMemberCursor(tampered, 1, null, true, XpAuditMemberSort.NameDescending));
    }

    private static string Render(FilterDefinition<MemberXp> filter) => filter.Render(new RenderArgs<MemberXp>(
        BsonSerializer.SerializerRegistry.GetSerializer<MemberXp>(), BsonSerializer.SerializerRegistry)).ToJson();

    private static string Render(SortDefinition<MemberXp> sort) => sort.Render(new RenderArgs<MemberXp>(
        BsonSerializer.SerializerRegistry.GetSerializer<MemberXp>(), BsonSerializer.SerializerRegistry)).ToJson();

    private static void AssertInvalid(Action action)
    {
        var exception = Assert.Throws<XpAuditValidationException>(action);
        Assert.Equal("xpAudit.invalidCursor", exception.Code);
    }
}
