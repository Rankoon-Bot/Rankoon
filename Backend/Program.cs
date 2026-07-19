using Discord;
using Discord.WebSocket;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Rankoon.Data.Auth;
using Rankoon.Data.Discord;
using Rankoon.Data.MongoDb;
using Rankoon.Data.Reporting;
using Rankoon.Data.Utils;
using Serilog;
using System.Net.Sockets;
using System.Text;
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
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString | System.Text.Json.Serialization.JsonNumberHandling.WriteAsString;
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("leaderboard", context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("discord_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 90, Window = TimeSpan.FromMinutes(1), QueueLimit = 2 }));
    options.AddPolicy("reports", context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("discord_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 2 }));
});
builder.Services.AddProblemDetails();
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
builder.Services.AddHostedService(services => services.GetRequiredService<ReportWriter>());
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
builder.Services.AddSingleton<Rankoon.Data.Xp.IXpService, Rankoon.Data.Xp.XpService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.LevelRoleService>();
builder.Services.AddSingleton<Rankoon.Data.Xp.LeaderboardService>();
builder.Services.AddHostedService<MongoIndexInitializer>();
builder.Services.AddHostedService<RankoonBotHostedService>();
builder.Services.AddSingleton<VoiceXpWatchdog>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<VoiceXpWatchdog>());
builder.Services.AddSingleton<VcHubService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<VcHubService>());
builder.Services.AddHostedService<ActivityXpEventService>();
builder.Services.AddSingleton<GuildMembershipService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<GuildMembershipService>());
builder.Services.AddHostedService<RankoonCommandService>();

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
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
if (Directory.Exists(app.Environment.WebRootPath))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
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
    builder.Services.Configure<MongoDbSettings>(
        builder.Configuration.GetSection(MongoDbSettings.SectionName));
    builder.Services.Configure<DiscordSettings>(
        builder.Configuration.GetSection(DiscordSettings.SectionName));
    builder.Services.Configure<JwtSettings>(
        builder.Configuration.GetSection(JwtSettings.SectionName));
    builder.Services.Configure<FrontendSettings>(
        builder.Configuration.GetSection(FrontendSettings.SectionName));
}
