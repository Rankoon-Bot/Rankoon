using Discord;
using Discord.WebSocket;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.Diagnostics;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;
using Rankoon.Data.Analytics;
using Rankoon.Data.Operations;
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
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ApiErrorResultFilter>();
    options.Conventions.Add(new DevelopmentOnlyControllerConvention(builder.Environment));
}).AddJsonOptions(options =>
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
builder.Services.AddAuthorization(options => options.AddPolicy(AuthorizationPolicies.BotOperator, policy =>
    policy.RequireAuthenticatedUser().AddRequirements(new BotOperatorRequirement())));
builder.Services.AddSingleton<IAuthorizationHandler, BotOperatorAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, BotOperatorAuthorizationResultHandler>();
builder.Services.AddSignalR(options => options.MaximumParallelInvocationsPerClient = 1).AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddSingleton<LeaderboardSubscriptionRegistry>();
var rateLimiting = builder.Configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
builder.Services.AddOptions<RateLimitingOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitingOptions.SectionName))
    .Validate(RateLimitPolicies.IsValid, "RateLimiting contains an invalid limit, duration, queue size, concurrency limit, retry interval, or trusted proxy IP.")
    .ValidateOnStart();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var proxy in rateLimiting.TrustedProxyIps)
        if (System.Net.IPAddress.TryParse(proxy, out var address)) options.KnownProxies.Add(address);
});
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPolicies.CreateConcurrency(context, rateLimiting));
    options.OnRejected = async (context, cancellationToken) =>
    {
        var seconds = rateLimiting.RejectionRetryAfterSeconds;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        }
        await ApiErrorFactory.WriteRateLimitedAsync(context.HttpContext, seconds);
    };
    options.AddPolicy(RateLimitPolicies.Leaderboard, context => RateLimitPolicies.Create(context, RateLimitPolicies.Leaderboard, rateLimiting.Leaderboard));
    options.AddPolicy(RateLimitPolicies.Reports, context => RateLimitPolicies.Create(context, RateLimitPolicies.Reports, rateLimiting.Reports));
    options.AddPolicy(RateLimitPolicies.BotManagement, context => RateLimitPolicies.Create(context, RateLimitPolicies.BotManagement, rateLimiting.BotManagement));
    options.AddPolicy(RateLimitPolicies.OAuthLogin, context => RateLimitPolicies.Create(context, RateLimitPolicies.OAuthLogin, rateLimiting.OAuthLogin));
    options.AddPolicy(RateLimitPolicies.OAuthCallback, context => RateLimitPolicies.Create(context, RateLimitPolicies.OAuthCallback, rateLimiting.OAuthCallback));
    options.AddPolicy(RateLimitPolicies.OAuthRefresh, context => RateLimitPolicies.Create(context, RateLimitPolicies.OAuthRefresh, rateLimiting.OAuthRefresh));
    options.AddPolicy(RateLimitPolicies.OAuthLogout, context => RateLimitPolicies.Create(context, RateLimitPolicies.OAuthLogout, rateLimiting.OAuthLogout));
    options.AddPolicy(RateLimitPolicies.CustomBotValidation, context => RateLimitPolicies.Create(context, RateLimitPolicies.CustomBotValidation, rateLimiting.CustomBotValidation));
    options.AddPolicy(RateLimitPolicies.CustomBotSave, context => RateLimitPolicies.Create(context, RateLimitPolicies.CustomBotSave, rateLimiting.CustomBotSave));
    options.AddPolicy(RateLimitPolicies.CustomBotActivate, context => RateLimitPolicies.Create(context, RateLimitPolicies.CustomBotActivate, rateLimiting.CustomBotActivate));
    options.AddPolicy(RateLimitPolicies.CustomBotRestart, context => RateLimitPolicies.Create(context, RateLimitPolicies.CustomBotRestart, rateLimiting.CustomBotRestart));
    options.AddPolicy(RateLimitPolicies.XpImport, context => RateLimitPolicies.Create(context, RateLimitPolicies.XpImport, rateLimiting.XpImport));
    options.AddPolicy(RateLimitPolicies.XpSettings, context => RateLimitPolicies.Create(context, RateLimitPolicies.XpSettings, rateLimiting.XpSettings));
});
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Configure settings
ConfigureAppSettings(builder);

var dcConfig = new DiscordSocketConfig()
{
    LogLevel = LogSeverity.Info,
    MessageCacheSize = 0,
    AuditLogCacheSize = 0,
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
builder.Services.AddSingleton(new GatewayIntentState(dcConfig.GatewayIntents));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddRankoonDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddAntiforgery(options => options.HeaderName = builder.Configuration["AuthCookies:CsrfHeaderName"] ?? "X-CSRF-TOKEN");

// Register database context
builder.Services.AddSingleton<RankoonDbContext>();
builder.Services.AddSingleton<AuthDataIntegrityInitializer>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IApplicationCache, ApplicationCache>();
builder.Services.AddSingleton<IOAuthStateStore, OAuthStateStore>();
builder.Services.AddSingleton<ReportWriter>();
builder.Services.AddSingleton<IReportWriter>(services => services.GetRequiredService<ReportWriter>());
builder.Services.AddSingleton<IReportQueryService, ReportQueryService>();
builder.Services.AddSingleton<GuildAnalyticsRecorder>();
builder.Services.AddSingleton<IGuildAnalyticsRecorder>(services => services.GetRequiredService<GuildAnalyticsRecorder>());
builder.Services.AddSingleton<IGuildAnalyticsQueryService, GuildAnalyticsQueryService>();
builder.Services.AddSingleton<IGuildAuditWriter, GuildAuditWriter>();
builder.Services.AddSingleton<IOperationalErrorRecorder, OperationalErrorRecorder>();
builder.Services.AddSingleton<IWorkerHealthRegistry, WorkerHealthRegistry>();
builder.Services.AddSingleton<IOperationsQueryService, OperationsQueryService>();
builder.Services.AddSingleton<ISignedCursorService, SignedCursorService>();

// Register HTTP client for Discord API calls
builder.Services.AddHttpClient<IDiscordService, DiscordService>();

// Register our services
builder.Services.AddSingleton<IBotInfoCache, BotInfoCache>();
builder.Services.AddSingleton<IBotOperatorAccessService, BotOperatorAccessService>();
builder.Services.AddSingleton<ICustomBotTokenProtector, CustomBotTokenProtector>();
builder.Services.AddSingleton<IDiscordOAuthTokenProtector, DiscordOAuthTokenProtector>();
builder.Services.AddSingleton<DiscordOAuthTokenMigrationService>();
builder.Services.AddSingleton<ICustomBotIdentityAccessPolicy, CustomBotIdentityAccessPolicy>();
builder.Services.AddSingleton<IPlatformBotRuntime, PlatformBotRuntime>();
builder.Services.AddSingleton<IGuildBotAuthority, GuildBotAuthority>();
builder.Services.AddSingleton<IBotRuntimeManager, BotRuntimeManager>();
builder.Services.AddSingleton<IGuildRuntimePresenceService, GuildRuntimePresenceService>();
builder.Services.AddSingleton<IGuildDiscordContextResolver, GuildDiscordContextResolver>();
builder.Services.AddSingleton<ICustomBotIdentityValidator, CustomBotIdentityValidator>();
builder.Services.AddSingleton<ICustomBotIdentityService, CustomBotIdentityService>();
builder.Services.AddSingleton<ActivityXpEventService>();
builder.Services.AddSingleton<RankoonCommandSchemaProvider>();
builder.Services.AddSingleton<ApplicationCommandRegistrar>();
builder.Services.AddSingleton<RankoonInteractionHandler>();
builder.Services.AddSingleton<IDiscordRuntimeEventDispatcher, DiscordRuntimeEventDispatcher>();
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<BrowserSessionService>();
builder.Services.AddSingleton<Rankoon.Controllers.IAuthCookieService>(services => services.GetRequiredService<BrowserSessionService>());
builder.Services.AddSingleton<Rankoon.Controllers.IOAuthCallbackCookieService>(services => services.GetRequiredService<BrowserSessionService>());
builder.Services.AddSingleton<IBrowserSessionService>(services => services.GetRequiredService<BrowserSessionService>());
builder.Services.AddScoped<IUserDiscordGuildProvider, UserDiscordGuildProvider>();
builder.Services.AddScoped<IGuildAuthorizationService, GuildAuthorizationService>();
builder.Services.AddSingleton<IGuildModuleRegistry, GuildModuleRegistry>();
builder.Services.AddSingleton<IGuildRolePermissionService, GuildRolePermissionService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.XpService>();
builder.Services.AddSingleton<GuildXpSettingsRuntimeCache>();
builder.Services.AddSingleton<IGuildXpSettingsRuntimeCache>(services => services.GetRequiredService<GuildXpSettingsRuntimeCache>());
builder.Services.AddSingleton<IGuildXpSettingsChangeConsumer>(services => services.GetRequiredService<GuildXpSettingsRuntimeCache>());
builder.Services.AddSingleton<IGuildXpSettingsChangePublisher, InProcessGuildXpSettingsChangePublisher>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ILevelTransitionService, Rankoon.Data.Xp.LevelTransitionService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ILevelUpTemplateRenderer, Rankoon.Data.Xp.LevelUpTemplateRenderer>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ILevelUpRandom, Rankoon.Data.Xp.LevelUpRandom>();
builder.Services.AddSingleton<Rankoon.Data.Xp.LevelUpTemplateSelector>();
builder.Services.AddSingleton<Rankoon.Data.Xp.IXpService>(services => services.GetRequiredService<Rankoon.Data.Xp.XpService>());
builder.Services.AddSingleton<Rankoon.Data.Xp.Import.XpImportParser>();
builder.Services.AddSingleton<Rankoon.Data.Xp.Import.IXpImportService, Rankoon.Data.Xp.Import.XpImportService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.IXpAuditService, Rankoon.Data.Xp.XpAuditService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ServerBoosterXpMultiplierResolver>();
    builder.Services.AddSingleton<Rankoon.Data.Xp.VoiceActivityAccumulator>();
    builder.Services.AddSingleton<Rankoon.Data.Xp.IVoiceActivityAccumulator>(services => services.GetRequiredService<Rankoon.Data.Xp.VoiceActivityAccumulator>());
    builder.Services.AddSingleton<Rankoon.Data.Xp.IXpProjectionCoordinator, Rankoon.Data.Xp.XpProjectionCoordinator>();
    builder.Services.AddSingleton<Rankoon.Data.Xp.IVoiceActivityProjectionService, Rankoon.Data.Xp.VoiceActivityProjectionService>();
    builder.Services.AddSingleton<Rankoon.Data.Xp.VoiceActivityProjectionRepairService>();
    builder.Services.AddSingleton<Rankoon.Data.Xp.VoiceLedgerMigrationService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ISeasonService, Rankoon.Data.Xp.SeasonService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ISeasonLifecycleService, Rankoon.Data.Xp.SeasonLifecycleService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.LedgerProjectionRepairService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.SeasonCoordinator>();
builder.Services.AddSingleton<Rankoon.Data.Xp.LevelRoleService>();
builder.Services.AddSingleton<IDiscordAnnouncementSender, DiscordAnnouncementSender>();
builder.Services.AddSingleton<LevelProgressionWorker>();
builder.Services.AddSingleton<Rankoon.Data.Xp.LeaderboardService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.IGuildUserPresentationService, Rankoon.Data.Xp.GuildUserPresentationService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.IGuildUserAvatarCacheRepository, Rankoon.Data.Xp.GuildUserAvatarCacheRepository>();
builder.Services.AddSingleton<Rankoon.Data.Xp.IGuildUserAvatarUrlFactory, Rankoon.Data.Xp.GuildUserAvatarUrlFactory>();
builder.Services.AddSingleton<Rankoon.Data.Xp.GuildUserAvatarObservationWorker>();
builder.Services.AddSingleton<Rankoon.Data.Xp.IGuildUserAvatarObserver>(services => services.GetRequiredService<Rankoon.Data.Xp.GuildUserAvatarObservationWorker>());
builder.Services.AddSingleton<Rankoon.Data.Xp.GuildUserAvatarHydrationWorker>();
builder.Services.AddSingleton<Rankoon.Data.Xp.ILeaderboardRealtimePublisher, Rankoon.Data.Xp.LeaderboardRealtimePublisher>();
builder.Services.AddSingleton<Rankoon.Data.Development.DevelopmentLeaderboardService>();
builder.Services.AddSingleton<VoiceXpWatchdog>();
builder.Services.AddSingleton<VoiceXpEligibilityEvaluator>();
builder.Services.AddSingleton<IGuildXpSettingsChangeConsumer>(services => services.GetRequiredService<VoiceXpWatchdog>());
builder.Services.AddSingleton<VcHubService>();
builder.Services.AddSingleton<GuildMembershipService>();
builder.Services.AddSingleton<RankoonBotHostedService>();
builder.Services.AddSingleton<SelfRoleService>();
builder.Services.AddSingleton<SelfRoleReactionService>();
builder.Services.AddSingleton<IPermissionRequirementCatalog, PermissionRequirementCatalog>();
builder.Services.AddSingleton<IDiagnosticReportCache, DiagnosticReportCache>();
builder.Services.AddSingleton<IBotPermissionDiagnosticService, BotPermissionDiagnosticService>();
builder.Services.AddScoped<Rankoon.Data.Dashboard.IDashboardOverviewService, Rankoon.Data.Dashboard.DashboardOverviewService>();

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService(services => services.GetRequiredService<ReportWriter>());
    builder.Services.AddHostedService(services => services.GetRequiredService<GuildAnalyticsRecorder>());
    builder.Services.AddHostedService<MongoIndexInitializer>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordOAuthTokenMigrationService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Rankoon.Data.Xp.LedgerProjectionRepairService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Rankoon.Data.Xp.VoiceActivityProjectionRepairService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Rankoon.Data.Xp.VoiceLedgerMigrationService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Rankoon.Data.Xp.SeasonCoordinator>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<LevelProgressionWorker>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<VoiceXpWatchdog>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Rankoon.Data.Xp.GuildUserAvatarObservationWorker>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Rankoon.Data.Xp.GuildUserAvatarHydrationWorker>());
    builder.Services.AddHostedService(provider => (DiscordRuntimeEventDispatcher)provider.GetRequiredService<IDiscordRuntimeEventDispatcher>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<RankoonBotHostedService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<VcHubService>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<GuildMembershipService>());
    builder.Services.AddHostedService<CustomBotIdentityHostedService>();
}

// Configure JWT authentication
var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>();
if (jwtSettings == null || string.IsNullOrEmpty(jwtSettings.SecretKey))
{
    throw new InvalidOperationException("JWT settings are not properly configured");
}

var key = Encoding.ASCII.GetBytes(jwtSettings.SecretKey);
var authCookieOptions = builder.Configuration.GetSection(AuthCookieOptions.SectionName).Get<AuthCookieOptions>() ?? new AuthCookieOptions();

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
            if (!context.Request.Headers.ContainsKey("Authorization") &&
                context.Request.Cookies.TryGetValue(authCookieOptions.AccessCookieName, out var token))
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
    if (exception != null)
    {
        ulong? guildId = context.Request.RouteValues.TryGetValue("guildId", out var routeGuild) && ulong.TryParse(Convert.ToString(routeGuild), out var parsedGuild) ? parsedGuild : null;
        var route = context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.RouteNameMetadata>()?.RouteName ?? context.Request.Path.Value;
        var correlation = context.Request.Headers.TryGetValue("X-Correlation-ID", out var header) ? header.ToString() : context.TraceIdentifier;
        await context.RequestServices.GetRequiredService<IOperationalErrorRecorder>().RecordAsync(new(exception, "aspnet", "unhandled_request", GuildId: guildId, Route: route, CorrelationId: correlation, TraceId: System.Diagnostics.Activity.Current?.TraceId.ToString(), Build: typeof(Program).Assembly.GetName().Version?.ToString(), Context: new Dictionary<string, object?> { ["method"] = context.Request.Method }), CancellationToken.None);
    }
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
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/auth"))
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
    }
    await next();
});
app.UseAuthentication();
app.UseMiddleware<CsrfValidationMiddleware>();
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
        .Validate(VoiceWatchdogOptions.IsValid, "VoiceWatchdog contains invalid intervals or concurrency.")
        .ValidateOnStart();
    builder.Services.AddOptions<Rankoon.Data.Xp.GuildUserAvatarHydrationOptions>()
        .Bind(builder.Configuration.GetSection(Rankoon.Data.Xp.GuildUserAvatarHydrationOptions.SectionName))
        .Validate(Rankoon.Data.Xp.GuildUserAvatarHydrationOptions.IsValid, "GuildUserAvatarHydration contains invalid batch, concurrency, polling, or lease settings.")
        .ValidateOnStart();
    builder.Services.AddOptions<VoiceActivityOptions>()
        .Bind(builder.Configuration.GetSection(VoiceActivityOptions.SectionName))
        .Validate(VoiceActivityOptions.IsValid, "VoiceActivity contains invalid checkpoint, watermark, projection, or batch values.")
        .ValidateOnStart();
    builder.Services.AddOptions<Rankoon.Data.Model.VoiceLedgerMigrationOptions>()
        .Bind(builder.Configuration.GetSection(Rankoon.Data.Model.VoiceLedgerMigrationOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddOptions<MongoStartupMaintenanceOptions>()
        .Bind(builder.Configuration.GetSection(MongoStartupMaintenanceOptions.SectionName))
        .Validate(options => options.RepairBatchSize is >= 1 and <= 1000, "MongoStartupMaintenance:RepairBatchSize must be between 1 and 1000.")
        .ValidateOnStart();
    builder.Services.AddOptions<AnalyticsRetentionOptions>().Bind(builder.Configuration.GetSection(AnalyticsRetentionOptions.SectionName)).ValidateOnStart();
    builder.Services.AddOptions<ReportingRetentionOptions>().Bind(builder.Configuration.GetSection(ReportingRetentionOptions.SectionName)).ValidateOnStart();
    builder.Services.Configure<MongoDbSettings>(
        builder.Configuration.GetSection(MongoDbSettings.SectionName));
    builder.Services.Configure<DiscordSettings>(
        builder.Configuration.GetSection(DiscordSettings.SectionName));
    builder.Services.AddOptions<JwtSettings>()
        .Bind(builder.Configuration.GetSection(JwtSettings.SectionName))
        .Validate(options => options.AccessTokenExpirationMinutes is >= 5 and <= 30, "Jwt:AccessTokenExpirationMinutes must be between 5 and 30.")
        .ValidateOnStart();
    builder.Services.AddOptions<AuthCookieOptions>()
        .Bind(builder.Configuration.GetSection(AuthCookieOptions.SectionName))
        .Validate(options => builder.Environment.IsDevelopment() || options.Secure, "AuthCookies:Secure must be true outside Development.")
        .Validate(options => builder.Environment.IsDevelopment() || (IsHostCookieName(options.AccessCookieName) && IsHostCookieName(options.RefreshCookieName)), "Auth cookie names must use the __Host- prefix outside Development.")
        .Validate(options => !string.IsNullOrWhiteSpace(options.CsrfHeaderName), "AuthCookies:CsrfHeaderName is required.")
        .ValidateOnStart();
    builder.Services.Configure<FrontendSettings>(
        builder.Configuration.GetSection(FrontendSettings.SectionName));
    builder.Services.AddOptions<CustomBotIdentityOptions>()
        .Bind(builder.Configuration.GetSection(CustomBotIdentityOptions.SectionName))
        .Validate(options => options.MaxActiveGuilds is null or > 0, "CustomBotIdentity:MaxActiveGuilds must be greater than zero when configured.")
        .Validate(options => !options.Enabled || options.FingerprintKey.Length >= 32, "CustomBotIdentity:FingerprintKey must contain at least 32 characters when enabled.")
        .Validate(options => options.StartupParallelism is >= 1 and <= 4, "CustomBotIdentity:StartupParallelism must be between one and four.")
        .Validate(options => options.MaxConcurrentRuntimes > 0, "CustomBotIdentity:MaxConcurrentRuntimes must be greater than zero.")
        .Validate(options => options.RuntimeStartQueueCapacity > 0, "CustomBotIdentity:RuntimeStartQueueCapacity must be greater than zero.")
        .Validate(options => options.RuntimeStartRetries is >= 0 and <= 5, "CustomBotIdentity:RuntimeStartRetries must be between zero and five.")
        .Validate(options => options.RuntimeRetryDelaySeconds is >= 1 and <= 60, "CustomBotIdentity:RuntimeRetryDelaySeconds must be between one and 60.")
        .Validate(options => options.RuntimeShutdownTimeoutSeconds is >= 1 and <= 120, "CustomBotIdentity:RuntimeShutdownTimeoutSeconds must be between one and 120.")
        .ValidateOnStart();
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

static bool IsHostCookieName(string? name) =>
    !string.IsNullOrWhiteSpace(name) && name.StartsWith("__Host-", StringComparison.Ordinal);

public partial class Program;
