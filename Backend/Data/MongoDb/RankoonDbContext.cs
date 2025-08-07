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
