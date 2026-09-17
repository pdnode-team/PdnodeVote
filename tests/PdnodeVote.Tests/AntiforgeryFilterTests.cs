using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PdnodeVote.Services;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for TODO-BUGS.md S10a. Minimal APIs receive no antiforgery validation from
/// <c>UseAntiforgery()</c> alone (verified with a probe: an untokened JSON POST was still accepted) and
/// .NET 10 has no <c>RequireAntiforgery()</c> for <c>RouteHandlerBuilder</c>, so
/// <see cref="AntiforgeryEndpointFilter"/> performs the check explicitly.
///
/// These exercise the filter's decision logic in isolation; the end-to-end behaviour (a real token is
/// accepted, a missing one rejected) was additionally verified by running the app.
/// </summary>
public class AntiforgeryFilterTests
{
    /// <summary>Minimal IAntiforgery stub; only ValidateRequestAsync outcome matters here.</summary>
    private sealed class StubAntiforgery(bool throws) : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(!throws);

        public Task ValidateRequestAsync(HttpContext httpContext) =>
            throws ? Task.FromException(new AntiforgeryValidationException("invalid")) : Task.CompletedTask;

        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
    }

    private sealed record FilterCase(
        EndpointFilterInvocationContext Context,
        Func<bool> NextWasCalled,
        IServiceProvider Services) : IDisposable
    {
        public void Dispose()
        {
            if (Services is IDisposable disposable) disposable.Dispose();
        }
    }

    private static FilterCase CreateCase(string method, bool hasCookie, bool validationThrows)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        if (hasCookie)
        {
            http.Request.Headers.Cookie = ".AspNetCore.Antiforgery.test=token-value";
        }

        var collection = new ServiceCollection();
        collection.AddSingleton<IAntiforgery>(new StubAntiforgery(validationThrows));
        collection.Configure<AntiforgeryOptions>(o => o.Cookie.Name = ".AspNetCore.Antiforgery.test");
        var services = collection.BuildServiceProvider();
        http.RequestServices = services;

        var called = false;
        var context = new EndpointFilterInvocationContextStub(http, () => called = true);
        return new FilterCase(context, () => called, services);
    }

    private sealed class EndpointFilterInvocationContextStub(HttpContext http, Action onNext)
        : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = http;
        public override IList<object?> Arguments { get; } = new List<object?>();
        public override T GetArgument<T>(int index) => default!;

        public Action OnNext { get; } = onNext;
    }

    /// <summary>Runs the filter; returns the response status code, or null when the endpoint ran.</summary>
    private static async Task<int?> RunAsync(EndpointFilterInvocationContext context, bool validationThrows)
    {
        var filter = new AntiforgeryEndpointFilter(new StubAntiforgery(validationThrows));
        var result = await filter.InvokeAsync(context, _ =>
        {
            if (context is EndpointFilterInvocationContextStub stub) stub.OnNext();
            return ValueTask.FromResult<object?>(Results.Ok());
        });

        return result is IStatusCodeHttpResult status ? status.StatusCode : null;
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SafeMethods_PassThroughWithoutValidation(string method)
    {
        using var testCase = CreateCase(method, hasCookie: false, validationThrows: false);

        var status = await RunAsync(testCase.Context, validationThrows: false);

        Assert.True(testCase.NextWasCalled(), "the endpoint should still have been invoked");
        Assert.Equal(StatusCodes.Status200OK, status); // the stub endpoint ran and returned Ok
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task UnsafeMethods_WithoutAntiforgeryCookie_AreRejected(string method)
    {
        // ValidateRequestAsync treats a missing cookie as "nothing to validate" and succeeds, so the
        // filter must fail closed on its own or a caller could bypass validation by omitting the cookie.
        using var testCase = CreateCase(method, hasCookie: false, validationThrows: false);

        var status = await RunAsync(testCase.Context, validationThrows: false);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.False(testCase.NextWasCalled(), "the endpoint must not run when validation was skipped");
    }

    [Fact]
    public async Task UnsafeMethod_WithCookieButInvalidToken_IsRejected()
    {
        using var testCase = CreateCase("POST", hasCookie: true, validationThrows: true);

        var status = await RunAsync(testCase.Context, validationThrows: true);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.False(testCase.NextWasCalled());
    }

    [Fact]
    public async Task UnsafeMethod_WithValidToken_ReachesTheEndpoint()
    {
        using var testCase = CreateCase("POST", hasCookie: true, validationThrows: false);

        var status = await RunAsync(testCase.Context, validationThrows: false);

        Assert.True(testCase.NextWasCalled());
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public void PollAndAdminGroups_InstallTheFilter()
    {
        // Guards against a group silently losing the opt-in.
        static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdnodeVote.csproj"))) dir = dir.Parent;
            return dir!.FullName;
        }

        foreach (var file in new[] { "PollEndpoints.cs", "AdminEndpoints.cs" })
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "Endpoints", file));
            Assert.Contains("AddEndpointFilter<AntiforgeryEndpointFilter>()", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TokenEndpoint_IsMapped()
    {
        static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdnodeVote.csproj"))) dir = dir.Parent;
            return dir!.FullName;
        }

        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));
        Assert.Contains("MapAntiforgeryTokenEndpoint()", program, StringComparison.Ordinal);
    }
}
