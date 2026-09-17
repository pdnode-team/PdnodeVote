using System.Net;
using System.Net.Http.Json;

namespace PdnodeVote.Client.Services;

/// <summary>Supplies the antiforgery request token attached to state-changing API calls.</summary>
public interface IAntiforgeryTokenProvider
{
    /// <summary>Returns a cached token, fetching one from the server on first use.</summary>
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches the antiforgery token from <c>GET /api/antiforgery/token</c> and caches it.
/// </summary>
/// <remarks>
/// Minimal APIs get no antiforgery validation from <c>app.UseAntiforgery()</c> alone (verified: a
/// request with no token is still accepted) and there is no <c>RequireAntiforgery()</c> extension for
/// <c>RouteHandlerBuilder</c>, so validation is implemented server-side in
/// <see cref="PdnodeVote.Services.AntiforgeryEndpointFilter"/> and the token has to be carried
/// explicitly by the caller. This provider is the client half of that contract.
/// </remarks>
public class AntiforgeryTokenProvider(HttpClient httpClient) : IAntiforgeryTokenProvider
{
    private string? _cachedToken;

    public async ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_cachedToken))
        {
            return _cachedToken;
        }

        try
        {
            var response = await httpClient.GetAsync("api/antiforgery/token", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            _cachedToken = payload?.Token;
        }
        catch (HttpRequestException)
        {
            // Offline or the server is down: the write will surface its own error.
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }

        return _cachedToken;
    }

    private sealed record TokenResponse(string? Token);
}

/// <summary>
/// Adds <c>RequestVerificationToken</c> to state-changing API requests so the server-side antiforgery
/// filter accepts them.
/// </summary>
/// <remarks>
/// Only GET/HEAD and same-origin relative API paths are touched; the token is fetched lazily so a
/// purely read-only session never issues the extra request. Registering both this handler and
/// <see cref="CookieHandler"/> on one <c>HttpClient</c> keeps the WebAssembly path covered.
/// </remarks>
public class AntiforgeryHeaderHandler(IAntiforgeryTokenProvider tokenProvider) : DelegatingHandler
{
    public const string HeaderName = "RequestVerificationToken";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (RequiresToken(request))
        {
            var token = await tokenProvider.GetTokenAsync(cancellationToken);
            if (!string.IsNullOrEmpty(token) && !request.Headers.Contains(HeaderName))
            {
                request.Headers.TryAddWithoutValidation(HeaderName, token);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static bool RequiresToken(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
        {
            return false;
        }

        var uri = request.RequestUri;
        if (uri is null || uri.IsAbsoluteUri) return false;

        return uri.OriginalString.StartsWith("api/", StringComparison.OrdinalIgnoreCase);
    }
}
