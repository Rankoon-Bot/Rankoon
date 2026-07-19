using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Rankoon.Api;
using Xunit;

namespace Backend.Tests;

public sealed class ApiErrorContractTests
{
    [Fact]
    public void Catalog_has_unique_stable_keys_and_safe_messages()
    {
        Assert.NotEmpty(ApiErrorCatalog.All);
        Assert.Equal(ApiErrorCatalog.All.Count, ApiErrorCatalog.All.Select(error => error.Key).Distinct(StringComparer.Ordinal).Count());

        foreach (var error in ApiErrorCatalog.All)
        {
            Assert.Matches("^[a-z][A-Za-z0-9]*(\\.[A-Za-z][A-Za-z0-9]*)+$", error.Key);
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            Assert.DoesNotContain("Exception", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(error.StatusCode, 400, 599);
        }
    }

    [Theory]
    [InlineData(400, "request.badRequest")]
    [InlineData(401, "auth.unauthorized")]
    [InlineData(403, "auth.forbidden")]
    [InlineData(404, "resource.notFound")]
    [InlineData(405, "request.methodNotAllowed")]
    [InlineData(415, "request.unsupportedMediaType")]
    [InlineData(429, "rateLimit.exceeded")]
    [InlineData(500, "server.internal")]
    [InlineData(422, "request.rejected")]
    public void Status_codes_map_to_canonical_errors(int statusCode, string expectedKey)
    {
        Assert.Equal(expectedKey, ApiErrorCatalog.ForStatusCode(statusCode).Key);
    }

    [Fact]
    public void Generic_result_preserves_the_original_status_code()
    {
        var context = new DefaultHttpContext();
        var definition = ApiErrorCatalog.ForStatusCode(StatusCodes.Status422UnprocessableEntity);

        var result = ApiErrorFactory.Result(context, definition.Key, statusCode: StatusCodes.Status422UnprocessableEntity);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        Assert.Equal("request.rejected", Assert.IsType<ApiErrorResponse>(result.Value).ErrorKey);
    }

    [Fact]
    public void Factory_includes_required_fields_and_omits_optional_fields()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-123" };

        var response = ApiErrorFactory.Create(context, "resource.notFound");
        var json = JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("resource.notFound", json.GetProperty("errorKey").GetString());
        Assert.Equal("The requested resource was not found.", json.GetProperty("message").GetString());
        Assert.Equal("trace-123", json.GetProperty("traceId").GetString());
        Assert.False(json.TryGetProperty("parameters", out _));
        Assert.False(json.TryGetProperty("errors", out _));
        Assert.False(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Writer_sets_status_and_serializes_parameters()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-rate-limit" };
        context.Response.Body = new MemoryStream();

        await ApiErrorFactory.WriteAsync(
            context,
            "rateLimit.exceeded",
            new Dictionary<string, object?> { ["retryAfterSeconds"] = 12 });

        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("rateLimit.exceeded", json.RootElement.GetProperty("errorKey").GetString());
        Assert.Equal(12, json.RootElement.GetProperty("parameters").GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal("trace-rate-limit", json.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public void Validation_errors_are_structured_and_keyed()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-validation" };
        var errors = new Dictionary<string, IReadOnlyList<ApiValidationError>>
        {
            ["message.points"] = [ApiErrorFactory.Validation("xp.settings.messagePoints")]
        };

        var response = ApiErrorFactory.Create(context, "xp.settingsInvalid", errors: errors);

        var error = Assert.Single(response.Errors!["message.points"]);
        Assert.Equal("xp.settings.messagePoints", error.ErrorKey);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Unknown_keys_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ApiErrorCatalog.Get("unknown.error"));
    }
}
