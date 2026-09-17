using System.Net;

/// <summary>
/// Resolves the caller's IP address for rate limiting / duplicate-vote checks.
/// </summary>
/// <remarks>
/// This must never read forwarding headers directly: X-Forwarded-For is client-controlled and
/// was previously trusted verbatim, which allowed a guest to forge a new IP per request and
/// bypass the per-IP vote limit. <c>Connection.RemoteIpAddress</c> is either the real peer
/// address or the value installed by the ForwardedHeaders middleware, which only rewrites it
/// for explicitly trusted proxies.
/// </remarks>
public static class ClientIpAccessor
{
    public const string FallbackIp = "127.0.0.1";

    public static string GetClientIp(HttpContext? context) =>
        context?.Connection?.RemoteIpAddress?.ToString() ?? FallbackIp;

    public static string GetClientIp(IHttpContextAccessor accessor) =>
        GetClientIp(accessor.HttpContext);
}
