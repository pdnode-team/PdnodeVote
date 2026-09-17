using Microsoft.AspNetCore.Identity;

namespace PdnodeVote.RateLimiting;

/// <summary>
/// Identity password / lockout policy, kept in one place so it can be asserted by tests and
/// cannot drift from what the application actually registers.
/// </summary>
public static class SecurityPolicy
{
    public const int RequiredPasswordLength = 8;
    public const int MaxFailedAccessAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    public static void Apply(IdentityOptions options)
    {
        // Login still works for accounts whose existing password predates this policy: Identity
        // validates password rules when a password is SET (create / change / reset), not on sign-in.
        options.Password.RequiredLength = RequiredPasswordLength;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = false; // keep passphrases practical

        // Brute-force protection. Login.razor must pass lockoutOnFailure: true for these to apply.
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
        options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
    }
}
