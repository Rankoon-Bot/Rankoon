using Discord;
using Discord.WebSocket;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;
using Rankoon.Data.Utils;
using Rankoon.Api;
using Rankoon.Hubs;
using Serilog;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

// Load environment variables from .env file
Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Expand environment variables in configuration
ConfigurationHelper.ExpandEnvironmentVariables(builder.Configuration);

// Add services to the container.
builder.Services.AddControllers(options => options.Filters.Add<ApiErrorResultFilter>()).AddJsonOptions(options =>
{
    options.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString | System.Text.Json.Serialization.JsonNumberHandling.WriteAsString;
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
});
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var malformedJson = context.ModelState.Any(entry =>
            entry.Value?.Errors.Count > 0 &&
            (entry.Key.StartsWith('$') || entry.Value.Errors.Any(error => IsJsonException(error.Exception))));
        var validationError = ApiErrorFactory.Validation("request.validationFailed");
        var errors = context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .ToDictionary(
                entry => string.IsNullOrWhiteSpace(entry.Key) ? "$" : entry.Key,
                entry => (IReadOnlyList<ApiValidationError>)entry.Value!.Errors.Select(_ => validationError).ToArray(),
                StringComparer.Ordinal);
        return ApiErrorFactory.Result(
            context.HttpContext,
            malformedJson ? "request.malformedJson" : "request.validationFailed",
            errors: errors);
    };
});
builder.Services.Configure<RouteOptions>(options =>
    options.ConstraintMap["nonApi"] = typeof(NonApiPathRouteConstraint));
builder.Services.AddAuthorization();
builder.Services.AddSignalR().AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddSingleton<LeaderboardSubscriptionRegistry>();
var leaderboardPermitLimit = builder.Configuration.GetValue("RateLimiting:LeaderboardPermitLimit", 90);
var reportsPermitLimit = builder.Configuration.GetValue("RateLimiting:ReportsPermitLimit", 60);
var rateLimitQueueLimit = builder.Configuration.GetValue("RateLimiting:QueueLimit", 2);
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, cancellationToken) =>
    {
        IReadOnlyDictionary<string, object?>? parameters = null;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters = new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds };
        }
        await ApiErrorFactory.WriteAsync(context.HttpContext, "rateLimit.exceeded", parameters);
    };
    options.AddPolicy("leaderboard", context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("discord_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = leaderboardPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = rateLimitQueueLimit }));
    options.AddPolicy("reports", context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("discord_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = reportsPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = rateLimitQueueLimit }));
});
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Configure settings
ConfigureAppSettings(builder);

var dcConfig = new DiscordSocketConfig()
{
    LogLevel = LogSeverity.Info,
    MessageCacheSize = 1024,
    AuditLogCacheSize = 1024,
    AlwaysDownloadUsers = false,
    AlwaysDownloadDefaultStickers = false,
    TotalShards = 1,
    UseInteractionSnowflakeDate = false,
    // MessageContent is required for length-based message XP and must be enabled in the Discord Developer Portal.
    GatewayIntents = GatewayIntents.Guilds
        | GatewayIntents.GuildVoiceStates
        | GatewayIntents.GuildMessages
        | GatewayIntents.GuildMessageReactions
        | GatewayIntents.GuildScheduledEvents
        | GatewayIntents.GuildMembers
        | GatewayIntents.MessageContent
};
builder.Services.AddSingleton(new DiscordShardedClient(dcConfig));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

// Register database context
builder.Services.AddSingleton<RankoonDbContext>();
builder.Services.AddSingleton<ReportWriter>();
builder.Services.AddSingleton<IReportWriter>(services => services.GetRequiredService<ReportWriter>());
builder.Services.AddSingleton<IReportQueryService, ReportQueryService>();

// Register HTTP client for Discord API calls
builder.Services.AddHttpClient<IDiscordService, DiscordService>();

// Register our services
builder.Services.AddSingleton<IBotInfoCache, BotInfoCache>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IGuildAuthorizationService, GuildAuthorizationService>();
builder.Services.AddSingleton<IGuildModuleRegistry, GuildModuleRegistry>();
builder.Services.AddSingleton<IGuildRolePermissionService, GuildRolePermissionService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.XpService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.IXpService>(services => services.GetRequiredService<Rankoon.Data.Xp.XpService>());
builder.Services.AddSingleton<Rankoon.Data.Xp.IXpAuditService, Rankoon.Data.Xp.XpAuditService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ISeasonService, Rankoon.Data.Xp.SeasonService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ISeasonLifecycleService, Rankoon.Data.Xp.SeasonLifecycleService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.LedgerProjectionRepairService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.SeasonCoordinator>();
builder.Services.AddSingleton<Rankoon.Data.Xp.LevelRoleService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.LeaderboardService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ILeaderboardRealtimePublisher, Rankoon.Data.Xp.LeaderboardRealtimePublisher>();
builder.Services.AddSingleton<VoiceXpWatchdog>();
builder.Services.AddSingleton<VcHubService>();
builder.Services.AddSingleton<GuildMembershipService>();
builder.Services.AddSingleton<RankoonBotHostedService>();
builder.Services.AddSingleton<SelfRoleService>();
builder.Services.AddSingleton<SelfRoleReactionService>();

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService(services => services.GetRequiredService<ReportWriter>());
    builder.Services.AddHostedService<MongoIndexInitializer>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Rankoon.Data.Xp.LedgerProjectionRepairService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Rankoon.Data.Xp.SeasonCoordinator>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<RankoonBotHostedService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<VoiceXpWatchdog>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<VcHubService>());
    builder.Services.AddHostedService<ActivityXpEventService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<SelfRoleReactionService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<GuildMembershipService>());
    builder.Services.AddHostedService<RankoonCommandService>();
}

// Configure JWT authentication
var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>();
if (jwtSettings == null || string.IsNullOrEmpty(jwtSettings.SecretKey))
{
    throw new InvalidOperationException("JWT settings are not properly configured");
}

var key = Encoding.ASCII.GetBytes(jwtSettings.SecretKey);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false; // Set to true in production
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = jwtSettings.Issuer,
        ValidateAudience = true,
        ValidAudience = jwtSettings.Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            if (context.Request.Path.StartsWithSegments("/hubs/leaderboard") && context.Request.Query.TryGetValue("access_token", out var token))
                context.Token = token;
            return Task.CompletedTask;
        },
        OnChallenge = async context =>
        {
            context.HandleResponse();
            await ApiErrorFactory.WriteAsync(context.HttpContext, "auth.unauthorized");
        },
        OnForbidden = context => ApiErrorFactory.WriteAsync(context.HttpContext, "auth.forbidden")
    };
});

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("ApiExceptionHandler")
        .LogError(exception, "Unhandled exception for {Method} {Path}; trace ID {TraceId}", context.Request.Method, context.Request.Path, context.TraceIdentifier);
    if (!context.Response.HasStarted)
    {
        context.Response.Clear();
        await ApiErrorFactory.WriteAsync(context, "server.internal");
    }
}));
app.UseStatusCodePages(async statusContext =>
{
    var context = statusContext.HttpContext;
    if (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        var statusCode = context.Response.StatusCode;
        var definition = ApiErrorCatalog.ForStatusCode(statusCode);
        await ApiErrorFactory.WriteAsync(context, definition.Key, statusCode: statusCode);
    }
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHub<LeaderboardHub>("/hubs/leaderboard");
if (Directory.Exists(app.Environment.WebRootPath))
{
    var webRootFileProvider = new PhysicalFileProvider(app.Environment.WebRootPath);
    var staticFileOptions = new StaticFileOptions { FileProvider = webRootFileProvider };
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRootFileProvider });
    app.UseStaticFiles(staticFileOptions);
    app.MapFallbackToFile("{*path:nonApi}", "index.html", staticFileOptions);
}

try
{
    await app.RunAsync();
}
catch (IOException exception) when (exception.GetBaseException() is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
{
    Console.Error.WriteLine("Backend konnte nicht gestartet werden: Die konfigurierte HTTP-Adresse wird bereits verwendet. Pruefe, ob Rankoon bereits laeuft, oder konfiguriere ASPNETCORE_URLS.");
    Environment.ExitCode = 1;
}


static void ConfigureAppSettings(WebApplicationBuilder builder)
{
    builder.Services.AddOptions<VoiceWatchdogOptions>()
        .Bind(builder.Configuration.GetSection(VoiceWatchdogOptions.SectionName))
        .Validate(options => options.IntervalSeconds > 0, "VoiceWatchdog:IntervalSeconds must be greater than zero.")
        .ValidateOnStart();
    builder.Services.Configure<MongoDbSettings>(
        builder.Configuration.GetSection(MongoDbSettings.SectionName));
    builder.Services.Configure<DiscordSettings>(
        builder.Configuration.GetSection(DiscordSettings.SectionName));
    builder.Services.Configure<JwtSettings>(
        builder.Configuration.GetSection(JwtSettings.SectionName));
    builder.Services.Configure<FrontendSettings>(
        builder.Configuration.GetSection(FrontendSettings.SectionName));
}

static bool IsJsonException(Exception? exception)
{
    while (exception != null)
    {
        if (exception is JsonException or InputFormatterException) return true;
        exception = exception.InnerException;
    }
    return false;
}

public partial class Program;
