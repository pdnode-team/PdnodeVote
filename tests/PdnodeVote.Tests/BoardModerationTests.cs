using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdnodeVote.Data;
using PdnodeVote.Services;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for TODO-BUGS.md L5: the board-posting rule had two divergent copies. The poll
/// write path ignored the <c>CategoryModerators</c> mapping, so a board-level moderator assignment had
/// no effect. The rule now lives in <see cref="ICategoryService"/> and the write path delegates to it.
/// </summary>
public class BoardModerationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly CategoryService _categoryService;
    private readonly PollService _pollService;

    public BoardModerationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _dbContext.Database.EnsureCreated();

        _factory = new TestDbContextFactory(options);
        _userManager = IdentityTestServices.CreateUserManager(_dbContext);
        _roleManager = new RoleManager<IdentityRole>(
            new RoleStore<IdentityRole>(_dbContext),
            new IRoleValidator<IdentityRole>[0],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole>>.Instance);

        _categoryService = new CategoryService(_factory, _userManager, null);
        // Pass the category service exactly as DI does, so the delegated rule is what gets exercised.
        _pollService = new PollService(_factory, new TestEmailNotificationService(), _userManager,
            categoryService: _categoryService);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private async Task<ApplicationUser> CreateUserAsync(string prefix, string? role = null)
    {
        var user = new ApplicationUser { UserName = $"{prefix}{Guid.NewGuid():N}@test.com" };
        user.Email = user.UserName;
        await _userManager.CreateAsync(user, "pw123");

        if (role is not null)
        {
            if (!await _roleManager.RoleExistsAsync(role)) await _roleManager.CreateAsync(new IdentityRole(role));
            await _userManager.AddToRoleAsync(user, role);
        }

        return user;
    }

    private async Task<Category> CreateBoardAsync(CategoryPostPermission permission)
    {
        var board = new Category
        {
            Name = $"Board {Guid.NewGuid():N}",
            Slug = $"board-{Guid.NewGuid():N}",
            PostPermission = permission,
            Depth = 0
        };
        _dbContext.Categories.Add(board);
        await _dbContext.SaveChangesAsync();
        return board;
    }

    private Task<(bool Success, string Message, int PollId)> TryCreateAsync(string userId, int categoryId) =>
        _pollService.CreatePollAsync(
            "Board permission probe", null, userId, false, false, 1,
            PdnodeVote.Data.ResultVisibility.AlwaysPublic, null,
            new List<string> { "A", "B" }, categoryId);

    [Fact]
    public async Task ModeratorOnlyBoard_WithoutAssignments_AllowsAnyModerator()
    {
        var board = await CreateBoardAsync(CategoryPostPermission.ModeratorOnly);
        var moderator = await CreateUserAsync("mod", "Moderator");

        var (ok, message, _) = await TryCreateAsync(moderator.Id, board.Id);

        Assert.True(ok, message);
    }

    [Fact]
    public async Task ModeratorOnlyBoard_WithAssignments_RejectsUnassignedModerator()
    {
        // This is the behaviour the duplicated inline rule was missing: once a board has explicit
        // moderators, a moderator who is not on the list must be refused.
        var board = await CreateBoardAsync(CategoryPostPermission.ModeratorOnly);
        var assigned = await CreateUserAsync("assigned", "Moderator");
        var unassigned = await CreateUserAsync("unassigned", "Moderator");

        _dbContext.CategoryModerators.Add(new CategoryModerator
        {
            CategoryId = board.Id,
            UserId = assigned.Id,
            AssignedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var (okUnassigned, messageUnassigned, _) = await TryCreateAsync(unassigned.Id, board.Id);
        Assert.False(okUnassigned);
        Assert.Contains("assigned moderators", messageUnassigned, StringComparison.OrdinalIgnoreCase);

        var (okAssigned, messageAssigned, _) = await TryCreateAsync(assigned.Id, board.Id);
        Assert.True(okAssigned, messageAssigned);
    }

    [Fact]
    public async Task ModeratorOnlyBoard_StillAllowsSuperModeratorAndAdmin()
    {
        var board = await CreateBoardAsync(CategoryPostPermission.ModeratorOnly);
        var assigned = await CreateUserAsync("assigned", "Moderator");
        _dbContext.CategoryModerators.Add(new CategoryModerator
        {
            CategoryId = board.Id,
            UserId = assigned.Id,
            AssignedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var superMod = await CreateUserAsync("super", "SuperModerator");
        var admin = await CreateUserAsync("admin", "Admin");

        Assert.True((await TryCreateAsync(superMod.Id, board.Id)).Success);
        Assert.True((await TryCreateAsync(admin.Id, board.Id)).Success);
    }

    [Fact]
    public async Task AdminOnlyBoard_RejectsModeratorAndAllowsAdmin()
    {
        var board = await CreateBoardAsync(CategoryPostPermission.AdminOnly);
        var moderator = await CreateUserAsync("mod", "Moderator");
        var admin = await CreateUserAsync("admin", "Admin");

        var (okMod, message, _) = await TryCreateAsync(moderator.Id, board.Id);
        Assert.False(okMod);
        Assert.Contains("administrators only", message, StringComparison.OrdinalIgnoreCase);

        Assert.True((await TryCreateAsync(admin.Id, board.Id)).Success);
    }

    [Fact]
    public async Task AnyBoard_AllowsRegularUser()
    {
        var board = await CreateBoardAsync(CategoryPostPermission.Anyone);
        var user = await CreateUserAsync("user");

        var (ok, message, _) = await TryCreateAsync(user.Id, board.Id);

        Assert.True(ok, message);
    }

    [Fact]
    public async Task MissingBoard_IsRejected()
    {
        var user = await CreateUserAsync("user");

        var (ok, message, _) = await TryCreateAsync(user.Id, 999999);

        Assert.False(ok);
        Assert.Contains("does not exist", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MovingPollIntoAdminOnlyBoard_IsRejected()
    {
        // The update path must enforce the same rule as creation (TODO-BUGS.md M4).
        var openBoard = await CreateBoardAsync(CategoryPostPermission.Anyone);
        var adminBoard = await CreateBoardAsync(CategoryPostPermission.AdminOnly);
        var author = await CreateUserAsync("author");

        var (created, message, pollId) = await TryCreateAsync(author.Id, openBoard.Id);
        Assert.True(created, message);

        var result = await _pollService.UpdatePollAsync(pollId, author.Id, new PdnodeVote.Client.Models.UpdatePollRequest
        {
            Title = "Board permission probe",
            CategoryId = adminBoard.Id
        });

        Assert.False(result.Success);
        Assert.Contains("administrators only", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
