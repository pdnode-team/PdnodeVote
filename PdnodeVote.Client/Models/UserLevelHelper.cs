namespace PdnodeVote.Client.Models;

public static class UserLevelHelper
{
    public static int CalculateLevel(DateTime createdAt, int voteCount, int approvedPollCount, int votesReceivedCount)
    {
        var age = DateTime.UtcNow - createdAt;

        // Level 6: Extremely hard with high prestige (>= 90 days, 350+ votes, 15+ approved polls, 200+ votes received)
        if (age.TotalDays >= 90 && voteCount >= 350 && approvedPollCount >= 15 && votesReceivedCount >= 200)
            return 6;

        // Level 5: Very hard (>= 60 days, 150+ votes, 8+ approved polls, 50+ votes received)
        if (age.TotalDays >= 60 && voteCount >= 150 && approvedPollCount >= 8 && votesReceivedCount >= 50)
            return 5;

        // Level 4: Hard (>= 30 days, 60+ votes, 3+ approved polls)
        if (age.TotalDays >= 30 && voteCount >= 60 && approvedPollCount >= 3)
            return 4;

        // Level 3: Moderately challenging (>= 14 days, 20+ votes, 1+ approved poll)
        if (age.TotalDays >= 14 && voteCount >= 20 && approvedPollCount >= 1)
            return 3;

        // Level 2: Account age >= 7 days is the primary requirement + participated
        if (age.TotalDays >= 7 && (voteCount >= 1 || approvedPollCount >= 1))
            return 2;

        // Level 1: Default entry level upon participation
        return 1;
    }

    public static string GetLevelName(int level) => $"Level {Math.Clamp(level, 1, 6)}";

    public static string GetLevelShort(int level) => $"Lv.{Math.Clamp(level, 1, 6)}";

    public static string GetLevelColor(int level) => level switch
    {
        6 => "#8b5cf6", // Prestige Violet
        5 => "#f59e0b", // Amber
        4 => "#10b981", // Emerald
        3 => "#06b6d4", // Cyan
        2 => "#3b82f6", // Blue
        _ => "#64748b"  // Slate
    };
}
