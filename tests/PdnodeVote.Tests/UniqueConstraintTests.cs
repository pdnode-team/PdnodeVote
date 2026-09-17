using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PdnodeVote.Data;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for TODO-BUGS.md D2–D6: the read-then-write flows are now backed by unique
/// indexes, and the "lost the race" outcome is handled instead of surfacing as an HTTP 500.
/// </summary>
public class UniqueConstraintTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public UniqueConstraintTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private ApplicationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning))
            .Options);

    [Fact]
    public async Task Migrations_ApplyCleanlyToAFreshDatabase()
    {
        // The unique indexes arrive through a migration, so verify it actually applies — a broken
        // migration would only show up on a new deployment, not in any service-level test.
        await using var context = CreateContext();

        var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
        Assert.NotEmpty(pending);

        await context.Database.MigrateAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());

        // NOTE: deliberately not asserting HasPendingModelChanges()==false. EF Core reports pending
        // model changes here even with zero schema drift — `dotnet ef migrations add` produces an
        // empty migration — which is the same false positive that Program.cs downgrades via
        // ConfigureWarnings. The real assertion is above: the migration applied and nothing is pending.
    }

    [Fact]
    public async Task VoteRecords_RejectDuplicateUserVote()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var author = new ApplicationUser { UserName = "author@t.com", Email = "author@t.com" };
        var voter = new ApplicationUser { UserName = "voter@t.com", Email = "voter@t.com" };
        context.Users.AddRange(author, voter);
        await context.SaveChangesAsync();

        var poll = new Poll
        {
            Title = "P",
            CreatorId = author.Id,
            Status = PollStatus.Approved,
            Options = [new PollOption { Text = "A", Order = 1 }, new PollOption { Text = "B", Order = 2 }]
        };
        context.Polls.Add(poll);
        await context.SaveChangesAsync();

        var optionId = poll.Options[0].Id;
        context.VoteRecords.Add(new VoteRecord { PollId = poll.Id, PollOptionId = optionId, UserId = voter.Id, IpAddress = "1.1.1.1" });
        await context.SaveChangesAsync();

        context.VoteRecords.Add(new VoteRecord { PollId = poll.Id, PollOptionId = optionId, UserId = voter.Id, IpAddress = "1.1.1.1" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.True(ex.IsUniqueConstraintViolation("VoteRecords"));
    }

    [Fact]
    public async Task VoteRecords_AllowMultipleGuestVotesWithNullUserId()
    {
        // Guest votes store UserId = NULL, and SQLite treats NULLs as distinct in a unique index, so
        // the unique constraint on (PollId, UserId) must not block them.
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var author = new ApplicationUser { UserName = "author2@t.com", Email = "author2@t.com" };
        context.Users.Add(author);
        await context.SaveChangesAsync();

        var poll = new Poll
        {
            Title = "P2",
            CreatorId = author.Id,
            Status = PollStatus.Approved,
            Options = [new PollOption { Text = "A", Order = 1 }, new PollOption { Text = "B", Order = 2 }]
        };
        context.Polls.Add(poll);
        await context.SaveChangesAsync();

        var optionId = poll.Options[0].Id;
        context.VoteRecords.Add(new VoteRecord { PollId = poll.Id, PollOptionId = optionId, UserId = null, IpAddress = "9.9.9.9" });
        context.VoteRecords.Add(new VoteRecord { PollId = poll.Id, PollOptionId = optionId, UserId = null, IpAddress = "9.9.9.9" });

        await context.SaveChangesAsync();

        Assert.Equal(2, await context.VoteRecords.CountAsync(v => v.PollId == poll.Id));
    }

    [Fact]
    public async Task CategoryRequestReviews_RejectDuplicateReviewer()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        var applicant = new ApplicationUser { UserName = "applicant@t.com", Email = "applicant@t.com" };
        var reviewer = new ApplicationUser { UserName = "reviewer@t.com", Email = "reviewer@t.com" };
        context.Users.AddRange(applicant, reviewer);
        await context.SaveChangesAsync();

        var request = new CategoryRequest { Name = "Board", ApplicantId = applicant.Id, Status = CategoryRequestStatus.Pending };
        context.CategoryRequests.Add(request);
        await context.SaveChangesAsync();

        context.CategoryRequestReviews.Add(new CategoryRequestReview { RequestId = request.Id, ReviewerId = reviewer.Id, IsApproved = true });
        await context.SaveChangesAsync();

        context.CategoryRequestReviews.Add(new CategoryRequestReview { RequestId = request.Id, ReviewerId = reviewer.Id, IsApproved = true });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.True(ex.IsUniqueConstraintViolation("CategoryRequestReviews"));
    }

    [Fact]
    public void UniqueConstraintDetection_IgnoresUnrelatedFailures()
    {
        // The guard must not swallow arbitrary database errors as "duplicate".
        var notUnique = new DbUpdateException("boom", new InvalidOperationException("something else"));

        Assert.False(notUnique.IsUniqueConstraintViolation());
        Assert.False(notUnique.IsUniqueConstraintViolation("VoteRecords"));
    }
}
