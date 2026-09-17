using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;   // [FromForm]
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MudBlazor.Services;
using PdnodeVote.Client.Services;
using PdnodeVote.Components;
using PdnodeVote.Components.Account;
using PdnodeVote.Data;
using PdnodeVote.Endpoints;
using PdnodeVote.Hubs;
using PdnodeVote.RateLimiting;
using PdnodeVote.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization();

// MudBlazor 服务
builder.Services.AddMudServices();

builder.Services.AddAppRateLimiting();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.Name = ".PdnodeVote.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);

    // API callers parse JSON and need to tell "not signed in" apart from "no such endpoint"; the
    // cookie handler's default reaction to a challenge is a 302 to the login page with an HTML body,
    // which ApiResponse.ReadAsync cannot interpret. Page requests keep that redirect so a browser
    // with an expired session still lands on the login page.
    options.Events = ApiAuthenticationResponses.CreateCookieEvents();
});

// Disconnected-circuit retention deliberately uses the framework defaults. The previous override
// (DisconnectedCircuitMaxRetained = 0 + RetentionPeriod = Zero) is implemented by .NET 10 as
// MemoryCacheOptions.SizeLimit = 0, which evicts a disconnected circuit immediately, so a Blazor
// reconnect could never resume it. The policy lives in CircuitRetention so a regression test can
// assert it without spinning up the host.
builder.Services.Configure<CircuitOptions>(CircuitRetention.Configure);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

// Resolve a relative SQLite path against the content root instead of the process working directory.
// Microsoft.Data.Sqlite resolves DataSource against the CWD, so starting the app from a different
// directory silently created (and seeded) a *different* database while the seeder prepared the folder
// next to the content root. An absolute path removes that ambiguity; in-memory and fully-qualified
// paths pass through untouched.
{
    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
    if (!string.IsNullOrWhiteSpace(csb.DataSource)
        && csb.DataSource != ":memory:"
        && !csb.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        && !Path.IsPathRooted(csb.DataSource))
    {
        csb.DataSource = Path.Combine(builder.Environment.ContentRootPath, csb.DataSource);
        connectionString = csb.ToString();
    }
}

// 注册 DbContextFactory 和 DbContext
// PendingModelChangesWarning is downgraded on purpose: EF Core 9+ raises it even when the model
// and the migration snapshot are schema-identical (verified: `dotnet ef migrations add` produces an
// empty migration here), yet it would otherwise abort Database.Migrate() on a brand-new database.
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString)
        .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning)));

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false; // 免邮箱确认直接登录

        // Password strength + lockout policy live in SecurityPolicy so tests assert the real values.
        SecurityPolicy.Apply(options);

        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IEmailSender<ApplicationUser>, IdentityEmailSender>();
builder.Services.AddScoped<IEmailNotificationService, EmailNotificationService>();

// 核心投票与管理服务
builder.Services.AddSingleton<IPollEventNotifier, PollEventNotifier>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<PollService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<ICommentService, CommentService>();
builder.Services.AddScoped<IAdminService, AdminService>();

// SignalR Real-time Pub/Sub Hub
builder.Services.AddSignalR();
builder.Services.AddHostedService<PollNotificationBroadcaster>();

// Server-side implementations of API client interfaces (for smooth server-side pre-rendering)
builder.Services.AddScoped<IPollApiClient, ServerPollApiClient>();
builder.Services.AddScoped<IAdminApiClient, ServerAdminApiClient>();
builder.Services.AddHttpClient();

// Shared read-error state consumed by the client components. Must be registered here as well as in
// PdnodeVote.Client/Program.cs: InteractiveAuto components are pre-rendered and can run on the server
// circuit, both of which resolve services from this container.
builder.Services.AddScoped<PdnodeVote.Client.Services.ApiErrorState>();

// X-Forwarded-* is only honoured when a trusted proxy is explicitly configured. The app
// previously read the X-Forwarded-For header directly, which let any client forge its own IP
// and defeat the per-IP guest-vote limit.
//
// NOTE: clearing KnownProxies/KnownIPNetworks is NOT sufficient to disable trusting the header
// — verified on .NET 10 that the middleware still rewrites RemoteIpAddress when both lists are
// empty. The middleware is therefore only mounted when trust is configured at all.
var forwardedTrust = builder.Configuration.GetSection("ForwardedHeaders");
var trustedProxies = forwardedTrust.GetSection("KnownProxies").Get<string[]>() ?? [];
var trustedIPNetworks = forwardedTrust.GetSection("KnownIPNetworks").Get<string[]>() ?? [];
var trustForwardedHeaders = trustedProxies.Length > 0 || trustedIPNetworks.Length > 0;

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = null;
    options.RequireHeaderSymmetry = false;

    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();

    foreach (var proxy in trustedProxies)
    {
        if (System.Net.IPAddress.TryParse(proxy, out var parsedProxy)) options.KnownProxies.Add(parsedProxy);
    }
    foreach (var network in trustedIPNetworks)
    {
        if (System.Net.IPNetwork.TryParse(network, out var parsedNetwork)) options.KnownIPNetworks.Add(parsedNetwork);
    }
});

var app = builder.Build();

// Must run before anything that consumes Connection.RemoteIpAddress / Request.Scheme.
if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

// 自动执行数据库迁移并填充初始示例数据
await DataSeeder.SeedAsync(app.Services);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
// Status-code pages are mounted conditionally, never globally: re-executing a failed /_framework,
// /_content or /api response would rewrite the status codes and bodies that the WebAssembly client
// and ApiResponse.ReadAsync depend on (including the JSON 401/403 challenges configured above).
//
// Note this branch is currently a no-op for unmatched page URLs - verified by running the app:
// GET /definitely-not-a-page answers "404 Not Found" with an empty body, i.e. the re-execute never
// fires because the Razor Components endpoint owns every non-file path. The original comment claimed
// a 404-page redirect that does not happen. It is left in place (rather than deleted) because the
// predicate is still the correct guard if status-code pages are ever made to work, and because
// changing response behaviour here is riskier than documenting it.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/_framework") 
                    && !context.Request.Path.StartsWithSegments("/_content")
                    && !context.Request.Path.StartsWithSegments("/api"), appBuilder =>
{
    appBuilder.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
});
app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var contentType = context.Response.ContentType;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; " +
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net; " +
            "font-src 'self' https://fonts.gstatic.com https://cdn.jsdelivr.net data:; " +
            "img-src 'self' data:; " +
            "connect-src 'self' ws: wss:; " +
            "base-uri 'self'; form-action 'self'; frame-ancestors 'none';";

        if (contentType != null && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
        }
        return Task.CompletedTask;
    });
    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.UseAntiforgery();

app.UseStaticFiles();

// MapStaticAssets only serves files listed in the build-time manifest, so images written to
// wwwroot/uploads at runtime are never served by it. UseStaticFiles above covers those.
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PdnodeVote.Client._Imports).Assembly);

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

// REST API Endpoints
app.MapPollEndpoints();
app.MapAdminEndpoints();

// Supplies the antiforgery request token that state-changing API endpoints require
// (minimal APIs get no validation from UseAntiforgery() alone).
app.MapAntiforgeryTokenEndpoint();

// SignalR Real-Time Hub
app.MapHub<PollHub>("/hubs/poll");

// POST /Account/Logout is owned here. The Identity template handler in
// MapAdditionalIdentityEndpoints() composes its redirect as LocalRedirect($"~/{returnUrl}"),
// which throws for every value of returnUrl (missing/empty -> 400 from required [FromForm] binding,
// "/" -> "~//" -> InvalidOperationException -> 500). Registering both silently shadowed this one.
app.MapPost("/Account/Logout", async (HttpContext http, SignInManager<ApplicationUser> signInManager, IAntiforgery antiforgery, [FromForm] string? returnUrl) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.Redirect("/");
    }

    await signInManager.SignOutAsync();

    // Only honour returnUrl when it cannot leave this origin (guards against open redirects).
    var target = !string.IsNullOrWhiteSpace(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative) && !returnUrl.StartsWith("//")
        ? returnUrl
        : "/";
    return Results.Redirect(target);
});

app.Run();

/// <summary>
/// Challenge / access-denied responses for cookie-authenticated requests.
///
/// Requests under /api are answered with a JSON body and the matching 401/403 status - the same shape
/// RateLimiting/AppRateLimiting.cs uses for 429 - because the WASM client's ApiResponse.ReadAsync
/// parses JSON and would otherwise receive an HTML login page for "not signed in" and "no such
/// endpoint" alike. Every other request keeps the cookie handler's normal redirect to
/// LoginPath/AccessDeniedPath so page navigation still reaches the login page.
/// </summary>
public static class ApiAuthenticationResponses
{
    public const string UnauthorizedMessage = "Authentication is required to access this resource.";
    public const string ForbiddenMessage = "You do not have permission to access this resource.";

    public static CookieAuthenticationEvents CreateCookieEvents() => new()
    {
        OnRedirectToLogin = context => HandleAsync(context, StatusCodes.Status401Unauthorized, UnauthorizedMessage),
        OnRedirectToAccessDenied = context => HandleAsync(context, StatusCodes.Status403Forbidden, ForbiddenMessage)
    };

    public static bool IsApiRequest(HttpContext httpContext) =>
        httpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    public static Task HandleAsync(RedirectContext<CookieAuthenticationOptions> context, int statusCode, string message)
    {
        if (!IsApiRequest(context.HttpContext))
        {
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        }

        context.Response.StatusCode = statusCode;
        // WriteAsJsonAsync sets ContentType, so the status-code-pages middleware (mounted for non-/api
        // paths only) never re-executes this response.
        return context.Response.WriteAsJsonAsync(
            new PdnodeVote.Client.Models.ServiceResult { Success = false, Message = message });
    }
}

/// <summary>
/// Blazor Server's disconnected-circuit retention policy.
///
/// A disconnected circuit is what a browser tab leaves behind while the SignalR connection is being
/// re-established; .NET 10 stores those circuits in a memory cache whose SizeLimit is derived from
/// <see cref="CircuitOptions.DisconnectedCircuitMaxRetained"/>. Setting it to 0 therefore makes an
/// immediate eviction and turns every transient network drop into a lost session.
/// </summary>
public static class CircuitRetention
{
    public static void Configure(CircuitOptions options)
    {
        options.DisconnectedCircuitMaxRetained = 100;
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
    }
}
