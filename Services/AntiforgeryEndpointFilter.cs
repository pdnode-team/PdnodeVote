using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Antiforgery;
using PdnodeVote.Client.Models;

namespace PdnodeVote.Services;

/// <summary>
/// Enforces antiforgery validation on state-changing API endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <c>app.UseAntiforgery()</c> does <b>not</b> protect minimal APIs: validation only runs for endpoints
/// carrying <see cref="IAntiforgeryMetadata"/>, and minimal APIs produce none. This was verified with a
/// probe — a JSON POST with no token at all was still accepted — and .NET 10 has no
/// <c>RequireAntiforgery()</c> extension for <c>RouteHandlerBuilder</c>. So the check is applied
/// explicitly through this filter, which is the same shape MVC/Razor Components get for free.
/// </para>
/// <para>
/// Only unsafe methods need a token; GET/HEAD/OPTIONS pass through. A failing request gets a 400 with
/// the usual <see cref="ServiceResult"/> body rather than an HTML error page, matching how the rest of
/// the API reports problems.
/// </para>
/// </remarks>
public sealed class AntiforgeryEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;


        if (!RequiresValidation(http.Request.Method))
        {
            return await next(context);
        }

        // Fail closed when the antiforgery cookie is absent. ValidateRequestAsync treats "no cookie" as
        // "nothing to validate" and returns successfully, so without this check a caller could bypass
        // validation entirely by simply omitting the cookie. Requiring it also means the request must
        // have gone through a token fetch, which is bound to the caller's identity.
        var options = http.RequestServices.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        var cookieName = options.Cookie.Name ?? ".AspNetCore.Antiforgery";
        if (!http.Request.Cookies.ContainsKey(cookieName))
        {
            return MissingToken();
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            // Cookie-authenticated state changes must prove same-origin intent. SameSite=Lax already
            // blocks the classic cross-site form post; this makes the guarantee explicit and also covers
            // a SameSite relaxation or a proxy that strips the cookie policy.
            return MissingToken();
        }

        return await next(context);
    }

    private static IResult MissingToken() => Results.BadRequest(new ServiceResult
    {
        Success = false,
        Message = "The security token for this request is missing or invalid. Reload the page and try again."
    });

    private static bool RequiresValidation(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);
}
