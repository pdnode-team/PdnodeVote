using System.Threading.RateLimiting;
using PdnodeVote.Client.Models;

namespace PdnodeVote.RateLimiting;

public static class AppRateLimiting
{
    public const string TooManyRequestsMessage = "Too many requests. Please wait a moment and try again.";

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                var http = context.HttpContext;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.Headers.RetryAfter = "60";

                var path = http.Request.Path.Value ?? "";
                if (path.StartsWith("/Account", StringComparison.OrdinalIgnoreCase))
                {
                    http.Response.ContentType = "text/html; charset=utf-8";
                    await http.Response.WriteAsync(
                        """
                        <!DOCTYPE html>
                        <html lang="en" data-bs-theme="dark">
                        <head>
                            <meta charset="utf-8" />
                            <meta name="viewport" content="width=device-width, initial-scale=1" />
                            <title>Too many attempts - Pdnode Vote</title>
                            <style>
                                html, body { margin: 0; background: #161a1f; color: #94a3b8; font-family: Inter, system-ui, sans-serif; }
                                .auth-wrapper { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 2rem 1rem; }
                                .auth-card { background: #1e242d; border: 1px solid rgba(255,255,255,.08); border-radius: 16px; padding: 2.2rem 2rem; max-width: 420px; width: 100%; text-align: center; }
                                h2 { color: #f8fafc; margin: 0 0 8px; font-size: 1.35rem; letter-spacing: -.025em; }
                                p { margin: 0 0 20px; font-size: 14px; line-height: 1.55; }
                                a { color: #34d399; text-decoration: none; font-weight: 600; font-size: 14px; }
                                a:hover { color: #10b981; }
                            </style>
                        </head>
                        <body>
                            <div class="auth-wrapper">
                                <div class="auth-card">
                                    <h2>Too many attempts</h2>
                                    <p>Please wait a minute and try again.</p>
                                    <a href="/Account/Login">Back to login</a>
                                </div>
                            </div>
                        </body>
                        </html>
                        """,
                        token);
                    return;
                }

                http.Response.ContentType = "application/json";
                await http.Response.WriteAsJsonAsync(
                    new ServiceResult { Success = false, Message = TooManyRequestsMessage },
                    token);
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var path = httpContext.Request.Path.Value ?? "";
                var method = httpContext.Request.Method;
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                if (HttpMethods.IsPost(method) && IsAuthPath(path))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"auth:{ip}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 8,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        });
                }

                if (HttpMethods.IsPost(method) && path.Contains("/vote", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"vote:{ip}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 20,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        });
                }

                if (IsWritePath(method, path))
                {
                    return RateLimitPartition.GetFixedWindowLimiter(
                        $"write:{ip}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 15,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        });
                }

                return RateLimitPartition.GetNoLimiter("none");
            });
        });

        return services;
    }

    private static bool IsAuthPath(string path) =>
        path.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Account/Register", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Account/ForgotPassword", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Account/ResetPassword", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Account/ExternalLogin", StringComparison.OrdinalIgnoreCase);

    private static bool IsWritePath(string method, string path)
    {
        if (!HttpMethods.IsPost(method) && !HttpMethods.IsPut(method) && !HttpMethods.IsDelete(method))
        {
            return false;
        }

        return path.Equals("/api/polls", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/polls/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/comments", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/category-requests", StringComparison.OrdinalIgnoreCase)
            || (HttpMethods.IsPut(method) && path.StartsWith("/api/polls/", StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsDelete(method) && path.StartsWith("/api/polls/", StringComparison.OrdinalIgnoreCase));
    }
}
