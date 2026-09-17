namespace PdnodeVote.Services;

/// <summary>
/// Why a user may not create a post in a board. Null (no denial) means the post is allowed.
/// </summary>
/// <remarks>
/// Exists so board-permission enforcement lives in exactly one place
/// (<see cref="CategoryService.GetPostDenialAsync(int?, string)"/>) while callers keep their existing,
/// more specific user-facing messages.
/// </remarks>
public enum CategoryPostDenial
{
    /// <summary>The requested board id does not exist.</summary>
    BoardNotFound = 0,

    /// <summary>The board is restricted to administrators.</summary>
    AdministratorsOnly = 1,

    /// <summary>The board is restricted to moderators and administrators.</summary>
    ModeratorsOnly = 2,

    /// <summary>
    /// The board has explicit moderator assignments and the caller is a moderator who is not among them.
    /// </summary>
    NotAssignedModerator = 3
}
