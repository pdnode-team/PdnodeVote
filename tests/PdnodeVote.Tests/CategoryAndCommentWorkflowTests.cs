using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PdnodeVote.Services;
using Xunit;
using PollStatus = PdnodeVote.Data.PollStatus;

namespace PdnodeVote.Tests;

public class CategoryAndCommentWorkflowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public CategoryAndCommentWorkflowTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();
        _factory = new TestDbContextFactory(_options);

        var userStore = new UserStore<ApplicationUser>(_context);
        var userOptions = Options.Create(new IdentityOptions());
        var passwordHasher = new PasswordHasher<ApplicationUser>();
        var userValidators = new List<IUserValidator<ApplicationUser>> { new UserValidator<ApplicationUser>() };
        var passwordValidators = new List<IPasswordValidator<ApplicationUser>> { new PasswordValidator<ApplicationUser>() };
        var keyNormalizer = new UpperInvariantLookupNormalizer();
        var errors = new IdentityErrorDescriber();

        _userManager = new UserManager<ApplicationUser>(
            userStore,
            userOptions,
            passwordHasher,
            userValidators,
            passwordValidators,
            keyNormalizer,
            errors,
            null!,
            new NullLogger<UserManager<ApplicationUser>>()
        );

        var roleStore = new RoleStore<IdentityRole>(_context);
        var roleValidators = new List<IRoleValidator<IdentityRole>> { new RoleValidator<IdentityRole>() };
        _roleManager = new RoleManager<IdentityRole>(
            roleStore,
            roleValidators,
            keyNormalizer,
            errors,
            new NullLogger<RoleManager<IdentityRole>>()
        );

        string[] roles = ["Admin", "SuperModerator", "Moderator", "User"];
        foreach (var r in roles)
        {
            _roleManager.CreateAsync(new IdentityRole(r)).GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CategoryRequest_ApprovedByAdmin_InstantlyCreatesCategory()
    {
        var catService = new CategoryService(_factory, _userManager);

        var admin = new ApplicationUser { UserName = "admin@test.com", Email = "admin@test.com" };
        await _userManager.CreateAsync(admin);
        await _userManager.AddToRoleAsync(admin, "Admin");

        var user = new ApplicationUser { UserName = "user@test.com", Email = "user@test.com" };
        await _userManager.CreateAsync(user);

        var submitResult = await catService.SubmitCategoryRequestAsync(user.Id, new SubmitCategoryRequest
        {
            Name = "Tech Trends",
            Description = "Emerging technology discussions",
            ParentId = null
        });
        Assert.True(submitResult.Success);

        var pending = await catService.GetPendingCategoryRequestsAsync();
        var req = pending.FirstOrDefault(r => r.Name == "Tech Trends");
        Assert.NotNull(req);

        var reviewResult = await catService.ReviewCategoryRequestAsync(admin.Id, req.Id, new ReviewCategoryRequest
        {
            Approve = true,
            Comment = "Approved by admin"
        });
        Assert.True(reviewResult.Success);

        var allCats = await catService.GetFlattenedCategoriesAsync();
        Assert.Contains(allCats, c => c.Name == "Tech Trends");
    }

    [Fact]
    public async Task CategoryRequest_NeedsTwoModerators_ToApprove()
    {
        var catService = new CategoryService(_factory, _userManager);

        var mod1 = new ApplicationUser { UserName = "mod1@test.com", Email = "mod1@test.com" };
        await _userManager.CreateAsync(mod1);
        await _userManager.AddToRoleAsync(mod1, "Moderator");

        var mod2 = new ApplicationUser { UserName = "mod2@test.com", Email = "mod2@test.com" };
        await _userManager.CreateAsync(mod2);
        await _userManager.AddToRoleAsync(mod2, "Moderator");

        var user = new ApplicationUser { UserName = "user2@test.com", Email = "user2@test.com" };
        await _userManager.CreateAsync(user);

        var submitResult = await catService.SubmitCategoryRequestAsync(user.Id, new SubmitCategoryRequest
        {
            Name = "Music Lounge",
            Description = "Music and audio gear discussion",
            ParentId = null
        });
        Assert.True(submitResult.Success);

        var pending = await catService.GetPendingCategoryRequestsAsync();
        var req = pending.FirstOrDefault(r => r.Name == "Music Lounge");
        Assert.NotNull(req);

        // First moderator reviews
        var rev1 = await catService.ReviewCategoryRequestAsync(mod1.Id, req.Id, new ReviewCategoryRequest
        {
            Approve = true,
            Comment = "Looks good"
        });
        Assert.True(rev1.Success);

        var catsAfterFirst = await catService.GetFlattenedCategoriesAsync();
        Assert.DoesNotContain(catsAfterFirst, c => c.Name == "Music Lounge");

        // Second moderator reviews
        var rev2 = await catService.ReviewCategoryRequestAsync(mod2.Id, req.Id, new ReviewCategoryRequest
        {
            Approve = true,
            Comment = "Seconded"
        });
        Assert.True(rev2.Success);

        var catsAfterSecond = await catService.GetFlattenedCategoriesAsync();
        Assert.Contains(catsAfterSecond, c => c.Name == "Music Lounge");
    }

    [Fact]
    public async Task CategoryRequest_SingleRejection_ImmediatelyRejects()
    {
        var catService = new CategoryService(_factory, _userManager);

        var mod = new ApplicationUser { UserName = "mod_reject@test.com", Email = "mod_reject@test.com" };
        await _userManager.CreateAsync(mod);
        await _userManager.AddToRoleAsync(mod, "Moderator");

        var user = new ApplicationUser { UserName = "applicant@test.com", Email = "applicant@test.com" };
        await _userManager.CreateAsync(user);

        var submitResult = await catService.SubmitCategoryRequestAsync(user.Id, new SubmitCategoryRequest
        {
            Name = "Spam Board",
            Description = "Testing",
            ParentId = null
        });
        Assert.True(submitResult.Success);

        var pending = await catService.GetPendingCategoryRequestsAsync();
        var req = pending.FirstOrDefault(r => r.Name == "Spam Board");
        Assert.NotNull(req);

        var rev = await catService.ReviewCategoryRequestAsync(mod.Id, req.Id, new ReviewCategoryRequest
        {
            Approve = false,
            Comment = "Duplicate board scope"
        });
        Assert.True(rev.Success);

        var pendingAfter = await catService.GetPendingCategoryRequestsAsync();
        Assert.DoesNotContain(pendingAfter, r => r.Id == req.Id);

        var cats = await catService.GetFlattenedCategoriesAsync();
        Assert.DoesNotContain(cats, c => c.Name == "Spam Board");
    }

    [Fact]
    public async Task Comments_ModeratorAndAdmin_AutoApproved_UnprivilegedNeedsReview()
    {
        var commentService = new CommentService(_factory, _userManager, new PollEventNotifier());

        var admin = new ApplicationUser { UserName = "admin_c@test.com", Email = "admin_c@test.com" };
        await _userManager.CreateAsync(admin);
        await _userManager.AddToRoleAsync(admin, "Admin");

        var newUser = new ApplicationUser { UserName = "newbie@test.com", Email = "newbie@test.com", CreatedAt = DateTime.UtcNow };
        await _userManager.CreateAsync(newUser);

        var poll = new Poll
        {
            Title = "Comment Testing Poll",
            CreatorId = admin.Id,
            Status = PollStatus.Approved,
            CreatedAt = DateTime.UtcNow
        };
        _context.Polls.Add(poll);
        await _context.SaveChangesAsync();

        // Admin comment: auto approved
        var adminResult = await commentService.AddCommentAsync(poll.Id, admin.Id, "Admin test comment");
        Assert.True(adminResult.Success);

        // New user comment: pending review due to credit policy
        var userResult = await commentService.AddCommentAsync(poll.Id, newUser.Id, "New user comment requiring review");
        Assert.True(userResult.Success);

        // Visible approved comments for public
        var approvedComments = await commentService.GetPollCommentsAsync(poll.Id, null);
        Assert.Single(approvedComments);
        Assert.Equal("Admin test comment", approvedComments[0].Content);

        // Pending queue for admin
        var pendingComments = await commentService.GetPendingCommentsAsync();
        Assert.Contains(pendingComments, c => c.Content == "New user comment requiring review");
    }
}
