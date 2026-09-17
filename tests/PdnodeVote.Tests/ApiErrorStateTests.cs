using System.Net;
using PdnodeVote.Client.Services;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for TODO-BUGS.md C3: reads used to swallow every exception and return an empty
/// collection, so a failed request rendered as "No polls found". ApiErrorState carries the failure to
/// the UI instead.
/// </summary>
public class ApiErrorStateTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdnodeVote.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void NewState_HasNoError()
    {
        var state = new ApiErrorState();

        Assert.False(state.HasError);
        Assert.Null(state.Message);
        Assert.Null(state.Context);
    }

    [Fact]
    public void Report_RecordsContextAndRaisedChanged()
    {
        var state = new ApiErrorState();
        var raised = 0;
        state.Changed += () => raised++;

        state.Report("the poll feed", new HttpRequestException("boom"));

        Assert.True(state.HasError);
        Assert.Equal("the poll feed", state.Context);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Report_DescribesHttpStatus()
    {
        var state = new ApiErrorState();

        state.Report("the poll feed", new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable));

        Assert.Contains("503", state.Message);
    }

    [Fact]
    public void Report_DescribesUnreachableServerAndTimeout()
    {
        var unreachable = new ApiErrorState();
        unreachable.Report("the poll feed", new HttpRequestException("no route"));
        Assert.Contains("could not be reached", unreachable.Message);

        var timeout = new ApiErrorState();
        timeout.Report("the poll feed", new TaskCanceledException());
        Assert.Contains("timed out", timeout.Message);
    }

    [Fact]
    public void Clear_ResetsEverythingIncludingRetry()
    {
        var state = new ApiErrorState { Retry = () => Task.CompletedTask };
        state.Report("your polls", new HttpRequestException("x"));

        state.Clear();

        Assert.False(state.HasError);
        Assert.Null(state.Message);
        Assert.Null(state.Context);
        Assert.Null(state.Retry);
    }

    [Fact]
    public void Clear_OnCleanState_DoesNotRaiseChanged()
    {
        var state = new ApiErrorState();
        var raised = 0;
        state.Changed += () => raised++;

        state.Clear();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Report_OneFailingSubscriber_DoesNotBlockOthers()
    {
        var state = new ApiErrorState();
        var secondRan = false;
        state.Changed += () => throw new InvalidOperationException("subscriber blew up");
        state.Changed += () => secondRan = true;

        state.Report("the poll feed", new HttpRequestException("x"));

        Assert.True(secondRan);
    }

    // --- source contracts: the wiring that cannot be exercised without a renderer/HTTP stack ---

    [Fact]
    public void PollApiClient_ReportsFailedReads()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "PdnodeVote.Client", "Services", "PollApiClient.cs"));

        Assert.Contains("ApiErrorState apiErrors", source, StringComparison.Ordinal);
        Assert.Contains("apiErrors.Report(context, ex);", source, StringComparison.Ordinal);

        // Every read context should be reported, not just a couple of them.
        foreach (var ctx in new[] { "the poll list", "your polls", "the poll feed", "the comments", "this poll" })
        {
            Assert.Contains($"\"{ctx}\"", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NotificationBell_MutatesOnlyOnTheRendererThread()
    {
        // The hub callback used to mutate _unreadCount/_notifications on the SignalR thread and only
        // then hop to the UI thread.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "PdnodeVote.Client", "Shared", "NotificationBell.razor"));

        var handlerIndex = source.IndexOf("_hubConnection.On<int, NotificationDto>(\"NotificationReceived\"", StringComparison.Ordinal);
        Assert.True(handlerIndex >= 0, "NotificationReceived handler not found");

        var handlerEnd = source.IndexOf("});", handlerIndex, StringComparison.Ordinal);
        var handler = source[handlerIndex..handlerEnd];

        var invokeIndex = handler.IndexOf("InvokeAsync(", StringComparison.Ordinal);
        var firstMutation = handler.IndexOf("_notifications.Insert", StringComparison.Ordinal);

        Assert.True(invokeIndex >= 0, "handler does not use InvokeAsync");
        Assert.True(firstMutation > invokeIndex, "the list is mutated before InvokeAsync");
    }

    [Fact]
    public void MainLayout_RendersTheErrorBanner()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "Components", "Layout", "MainLayout.razor"));

        Assert.Contains("<ApiErrorBanner", source, StringComparison.Ordinal);
    }
}
