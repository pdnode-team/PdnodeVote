namespace PdnodeVote.Client.Models;

public static class SystemConstants
{
    public const string RootAdminId = "00000000-0000-0000-0000-000000000000";
    public const string RootAdminEmail = "admin@vote.com";

    public static bool IsRootAdmin(string? userId) =>
        string.Equals(userId, RootAdminId, StringComparison.OrdinalIgnoreCase);
}
