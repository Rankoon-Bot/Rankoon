using Rankoon.Data.Model;

namespace Rankoon.Hubs;

public static class HubGroupNames
{
    public static string LeaderboardAudience(string audience, string alias) => $"leaderboard:{audience}:{alias}";

    public static string Leaderboard(string audience, string alias, SeasonLeaderboardScope scope, string? seasonId) =>
        $"leaderboard:{audience}:{alias}:{scope}:{seasonId ?? "-"}";
}
