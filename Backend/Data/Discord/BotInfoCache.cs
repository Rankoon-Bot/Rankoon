using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Rankoon.Data.Discord;

/// <summary>
/// Service for caching Discord bot application information
/// </summary>
public interface IBotInfoCache
{
    /// <summary>
    /// Check if a Discord user ID is a team member (owner or team member)
    /// </summary>
    bool IsTeamMember(string discordId);
    
    /// <summary>
    /// Get the bot owner's Discord ID
    /// </summary>
    string? GetBotOwnerId();
    
    /// <summary>
    /// Get all team member Discord IDs
    /// </summary>
    IReadOnlyList<string> GetTeamMemberIds();
}

public class BotInfoCache : IBotInfoCache
{
    private readonly ILogger<BotInfoCache> _logger;
    private readonly DiscordShardedClient _discordClient;
    
    private string? _botOwnerId;
    private HashSet<string> _teamMemberIds = new();
    private bool _isInitialized = false;

    public BotInfoCache(ILogger<BotInfoCache> logger, DiscordShardedClient discordClient)
    {
        _logger = logger;
        _discordClient = discordClient;
        
        // Subscribe to the Ready event to cache bot info when bot is ready
        _discordClient.ShardReady += OnShardReady;
    }

    private async Task OnShardReady(DiscordSocketClient shard)
    {
        if (_isInitialized) return; // Only initialize once
        
        try
        {
            _logger.LogInformation("Caching Discord bot application info...");
            
            var appInfo = await _discordClient.GetApplicationInfoAsync();
            
            // Cache owner ID
            _botOwnerId = appInfo.Owner.Id.ToString();
            _teamMemberIds.Add(_botOwnerId);
            
            // Cache team member IDs
            if (appInfo.Team != null)
            {
                foreach (var member in appInfo.Team.TeamMembers)
                {
                    _teamMemberIds.Add(member.User.Id.ToString());
                }
                _logger.LogInformation("Cached bot info: Owner={BotOwner}, TeamMembers={TeamMemberCount}", 
                    _botOwnerId, _teamMemberIds.Count);
            }
            else
            {
                _logger.LogInformation("Cached bot info: Owner={BotOwner}, No team", _botOwnerId);
            }
            
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache Discord bot application info");
        }
    }

    public bool IsTeamMember(string discordId)
    {
        // If not initialized yet, wait a bit for initialization
        if (!_isInitialized)
        {
            // Try to wait for up to 5 seconds for initialization
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!_isInitialized && DateTime.UtcNow < timeout)
            {
                Thread.Sleep(100); // Wait 100ms
            }
        }
        
        return _isInitialized && _teamMemberIds.Contains(discordId);
    }

    public string? GetBotOwnerId()
    {
        return _isInitialized ? _botOwnerId : null;
    }

    public IReadOnlyList<string> GetTeamMemberIds()
    {
        return _isInitialized ? _teamMemberIds.ToList() : new List<string>();
    }
}
