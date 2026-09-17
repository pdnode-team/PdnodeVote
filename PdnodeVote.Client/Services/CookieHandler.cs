using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace PdnodeVote.Client.Services;

public class CookieHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsBrowser())
        {
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
