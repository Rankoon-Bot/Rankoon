namespace Rankoon.Data.Xp;

/// <summary>Exact cumulative MEE6 level curve used by the legacy Rankoon bot.</summary>
public static class Mee6LevelCurve
{
    public static long RequiredXpForLevel(int level)
    {
        if (level <= 1) return 100;
        var value = 5m / 6m * level * (2m * level * level + 27m * level + 91m);
        return Math.Max(100, decimal.ToInt64(decimal.Floor(value)));
    }

    public static int GetLevel(decimal totalXp)
    {
        if (totalXp < 100) return 0;
        var low = 1;
        var high = 2;
        while (RequiredXpForLevel(high) <= totalXp && high < 1_000_000) high *= 2;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (RequiredXpForLevel(middle) <= totalXp) low = middle + 1;
            else high = middle - 1;
        }
        return high;
    }
}
