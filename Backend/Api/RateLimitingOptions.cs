using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace Rankoon.Api;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public int RejectionRetryAfterSeconds { get; init; } = 60;
    public string[] TrustedProxyIps { get; init; } = [];
    public RateLimitPolicyOptions Leaderboard { get; init; } = new() { PermitLimit = 90 };
    public RateLimitPolicyOptions Reports { get; init; } = new() { PermitLimit = 60 };
    public RateLimitPolicyOptions BotManagement { get; init; } = new() { PermitLimit = 30 };
    public RateLimitPolicyOptions OAuthLogin { get; init; } = new() { PermitLimit = 10 };
    public RateLimitPolicyOptions OAuthCallback { get; init; } = new() { PermitLimit = 10 };
    public RateLimitPolicyOptions OAuthRefresh { get; init; } = new() { PermitLimit = 12 };
    public RateLimitPolicyOptions OAuthLogout { get; init; } = new() { PermitLimit = 12 };
    public RateLimitPolicyOptions CustomBotValidation { get; init; } = new() { PermitLimit = 6, ConcurrencyLimit = 1 };
    public RateLimitPolicyOptions CustomBotSave { get; init; } = new() { PermitLimit = 6, ConcurrencyLimit = 1 };
    public RateLimitPolicyOptions CustomBotActivate { get; init; } = new() { PermitLimit = 3, ConcurrencyLimit = 1 };
    public RateLimitPolicyOptions CustomBotRestart { get; init; } = new() { PermitLimit = 3, ConcurrencyLimit = 1 };
    public RateLimitPolicyOptions XpImport { get; init; } = new() { PermitLimit = 3, ConcurrencyLimit = 1 };
    public RateLimitPolicyOptions XpSettings { get; init; } = new() { PermitLimit = 10, ConcurrencyLimit = 1 };
}

public sealed class RateLimitPolicyOptions
{
    public int PermitLimit { get; init; } = 30;
    public int WindowSeconds { get; init; } = 60;
    public int QueueLimit { get; init; } = 2;
    public int? ConcurrencyLimit { get; init; }
}

public static class RateLimitPolicies
{
    public const string Leaderboard = "leaderboard";
    public const string Reports = "reports";
    public const string BotManagement = "bot-management";
    public const string OAuthLogin = "oauth-login";
    public const string OAuthCallback = "oauth-callback";
    public const string OAuthRefresh = "oauth-refresh";
    public const string OAuthLogout = "oauth-logout";
    public const string CustomBotValidation = "custom-bot-validation";
    public const string CustomBotSave = "custom-bot-save";
    public const string CustomBotActivate = "custom-bot-activate";
    public const string CustomBotRestart = "custom-bot-restart";
    public const string XpImport = "xp-import";
    public const string XpSettings = "xp-settings";

    public static RateLimitPartition<string> Create(HttpContext context, string policy, RateLimitPolicyOptions options) =>
        RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context, policy), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.PermitLimit,
            Window = TimeSpan.FromSeconds(options.WindowSeconds),
            QueueLimit = options.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

    public static RateLimitPartition<string> CreateConcurrency(HttpContext context, RateLimitingOptions options)
    {
        var policy = context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName;
        var configured = policy switch
        {
            CustomBotValidation => options.CustomBotValidation,
            CustomBotSave => options.CustomBotSave,
            CustomBotActivate => options.CustomBotActivate,
            CustomBotRestart => options.CustomBotRestart,
            XpImport => options.XpImport,
            XpSettings => options.XpSettings,
            _ => null
        };
        if (configured?.ConcurrencyLimit is not { } concurrency) return RateLimitPartition.GetNoLimiter("not-costly");
        var guild = context.Request.RouteValues.TryGetValue("guildId", out var value) ? Convert.ToString(value) : "none";
        return RateLimitPartition.GetConcurrencyLimiter($"concurrency|{policy}|guild:{guild}", _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = concurrency,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    // The cookie contributes only a one-way digest; refresh credentials never become limiter keys.
    internal static string PartitionKey(HttpContext context, string policy)
    {
        var user = context.User.FindFirst("discord_id")?.Value ?? "anonymous";
        var guild = context.Request.RouteValues.TryGetValue("guildId", out var value) ? Convert.ToString(value) : null;
        var ip = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
        var refreshCookie = context.Request.Cookies["refresh_token"];
        var refresh = string.IsNullOrEmpty(refreshCookie) ? "" : $"|refresh:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshCookie)))}";
        return $"{policy}|user:{user}|guild:{guild ?? "none"}|ip:{ip}{refresh}";
    }

    public static bool IsValid(RateLimitingOptions options) =>
        options.RejectionRetryAfterSeconds is > 0 and <= 3600 &&
        options.TrustedProxyIps.All(ip => IPAddress.TryParse(ip, out _)) &&
        AllPolicies(options).All(IsValid);

    public static IEnumerable<RateLimitPolicyOptions> AllPolicies(RateLimitingOptions options) =>
    [
        options.Leaderboard, options.Reports, options.BotManagement,
        options.OAuthLogin, options.OAuthCallback, options.OAuthRefresh, options.OAuthLogout,
        options.CustomBotValidation, options.CustomBotSave, options.CustomBotActivate, options.CustomBotRestart,
        options.XpImport, options.XpSettings
    ];

    private static bool IsValid(RateLimitPolicyOptions options) =>
        options.PermitLimit is > 0 and <= 10_000 &&
        options.WindowSeconds is > 0 and <= 86_400 &&
        options.QueueLimit is >= 0 and <= 1_000 &&
        options.ConcurrencyLimit is null or > 0 and <= 100;
}
