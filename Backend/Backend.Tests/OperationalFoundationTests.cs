using Rankoon.Data.Analytics;
using Rankoon.Data.Operations;
using Xunit;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Http;
using Rankoon.Controllers;
using Rankoon.Data.Auth;
using Rankoon.Data.Model;
using Rankoon.Data.Reporting;
using Rankoon.Data.Utils;
using System.Reflection;
using MongoDB.Bson.Serialization.Attributes;

namespace Backend.Tests;

public sealed class OperationalFoundationTests
{
    [Fact]
    public void Redactor_Removes_Common_Secret_Forms()
    {
        const string input = "Authorization: Bearer abc123 Cookie: session=secret "
            + "mongodb://admin:hunter2@db/test?token=query-secret "
            + "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop";

        var result = OperationalErrorSanitizer.Redact(input, 10_000);

        Assert.DoesNotContain("abc123", result);
        Assert.DoesNotContain("session=secret", result);
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("query-secret", result);
        Assert.DoesNotContain("eyJhbGci", result);
        Assert.Contains(OperationalErrorSanitizer.Redacted, result);
    }

    [Fact]
    public void Fingerprint_Normalizes_Volatile_Identifiers_And_Numbers()
    {
        var first = OperationalErrorSanitizer.CreateFingerprint("InvalidOperationException",
            "Guild 123 failed request 4f1cb1f7-853d-4f9b-856c-1b7a927f161f");
        var second = OperationalErrorSanitizer.CreateFingerprint("InvalidOperationException",
            "Guild 987 failed request 80ce5d1d-19c5-432b-b216-121b8882138a");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Retention_Defaults_Match_Persistence_Contracts()
    {
        var reporting = new ReportingRetentionOptions();
        var analytics = new AnalyticsRetentionOptions();

        Assert.Equal(180, reporting.AuditDays);
        Assert.Equal(30, reporting.OccurrenceDays);
        Assert.Equal(180, reporting.IncidentDays);
        Assert.Equal(30, analytics.HourDays);
        Assert.Equal(400, analytics.DayDays);
    }

    [Fact]
    public void Worker_Health_Snapshot_Is_Ordered_And_Marks_Stale_Entries()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
        var registry = new WorkerHealthRegistry(time);
        registry.Report("worker-b", WorkerHealthState.Healthy);
        registry.Report("worker-a", WorkerHealthState.Degraded, "lagging");
        time.Advance(TimeSpan.FromMinutes(6));

        var snapshot = registry.GetSnapshot(TimeSpan.FromMinutes(5));

        Assert.Equal(["worker-a", "worker-b"], snapshot.Select(item => item.Worker));
        Assert.All(snapshot, item => Assert.True(item.IsStale));
        Assert.Equal("lagging", snapshot[0].Detail);
    }

    [Fact]
    public void Analytics_Ranges_Are_Bounded_And_Include_Previous_Period()
    {
        var to = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var period = GuildAnalyticsQueryService.CreatePeriod(AnalyticsRange.Last90Days, to);
        Assert.Equal(TimeSpan.FromDays(90), period.To - period.From);
        Assert.Equal(period.From, period.PreviousTo);
        Assert.Equal(TimeSpan.FromDays(90), period.PreviousTo - period.PreviousFrom);
        Assert.False(GuildAnalyticsQueryService.TryParseRange("365d", out _));
    }

    [Fact]
    public void Signed_Cursor_Is_Filter_Bound_And_Tamper_Resistant()
    {
        var service = new SignedCursorService(Options.Create(new JwtSettings { SecretKey = new string('x', 64) }));
        var token = service.Create(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero), "507f1f77bcf86cd799439011", "guild:1|status:open");
        Assert.True(service.TryRead(token, "guild:1|status:open", out var cursor));
        Assert.Equal("507f1f77bcf86cd799439011", cursor.Id);
        Assert.False(service.TryRead(token, "guild:2|status:open", out _));
        Assert.False(service.TryRead(token + "x", "guild:1|status:open", out _));
    }

    [Theory]
    [InlineData(OperationalIncidentStatus.New, "acknowledge", OperationalIncidentStatus.Acknowledged)]
    [InlineData(OperationalIncidentStatus.Acknowledged, "resolve", OperationalIncidentStatus.Resolved)]
    [InlineData(OperationalIncidentStatus.New, "ignore", OperationalIncidentStatus.Ignored)]
    [InlineData(OperationalIncidentStatus.Resolved, "reopen", OperationalIncidentStatus.New)]
    public void Incident_Transitions_Are_Explicit(OperationalIncidentStatus current, string action, OperationalIncidentStatus expected)
    {
        Assert.True(IncidentTransitions.TryApply(current, action, out var next));
        Assert.Equal(expected, next);
    }

    [Theory]
    [InlineData(OperationalIncidentStatus.Resolved, true)]
    [InlineData(OperationalIncidentStatus.Ignored, true)]
    [InlineData(OperationalIncidentStatus.Acknowledged, false)]
    public void Incident_Recurrence_Reopens_Closed_States(OperationalIncidentStatus status, bool expected) => Assert.Equal(expected, IncidentTransitions.ReopensOnRecurrence(status));

    [Fact]
    public void Operator_Routes_Require_BotOperator_And_Legacy_Error_Writer_Is_Gone()
    {
        var methods = typeof(BotManagementController).GetMethods().Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any() && method.Name != nameof(BotManagementController.GetAccess));
        Assert.All(methods, method => Assert.Contains(method.GetCustomAttributes<AuthorizeAttribute>(), attribute => attribute.Policy == AuthorizationPolicies.BotOperator));
        Assert.DoesNotContain(typeof(IReportWriter).GetMethods(), method => method.Name.Contains("Error", StringComparison.Ordinal));
        var routes = typeof(BotManagementController).GetMethods().SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()).SelectMany(attribute => attribute.Template is null ? [] : new[] { attribute.Template }).ToArray();
        Assert.Contains("incidents/{fingerprint}", routes);
        Assert.DoesNotContain("incidents/{id}", routes);
        var legacy = new ReportsController(null!, null!);
#pragma warning disable CS0618 // The test intentionally verifies the obsolete compatibility endpoints.
        Assert.Equal(StatusCodes.Status410Gone, Assert.IsType<StatusCodeResult>(legacy.Errors()).StatusCode);
        Assert.Equal(StatusCodes.Status410Gone, Assert.IsType<StatusCodeResult>(legacy.ErrorsSummary()).StatusCode);
#pragma warning restore CS0618
    }

    [Fact]
    public void Observability_Models_Retain_Required_Fields()
    {
        AssertFields<GuildAuditEvent>("Id", "GuildId", "Type", "Feature", "Action", "Outcome", "ActorUserId", "SubjectId", "ChannelId", "CorrelationId", "Metadata", "OccurredAtUtc", "RecordedAtUtc", "ExpiresAtUtc", "SchemaVersion");
        AssertFields<GuildAnalyticsBucket>("Count", "Value", "DurationSeconds", "Source", "Outcome", "Reason", "ChannelId");
        AssertFields<OperationalErrorOccurrence>("Severity", "Source", "ExceptionType", "Message", "StackTrace", "InnerException", "GuildId", "ActorUserId", "ChannelId", "Command", "Route", "Worker", "CorrelationId", "TraceId", "Build", "Metadata", "OccurredAtUtc", "RecordedAtUtc", "ExpiresAtUtc", "SchemaVersion");
        AssertFields<OperationalIncident>("Title", "Source", "Severity", "Status", "OccurrenceCount", "FirstSeenAtUtc", "LastSeenAtUtc", "LastOccurrenceId", "LastBuild", "AffectedGuildIds", "AffectedGuildCount", "AcknowledgedBy", "ResolvedBy", "IgnoredBy", "StatusNote", "CreatedAtUtc", "UpdatedAtUtc", "ExpiresAtUtc", "SchemaVersion");
        AssertBsonName<OperationalErrorOccurrence>(nameof(OperationalErrorOccurrence.InnerException), "inner_exception_summary");
        AssertBsonName<OperationalErrorOccurrence>(nameof(OperationalErrorOccurrence.TraceId), "trace_identifier");
        AssertBsonName<OperationalErrorOccurrence>(nameof(OperationalErrorOccurrence.Build), "build_version");
        AssertBsonName<OperationalIncident>(nameof(OperationalIncident.StatusNote), "note");
    }

    [Fact]
    public void Audit_Metadata_Is_Allowlisted_And_Does_Not_Keep_Message_Content()
    {
        var metadata = GuildAuditWriter.SanitizeMetadata(new Dictionary<string, object?> { ["source"] = "voice", ["reason"] = "a user message", ["message"] = "secret content", ["token"] = "secret" });
        Assert.Equal("voice", metadata["source"]);
        Assert.DoesNotContain("reason", metadata.Keys);
        Assert.DoesNotContain("message", metadata.Keys);
        Assert.DoesNotContain("token", metadata.Keys);
    }

    [Fact]
    public void Reporting_Grant_Normalizes_To_Analytics_Without_Operational_Error_Access()
    {
        var method = typeof(GuildRolePermissionService).GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static)!;
        var input = new[] { new GuildRoleModuleGrant { RoleId = 1, ModuleIds = [GuildModuleIds.Reporting] } };
        var grants = Assert.IsAssignableFrom<IEnumerable<GuildRoleModuleGrant>>(method.Invoke(null, [input])).Single();
        Assert.Equal([GuildModuleIds.Analytics], grants.ModuleIds);
        Assert.DoesNotContain("errors", grants.ModuleIds);
    }

    private static void AssertFields<T>(params string[] names)
    {
        var properties = typeof(T).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(names, name => Assert.Contains(name, properties));
    }

    private static void AssertBsonName<T>(string propertyName, string expected) => Assert.Equal(expected, typeof(T).GetProperty(propertyName)!.GetCustomAttribute<BsonElementAttribute>()!.ElementName);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
