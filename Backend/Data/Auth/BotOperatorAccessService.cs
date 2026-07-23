using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Rankoon.Api;

namespace Rankoon.Data.Auth;

public enum BotOperatorRole
{
    ApplicationOwner,
    TeamOwner,
    TeamAdmin,
    TeamDeveloper,
    TeamReadOnly
}

public sealed record BotOperatorAccessResult(bool IsAuthorized, BotOperatorRole? Role, bool IsAvailable = true);

public interface IBotOperatorAccessService
{
    Task<BotOperatorAccessResult> GetAccessAsync(ulong discordUserId, CancellationToken cancellationToken = default);
    Task WarmAsync(CancellationToken cancellationToken = default);
}

/// <summary>Resolves Discord application operators without exposing application metadata to callers.</summary>
public sealed class BotOperatorAccessService(DiscordShardedClient discord, TimeProvider timeProvider, ILogger<BotOperatorAccessService> logger) : IBotOperatorAccessService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private CachedApplication? cache;

    public async Task<BotOperatorAccessResult> GetAccessAsync(ulong discordUserId, CancellationToken cancellationToken = default)
    {
        var current = Volatile.Read(ref cache);
        if (current is null || current.ExpiresAt <= timeProvider.GetUtcNow())
        {
            await refreshLock.WaitAsync(cancellationToken);
            try
            {
                current = cache;
                if (current is null || current.ExpiresAt <= timeProvider.GetUtcNow())
                {
                    try
                    {
                        var application = await discord.GetApplicationInfoAsync(new RequestOptions { CancelToken = cancellationToken });
                        current = CreateCache(application, timeProvider.GetUtcNow().Add(CacheLifetime));
                        Volatile.Write(ref cache, current);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning(exception, "Discord application information could not be refreshed");
                        current = cache;
                    }
                }
            }
            finally { refreshLock.Release(); }
        }

        if (current is null) return new(false, null, false);
        return current.Roles.TryGetValue(discordUserId, out var role) ? new(true, role) : new(false, null);
    }

    public async Task WarmAsync(CancellationToken cancellationToken = default)
    {
        await RefreshIfNeededAsync(cancellationToken);
    }

    private async Task RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref cache);
        if (current is not null && current.ExpiresAt > timeProvider.GetUtcNow()) return;
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            current = cache;
            if (current is not null && current.ExpiresAt > timeProvider.GetUtcNow()) return;
            try
            {
                var application = await discord.GetApplicationInfoAsync(new RequestOptions { CancelToken = cancellationToken });
                Volatile.Write(ref cache, CreateCache(application, timeProvider.GetUtcNow().Add(CacheLifetime)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Discord application information could not be refreshed");
            }
        }
        finally { refreshLock.Release(); }
    }

    private static CachedApplication CreateCache(IApplication application, DateTimeOffset expiresAt)
    {
        var roles = new Dictionary<ulong, BotOperatorRole> { [application.Owner.Id] = BotOperatorRole.ApplicationOwner };
        if (application.Team != null)
        {
            foreach (var member in application.Team.TeamMembers)
            {
                // Discord already returns only actual application-team members. Role names differ
                // between Discord.Net versions, so role parsing must never decide access.
                var roleName = member.Role.ToString();
                var role = roleName.Contains("Owner", StringComparison.OrdinalIgnoreCase) ? BotOperatorRole.TeamOwner
                    : roleName.Contains("Admin", StringComparison.OrdinalIgnoreCase) ? BotOperatorRole.TeamAdmin
                    : roleName.Contains("Developer", StringComparison.OrdinalIgnoreCase) ? BotOperatorRole.TeamDeveloper
                    : roleName.Contains("Read", StringComparison.OrdinalIgnoreCase) ? BotOperatorRole.TeamReadOnly
                    : BotOperatorRole.TeamReadOnly;
                roles[member.User.Id] = role;
            }
        }
        return new(roles, expiresAt);
    }

    private sealed record CachedApplication(IReadOnlyDictionary<ulong, BotOperatorRole> Roles, DateTimeOffset ExpiresAt);
}

public static class AuthorizationPolicies
{
    public const string BotOperator = "botOperator";
}

public sealed class BotOperatorRequirement : IAuthorizationRequirement;

public sealed class BotOperatorAuthorizationHandler(IBotOperatorAccessService access) : AuthorizationHandler<BotOperatorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, BotOperatorRequirement requirement)
    {
        if (!ulong.TryParse(context.User.FindFirst("discord_id")?.Value, out var userId)) return;
        var result = await access.GetAccessAsync(userId);
        if (!result.IsAvailable && context.Resource is HttpContext httpContext) httpContext.Items["BotOperatorUnavailable"] = true;
        if (result.IsAuthorized) context.Succeed(requirement);
    }
}

public sealed class BotOperatorAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && context.Items.ContainsKey("BotOperatorUnavailable"))
            return ApiErrorFactory.WriteAsync(context, "botManagement.unavailable");
        return fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
