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
        ["customBotIdentity.disabled"] = new("customBotIdentity.disabled", "Custom Bot Identity is disabled on this Rankoon instance.", StatusCodes.Status403Forbidden),
        ["customBotIdentity.guildNotAllowed"] = new("customBotIdentity.guildNotAllowed", "This guild is not allowed to use Custom Bot Identity.", StatusCodes.Status403Forbidden),
        ["customBotIdentity.capacityReached"] = new("customBotIdentity.capacityReached", "Custom Bot Identity is currently at capacity.", StatusCodes.Status409Conflict),
        ["customBotIdentity.tokenInvalid"] = new("customBotIdentity.tokenInvalid", "The bot token is invalid or revoked.", StatusCodes.Status400BadRequest),
        ["customBotIdentity.tokenAlreadyAssigned"] = new("customBotIdentity.tokenAlreadyAssigned", "This bot token is already assigned to another guild.", StatusCodes.Status409Conflict),
        ["customBotIdentity.tokenApplicationMismatch"] = new("customBotIdentity.tokenApplicationMismatch", "An active identity can only rotate a token for the same Discord application.", StatusCodes.Status409Conflict),
        ["customBotIdentity.botNotInstalled"] = new("customBotIdentity.botNotInstalled", "The custom bot is not installed in this guild.", StatusCodes.Status400BadRequest),
        ["customBotIdentity.missingIntents"] = new("customBotIdentity.missingIntents", "Required privileged gateway intents are not enabled.", StatusCodes.Status400BadRequest),
        ["customBotIdentity.missingPermissions"] = new("customBotIdentity.missingPermissions", "The custom bot is missing required guild permissions.", StatusCodes.Status400BadRequest),
        ["customBotIdentity.roleHierarchyInvalid"] = new("customBotIdentity.roleHierarchyInvalid", "The custom bot role is below a managed Rankoon role.", StatusCodes.Status400BadRequest),
        ["customBotIdentity.runtimeStartFailed"] = new("customBotIdentity.runtimeStartFailed", "The custom bot runtime could not be started.", StatusCodes.Status400BadRequest),
        ["customBotIdentity.commandRegistrationFailed"] = new("customBotIdentity.commandRegistrationFailed", "Rankoon commands could not be registered for the custom bot.", StatusCodes.Status400BadRequest),
        ["customBotIdentity.selfRoleMigrationFailed"] = new("customBotIdentity.selfRoleMigrationFailed", "Self-role panels could not be migrated safely.", StatusCodes.Status409Conflict),
        ["customBotIdentity.revisionConflict"] = new("customBotIdentity.revisionConflict", "The bot identity changed since it was loaded. Reload and try again.", StatusCodes.Status409Conflict),
        ["customBotIdentity.ownerRequired"] = new("customBotIdentity.ownerRequired", "Only the guild owner can manage Custom Bot Identity.", StatusCodes.Status403Forbidden),
        ["customBotIdentity.platformBotDepartureFailed"] = new("customBotIdentity.platformBotDepartureFailed", "The custom bot is active, but the official Rankoon bot could not leave the guild yet.", StatusCodes.Status409Conflict),
        ["customBotIdentity.platformBotNotInstalled"] = new("customBotIdentity.platformBotNotInstalled", "Install the official Rankoon bot before returning to the platform identity.", StatusCodes.Status409Conflict),
        ["customBotIdentity.authoritativeRuntimeUnavailable"] = new("customBotIdentity.authoritativeRuntimeUnavailable", "The authoritative bot runtime is currently unavailable.", StatusCodes.Status409Conflict),
        ["customBotIdentity.handoverIncomplete"] = new("customBotIdentity.handoverIncomplete", "The custom bot handover has not been completed yet.", StatusCodes.Status409Conflict),
        ["customBotIdentity.returnToPlatformRequired"] = new("customBotIdentity.returnToPlatformRequired", "Return to the official Rankoon bot before completing this operation.", StatusCodes.Status409Conflict),
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
        ["leaderboard.invalidSeason"] = new("leaderboard.invalidSeason", "The requested season is not available on this leaderboard.", StatusCodes.Status400BadRequest),
        ["reports.invalidQuery"] = new("reports.invalidQuery", "The report query is invalid.", StatusCodes.Status400BadRequest),
        ["botManagement.invalidRange"] = new("botManagement.invalidRange", "The bot management range is invalid.", StatusCodes.Status400BadRequest),
        ["botManagement.unavailable"] = new("botManagement.unavailable", "Bot management is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable),
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
        ["xp.settings.serverBoosterRequired"] = new("xp.settings.serverBoosterRequired", "Server booster settings and tiers are required.", StatusCodes.Status400BadRequest),
        ["xp.settings.serverBoosterTierCount"] = new("xp.settings.serverBoosterTierCount", "At most 10 server booster tiers are allowed.", StatusCodes.Status400BadRequest),
        ["xp.settings.serverBoosterMonths"] = new("xp.settings.serverBoosterMonths", "Boost months must be a non-negative whole number.", StatusCodes.Status400BadRequest),
        ["xp.settings.serverBoosterMultiplier"] = new("xp.settings.serverBoosterMultiplier", "Booster multipliers must be between 1.00 and 10.00 with at most two decimal places.", StatusCodes.Status400BadRequest),
        ["xp.settings.serverBoosterDuplicateMonths"] = new("xp.settings.serverBoosterDuplicateMonths", "Server booster month thresholds must be unique.", StatusCodes.Status400BadRequest),
        ["xp.settings.serverBoosterOrder"] = new("xp.settings.serverBoosterOrder", "Server booster multipliers must not decrease as boost duration increases.", StatusCodes.Status400BadRequest),
        ["xp.import.guildMismatch"] = new("xp.import.guildMismatch", "The MEE6 export belongs to another guild.", StatusCodes.Status400BadRequest),
        ["xp.import.invalidPlayers"] = new("xp.import.invalidPlayers", "The MEE6 players export is invalid.", StatusCodes.Status400BadRequest),
        ["xpAudit.invalidUserId"] = new("xpAudit.invalidUserId", "The user ID is invalid.", StatusCodes.Status400BadRequest),
        ["xpAudit.memberNotFound"] = new("xpAudit.memberNotFound", "The XP member was not found.", StatusCodes.Status404NotFound),
        ["xpAudit.invalidCursor"] = new("xpAudit.invalidCursor", "The XP audit cursor is invalid.", StatusCodes.Status400BadRequest),
        ["xpAudit.invalidFilter"] = new("xpAudit.invalidFilter", "The XP audit filter is invalid.", StatusCodes.Status400BadRequest),
        ["xpAdjustment.selfAdjustmentForbidden"] = new("xpAdjustment.selfAdjustmentForbidden", "Non-owners cannot adjust their own XP.", StatusCodes.Status403Forbidden),
        ["xpAdjustment.amountRequired"] = new("xpAdjustment.amountRequired", "An adjustment amount is required.", StatusCodes.Status400BadRequest),
        ["xpAdjustment.amountOutOfRange"] = new("xpAdjustment.amountOutOfRange", "The adjustment amount is out of range.", StatusCodes.Status400BadRequest),
        ["xpAdjustment.reasonRequired"] = new("xpAdjustment.reasonRequired", "A reason is required.", StatusCodes.Status400BadRequest),
        ["xpAdjustment.reasonTooShort"] = new("xpAdjustment.reasonTooShort", "The reason is too short.", StatusCodes.Status400BadRequest),
        ["xpAdjustment.reasonTooLong"] = new("xpAdjustment.reasonTooLong", "The reason is too long.", StatusCodes.Status400BadRequest),
        ["xpAdjustment.referenceTooLong"] = new("xpAdjustment.referenceTooLong", "The reference is too long.", StatusCodes.Status400BadRequest),
        ["xpAdjustment.invalidRequestId"] = new("xpAdjustment.invalidRequestId", "The request ID is invalid.", StatusCodes.Status400BadRequest),
        ["xpAdjustment.requestConflict"] = new("xpAdjustment.requestConflict", "The request ID was already used with different content.", StatusCodes.Status409Conflict),
        ["xpAdjustment.entryNotFound"] = new("xpAdjustment.entryNotFound", "The XP adjustment was not found.", StatusCodes.Status404NotFound),
        ["xpAdjustment.notManual"] = new("xpAdjustment.notManual", "Only manual adjustments can be reversed.", StatusCodes.Status400BadRequest),
        ["xpAdjustment.alreadyReversed"] = new("xpAdjustment.alreadyReversed", "The adjustment has already been reversed.", StatusCodes.Status409Conflict),
        ["season.invalidTimeZone"] = new("season.invalidTimeZone", "The season time zone is invalid.", StatusCodes.Status400BadRequest),
        ["season.invalidSchedule"] = new("season.invalidSchedule", "The season schedule or naming template is invalid.", StatusCodes.Status400BadRequest),
        ["season.invalidTransition"] = new("season.invalidTransition", "The requested season status transition is not allowed.", StatusCodes.Status409Conflict),
        ["season.activeConflict"] = new("season.activeConflict", "Another season is already active for this guild.", StatusCodes.Status409Conflict),
        ["season.manualSchedule"] = new("season.manualSchedule", "Manual schedules must be created with explicit dates.", StatusCodes.Status400BadRequest),
        ["season.planConflict"] = new("season.planConflict", "The requested seasons overlap with the existing plan.", StatusCodes.Status409Conflict),
        ["season.notResumable"] = new("season.notResumable", "This season can only be resumed during its original time range.", StatusCodes.Status409Conflict),
        ["selfRoles.invalidPanel"] = new("selfRoles.invalidPanel", "The self-role panel is invalid.", StatusCodes.Status400BadRequest),
        ["selfRoles.tooManyMappings"] = new("selfRoles.tooManyMappings", "A self-role panel supports at most 20 mappings.", StatusCodes.Status400BadRequest),
        ["selfRoles.duplicateEmoji"] = new("selfRoles.duplicateEmoji", "Each self-role mapping must use a unique emoji.", StatusCodes.Status400BadRequest),
        ["selfRoles.roleNotManageable"] = new("selfRoles.roleNotManageable", "The selected role cannot be managed by the bot.", StatusCodes.Status400BadRequest),
        ["selfRoles.channelNotUsable"] = new("selfRoles.channelNotUsable", "The selected channel cannot receive self-role messages.", StatusCodes.Status400BadRequest),
        ["selfRoles.discordPermissions"] = new("selfRoles.discordPermissions", "The bot is missing permissions in the selected channel. Grant View Channel, Send Messages, Embed Links, Add Reactions, Read Message History, and Manage Messages.", StatusCodes.Status400BadRequest),
        ["selfRoles.emojiInvalid"] = new("selfRoles.emojiInvalid", "A self-role mapping contains an invalid emoji.", StatusCodes.Status400BadRequest),
        ["selfRoles.emojiRejected"] = new("selfRoles.emojiRejected", "Discord rejected a self-role emoji.", StatusCodes.Status400BadRequest),
        ["selfRoles.revisionConflict"] = new("selfRoles.revisionConflict", "The self-role panel changed since it was loaded. Reload and try again.", StatusCodes.Status409Conflict)
        , ["levelAnnouncements.revisionConflict"] = new("levelAnnouncements.revisionConflict", "Level-up announcement settings changed since they were loaded.", StatusCodes.Status409Conflict)
        , ["levelAnnouncements.settingsInvalid"] = new("levelAnnouncements.settingsInvalid", "Level-up announcement settings are invalid.", StatusCodes.Status400BadRequest)
        , ["levelAnnouncements.channelUnavailable"] = new("levelAnnouncements.channelUnavailable", "The configured channel is unavailable.", StatusCodes.Status400BadRequest)
        , ["levelAnnouncements.templateInvalid"] = new("levelAnnouncements.templateInvalid", "The template cannot be rendered.", StatusCodes.Status400BadRequest)
        ,["selfRoles.tooManyEmbeds"] = new("selfRoles.tooManyEmbeds", "A self-role panel supports at most 10 embeds.", StatusCodes.Status400BadRequest)
        ,["selfRoles.invalidEmbedStructure"] = new("selfRoles.invalidEmbedStructure", "The embed structure is invalid.", StatusCodes.Status400BadRequest)
        ,["selfRoles.missingRoleMappingsEmbed"] = new("selfRoles.missingRoleMappingsEmbed", "Exactly one role-mappings embed is required.", StatusCodes.Status400BadRequest)
        ,["selfRoles.tooManyFields"] = new("selfRoles.tooManyFields", "An embed supports at most 25 fields.", StatusCodes.Status400BadRequest)
        ,["selfRoles.embedTitleTooLong"] = new("selfRoles.embedTitleTooLong", "An embed title must not exceed 256 characters.", StatusCodes.Status400BadRequest)
        ,["selfRoles.embedDescriptionTooLong"] = new("selfRoles.embedDescriptionTooLong", "An embed description must not exceed 4096 characters.", StatusCodes.Status400BadRequest)
        ,["selfRoles.fieldNameRequired"] = new("selfRoles.fieldNameRequired", "An embed field name is required.", StatusCodes.Status400BadRequest)
        ,["selfRoles.fieldValueRequired"] = new("selfRoles.fieldValueRequired", "An embed field value is required.", StatusCodes.Status400BadRequest)
        ,["selfRoles.fieldNameTooLong"] = new("selfRoles.fieldNameTooLong", "An embed field name must not exceed 256 characters.", StatusCodes.Status400BadRequest)
        ,["selfRoles.fieldValueTooLong"] = new("selfRoles.fieldValueTooLong", "An embed field value must not exceed 1024 characters.", StatusCodes.Status400BadRequest)
        ,["selfRoles.embedTextTooLong"] = new("selfRoles.embedTextTooLong", "Embed text must not exceed 6000 characters per message.", StatusCodes.Status400BadRequest)
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
