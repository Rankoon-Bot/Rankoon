using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Rankoon.Data.Model;

namespace Rankoon.Data.MongoDb;

/// <summary>
/// MongoDB context for the Rankoon application
/// </summary>
public class RankoonDbContext
{
    private readonly IMongoDatabase _database;
    private readonly MongoDbSettings _settings;

    public RankoonDbContext(IOptions<MongoDbSettings> settings)
    {
        _settings = settings.Value;
        var client = new MongoClient(_settings.ConnectionString);
        _database = client.GetDatabase(_settings.DatabaseName);
    }

    /// <summary>
    /// Collection of Discord users
    /// </summary>
    public IMongoCollection<DiscordUser> DiscordUsers =>
        _database.GetCollection<DiscordUser>("discord_users");

    /// <summary>
    /// Collection of refresh tokens
    /// </summary>
    public IMongoCollection<RefreshToken> RefreshTokens =>
        _database.GetCollection<RefreshToken>("refresh_tokens");

    public IMongoCollection<GuildXpSettings> GuildXpSettings => _database.GetCollection<GuildXpSettings>("guild_xp_settings");
    public IMongoCollection<GuildSeasonSettings> GuildSeasonSettings => _database.GetCollection<GuildSeasonSettings>("guild_season_settings");
    public IMongoCollection<GuildSeason> GuildSeasons => _database.GetCollection<GuildSeason>("guild_seasons");
    public IMongoCollection<SeasonMemberXp> SeasonMemberXp => _database.GetCollection<SeasonMemberXp>("season_member_xp");
    public IMongoCollection<SeasonFinalStanding> SeasonFinalStandings => _database.GetCollection<SeasonFinalStanding>("season_final_standings");
    public IMongoCollection<SeasonCoordinatorLease> SeasonCoordinatorLeases => _database.GetCollection<SeasonCoordinatorLease>("season_coordinator_leases");
    public IMongoCollection<SeasonAnnouncementDelivery> SeasonAnnouncementDeliveries => _database.GetCollection<SeasonAnnouncementDelivery>("season_announcement_deliveries");
    public IMongoCollection<MemberXp> MemberXp => _database.GetCollection<MemberXp>("member_xp");
    public IMongoCollection<DevelopmentMockMember> DevelopmentMockMembers => _database.GetCollection<DevelopmentMockMember>("development_mock_members");
    public IMongoCollection<GuildLeaderboardSettings> GuildLeaderboardSettings => _database.GetCollection<GuildLeaderboardSettings>("guild_leaderboard_settings");
    public IMongoCollection<MemberLeaderboardPreference> MemberLeaderboardPreferences => _database.GetCollection<MemberLeaderboardPreference>("member_leaderboard_preferences");
    public IMongoCollection<XpLedgerEntry> XpLedger => _database.GetCollection<XpLedgerEntry>("xp_ledger");
    public IMongoCollection<GuildLevelUpAnnouncementSettings> GuildLevelUpAnnouncementSettings => _database.GetCollection<GuildLevelUpAnnouncementSettings>("guild_level_up_announcement_settings");
    public IMongoCollection<LevelTransitionEvent> LevelTransitionEvents => _database.GetCollection<LevelTransitionEvent>("level_transition_events");
    public IMongoCollection<VoiceSession> VoiceSessions => _database.GetCollection<VoiceSession>("voice_sessions");
    public IMongoCollection<VcHub> VcHubs => _database.GetCollection<VcHub>("vc_hubs");
    public IMongoCollection<TemporaryVoiceChannel> TemporaryVoiceChannels => _database.GetCollection<TemporaryVoiceChannel>("temporary_voice_channels");
    public IMongoCollection<GuildStats> GuildStats => _database.GetCollection<GuildStats>("guild_stats");
    public IMongoCollection<ReportEvent> ReportEvents => _database.GetCollection<ReportEvent>("report_events");
    public IMongoCollection<GuildRolePermissionPolicy> GuildRolePermissionPolicies => _database.GetCollection<GuildRolePermissionPolicy>("guild_role_permission_policies");
    public IMongoCollection<SelfRolePanel> SelfRolePanels => _database.GetCollection<SelfRolePanel>("self_role_panels");
    public IMongoCollection<SelfRoleAssignment> SelfRoleAssignments => _database.GetCollection<SelfRoleAssignment>("self_role_assignments");
}

/// <summary>
/// MongoDB configuration settings
/// </summary>
public class MongoDbSettings
{
    public const string SectionName = "MongoDb";

    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
}
