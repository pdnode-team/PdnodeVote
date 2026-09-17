namespace PdnodeVote.Data;

public static class SystemConstants
{
    /// <summary>
    /// The fixed, well-known UUID for the primary system administrator account (00000000-0000-0000-0000-000000000000).
    /// Similar to UID 0 / root in Unix or well-known system SIDs.
    /// Even if the administrator updates their email address or username,
    /// this immutable UUID guarantees continuous primary admin privileges and prevents lock-out/suspension.
    /// </summary>
    public const string RootAdminId = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// Default email address used when first seeding the primary administrator account.
    /// </summary>
    public const string DefaultAdminEmail = "admin@vote.com";

    /// <summary>
    /// Configuration section holding the initial administrator password, e.g.
    /// <c>"BootstrapAdmin": { "Password": "..." }</c>. Must be supplied by the operator;
    /// no default password is compiled into the application.
    /// </summary>
    public const string BootstrapAdminConfigKey = "BootstrapAdmin";

    /// <summary>
    /// Optional environment-variable alternative to <see cref="BootstrapAdminConfigKey"/>.
    /// </summary>
    public const string BootstrapAdminPasswordEnvVar = "PDNODEVOTE_ADMIN_PASSWORD";

    /// <summary>
    /// Checks whether a given user ID corresponds to the primary root administrator.
    /// </summary>
    public static bool IsRootAdmin(string? userId) =>
        !string.IsNullOrEmpty(userId) && string.Equals(userId, RootAdminId, StringComparison.OrdinalIgnoreCase);
}
