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
    /// Public origin used when generating absolute URLs (e.g. the poll QR code) so the result does not
    /// depend on the client-supplied <c>Host</c> header. Example: <c>"PublicBaseUrl": "https://vote.example.com"</c>.
    /// When unset the request's own scheme/host is used, which is only safe while the app is not behind
    /// an untrusted proxy and <c>AllowedHosts</c> is restricted.
    /// </summary>
    public const string PublicBaseUrlConfigKey = "PublicBaseUrl";

    /// <summary>
    /// One-off data migrations that must not run on every startup, e.g.
    /// <c>"DataMigration": { "PromoteModeratorsToSuperModerators": true }</c>. Operators enable a switch,
    /// let the app start once, then turn it back off; startup never escalates privileges on its own.
    /// </summary>
    public const string LegacyDataMigrationConfigKey = "DataMigration";

    /// <summary>Switch name under <see cref="LegacyDataMigrationConfigKey"/> for the L7 moderator promotion.</summary>
    public const string PromoteModeratorsToSuperModeratorsSwitch = "PromoteModeratorsToSuperModerators";

    /// <summary>
    /// Checks whether a given user ID corresponds to the primary root administrator.
    /// </summary>
    public static bool IsRootAdmin(string? userId) =>
        !string.IsNullOrEmpty(userId) && string.Equals(userId, RootAdminId, StringComparison.OrdinalIgnoreCase);
}
