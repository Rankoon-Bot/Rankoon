using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Rankoon.Api;

public sealed record ApiValidationError(
    string ErrorKey,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Parameters = null);

public sealed record ApiErrorResponse(
    string ErrorKey,
    string Message,
    string TraceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Parameters = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, IReadOnlyList<ApiValidationError>>? Errors = null);

public sealed record ApiErrorDefinition(string Key, string Message, int StatusCode);

public static class ApiErrorCatalog
{
    private static readonly IReadOnlyDictionary<string, ApiErrorDefinition> Definitions = new Dictionary<string, ApiErrorDefinition>(StringComparer.Ordinal)
    {
        ["request.badRequest"] = new("request.badRequest", "The request is invalid.", StatusCodes.Status400BadRequest),
        ["request.validationFailed"] = new("request.validationFailed", "One or more validation errors occurred.", StatusCodes.Status400BadRequest),
        ["request.malformedJson"] = new("request.malformedJson", "The request body contains malformed JSON.", StatusCodes.Status400BadRequest),
        ["request.unsupportedMediaType"] = new("request.unsupportedMediaType", "The request media type is not supported.", StatusCodes.Status415UnsupportedMediaType),
        ["request.methodNotAllowed"] = new("request.methodNotAllowed", "The HTTP method is not allowed for this resource.", StatusCodes.Status405MethodNotAllowed),
        ["request.rejected"] = new("request.rejected", "The request could not be completed.", StatusCodes.Status400BadRequest),
        ["resource.notFound"] = new("resource.notFound", "The requested resource was not found.", StatusCodes.Status404NotFound),
        ["auth.unauthorized"] = new("auth.unauthorized", "Authentication is required or the access token is invalid.", StatusCodes.Status401Unauthorized),
        ["auth.forbidden"] = new("auth.forbidden", "You are not allowed to access this resource.", StatusCodes.Status403Forbidden),
        ["rateLimit.exceeded"] = new("rateLimit.exceeded", "Too many requests were sent. Please try again later.", StatusCodes.Status429TooManyRequests),
        ["server.internal"] = new("server.internal", "An unexpected server error occurred.", StatusCodes.Status500InternalServerError),
        ["guild.invalidId"] = new("guild.invalidId", "The guild ID is invalid.", StatusCodes.Status400BadRequest),
        ["user.invalidId"] = new("user.invalidId", "The user ID is invalid.", StatusCodes.Status400BadRequest),
        ["user.notFound"] = new("user.notFound", "The user was not found.", StatusCodes.Status404NotFound),
        ["auth.refreshTokenRequired"] = new("auth.refreshTokenRequired", "A refresh token is required.", StatusCodes.Status400BadRequest),
        ["auth.refreshTokenInvalid"] = new("auth.refreshTokenInvalid", "The refresh token is invalid or expired.", StatusCodes.Status401Unauthorized),
        ["auth.logoutFailed"] = new("auth.logoutFailed", "The logout request could not be completed.", StatusCodes.Status400BadRequest),
        ["auth.tokenInvalid"] = new("auth.tokenInvalid", "The access token is invalid.", StatusCodes.Status401Unauthorized),
        ["auth.tokenMissing"] = new("auth.tokenMissing", "An access token is required.", StatusCodes.Status401Unauthorized),
        ["auth.guildsUnavailable"] = new("auth.guildsUnavailable", "The guild list could not be retrieved.", StatusCodes.Status500InternalServerError),
        ["auth.oauthFailed"] = new("auth.oauthFailed", "Authentication could not be completed. Please try again.", StatusCodes.Status400BadRequest),
        ["leaderboard.invalidCursor"] = new("leaderboard.invalidCursor", "The leaderboard cursor is invalid.", StatusCodes.Status400BadRequest),
        ["leaderboard.invalidAlias"] = new("leaderboard.invalidAlias", "The leaderboard alias is invalid.", StatusCodes.Status400BadRequest),
        ["leaderboard.aliasConflict"] = new("leaderboard.aliasConflict", "The leaderboard alias is already in use.", StatusCodes.Status409Conflict),
        ["reports.invalidQuery"] = new("reports.invalidQuery", "The report query is invalid.", StatusCodes.Status400BadRequest),
        ["permissions.rolesRequired"] = new("permissions.rolesRequired", "Roles are required.", StatusCodes.Status400BadRequest),
        ["permissions.nullRole"] = new("permissions.nullRole", "Roles must not contain null entries.", StatusCodes.Status400BadRequest),
        ["permissions.duplicateRole"] = new("permissions.duplicateRole", "Duplicate role IDs are not allowed.", StatusCodes.Status400BadRequest),
        ["permissions.roleNotInGuild"] = new("permissions.roleNotInGuild", "The role does not belong to this guild.", StatusCodes.Status400BadRequest),
        ["permissions.modulesRequired"] = new("permissions.modulesRequired", "Module IDs are required for the role.", StatusCodes.Status400BadRequest),
        ["permissions.duplicateModule"] = new("permissions.duplicateModule", "Duplicate module IDs are not allowed for the role.", StatusCodes.Status400BadRequest),
        ["permissions.unknownModule"] = new("permissions.unknownModule", "The module ID is unknown.", StatusCodes.Status400BadRequest),
        ["permissions.revisionConflict"] = new("permissions.revisionConflict", "Role permissions changed since they were loaded. Reload and try again.", StatusCodes.Status409Conflict),
        ["xp.settingsInvalid"] = new("xp.settingsInvalid", "The XP settings are invalid.", StatusCodes.Status400BadRequest),
        ["xp.settings.groupsRequired"] = new("xp.settings.groupsRequired", "All XP setting groups are required.", StatusCodes.Status400BadRequest),
        ["xp.settings.collectionsRequired"] = new("xp.settings.collectionsRequired", "All XP rule collections are required.", StatusCodes.Status400BadRequest),
        ["xp.settings.messagePoints"] = new("xp.settings.messagePoints", "Message XP values are invalid.", StatusCodes.Status400BadRequest),
        ["xp.settings.messageCharacters"] = new("xp.settings.messageCharacters", "Message character limits are invalid.", StatusCodes.Status400BadRequest),
        ["xp.settings.messageCooldown"] = new("xp.settings.messageCooldown", "Message cooldown must not be negative.", StatusCodes.Status400BadRequest),
        ["xp.settings.voicePoints"] = new("xp.settings.voicePoints", "Voice XP values must not be negative.", StatusCodes.Status400BadRequest),
        ["xp.settings.voiceTiming"] = new("xp.settings.voiceTiming", "Voice timing values are invalid.", StatusCodes.Status400BadRequest),
        ["xp.settings.reaction"] = new("xp.settings.reaction", "Reaction XP values are invalid.", StatusCodes.Status400BadRequest),
        ["xp.settings.eventInterest"] = new("xp.settings.eventInterest", "Event interest XP must not be negative.", StatusCodes.Status400BadRequest),
        ["xp.settings.thread"] = new("xp.settings.thread", "Thread XP values are invalid.", StatusCodes.Status400BadRequest),
        ["xp.settings.channelMultipliers"] = new("xp.settings.channelMultipliers", "Channel multipliers require unique channels and non-negative values.", StatusCodes.Status400BadRequest),
        ["xp.settings.levelRoles"] = new("xp.settings.levelRoles", "Level roles require unique roles and positive levels.", StatusCodes.Status400BadRequest),
        ["xp.import.guildMismatch"] = new("xp.import.guildMismatch", "The MEE6 export belongs to another guild.", StatusCodes.Status400BadRequest),
        ["xp.import.invalidPlayers"] = new("xp.import.invalidPlayers", "The MEE6 players export is invalid.", StatusCodes.Status400BadRequest)
    };

    public static IReadOnlyCollection<ApiErrorDefinition> All { get; } = Definitions.Values.ToArray();

    public static ApiErrorDefinition Get(string key) =>
        Definitions.TryGetValue(key, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown API error key.");

    public static ApiErrorDefinition ForStatusCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => Get("request.badRequest"),
        StatusCodes.Status401Unauthorized => Get("auth.unauthorized"),
        StatusCodes.Status403Forbidden => Get("auth.forbidden"),
        StatusCodes.Status404NotFound => Get("resource.notFound"),
        StatusCodes.Status405MethodNotAllowed => Get("request.methodNotAllowed"),
        StatusCodes.Status415UnsupportedMediaType => Get("request.unsupportedMediaType"),
        StatusCodes.Status429TooManyRequests => Get("rateLimit.exceeded"),
        >= 400 and < 500 => Get("request.rejected"),
        _ => Get("server.internal")
    };
}

public static class ApiErrorFactory
{
    public static ApiErrorResponse Create(
        HttpContext context,
        string errorKey,
        IReadOnlyDictionary<string, object?>? parameters = null,
        IReadOnlyDictionary<string, IReadOnlyList<ApiValidationError>>? errors = null)
    {
        var definition = ApiErrorCatalog.Get(errorKey);
        return new(definition.Key, definition.Message, context.TraceIdentifier, parameters, errors);
    }

    public static ObjectResult Result(
        HttpContext context,
        string errorKey,
        IReadOnlyDictionary<string, object?>? parameters = null,
        IReadOnlyDictionary<string, IReadOnlyList<ApiValidationError>>? errors = null,
        int? statusCode = null)
    {
        var definition = ApiErrorCatalog.Get(errorKey);
        return new ObjectResult(Create(context, errorKey, parameters, errors)) { StatusCode = statusCode ?? definition.StatusCode };
    }

    public static Task WriteAsync(HttpContext context, string errorKey, IReadOnlyDictionary<string, object?>? parameters = null, int? statusCode = null)
    {
        var definition = ApiErrorCatalog.Get(errorKey);
        context.Response.StatusCode = statusCode ?? definition.StatusCode;
        return context.Response.WriteAsJsonAsync(Create(context, errorKey, parameters), cancellationToken: context.RequestAborted);
    }

    public static ApiValidationError Validation(string errorKey, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var definition = ApiErrorCatalog.Get(errorKey);
        return new(definition.Key, definition.Message, parameters);
    }
}

public static class ApiControllerErrorExtensions
{
    public static ObjectResult ApiError(
        this ControllerBase controller,
        string errorKey,
        IReadOnlyDictionary<string, object?>? parameters = null,
        IReadOnlyDictionary<string, IReadOnlyList<ApiValidationError>>? errors = null) =>
        ApiErrorFactory.Result(controller.HttpContext, errorKey, parameters, errors);
}

public sealed class ApiErrorResultFilter : IAsyncAlwaysRunResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/api") &&
            context.Result is IStatusCodeActionResult statusResult &&
            statusResult.StatusCode is >= 400 and <= 599 &&
            context.Result is not ObjectResult { Value: ApiErrorResponse })
        {
            var definition = ApiErrorCatalog.ForStatusCode(statusResult.StatusCode.Value);
            context.Result = ApiErrorFactory.Result(context.HttpContext, definition.Key, statusCode: statusResult.StatusCode.Value);
        }

        return next();
    }
}
