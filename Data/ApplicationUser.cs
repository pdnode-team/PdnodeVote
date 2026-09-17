using Microsoft.AspNetCore.Identity;

namespace PdnodeVote.Data;

// Add profile data for application users by adding properties to the ApplicationUser class
public class ApplicationUser : IdentityUser
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsBanned { get; set; } = false;
    public DateTime? BannedUntil { get; set; }
    public string? BanReason { get; set; }

    public bool IsCurrentlyBanned => IsBanned && (BannedUntil == null || BannedUntil > DateTime.UtcNow);
    public bool IsPermanentlyBanned => IsBanned && BannedUntil == null;
    public bool IsRootAdmin => SystemConstants.IsRootAdmin(Id);
}
