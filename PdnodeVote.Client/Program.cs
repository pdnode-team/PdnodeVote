using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using PdnodeVote.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

builder.Services.AddMudServices();

// Antiforgery is opted into server-side by the state-changing API endpoints (minimal APIs get no
// validation from UseAntiforgery() alone). This client half fetches the token and attaches it.
//
// The token fetch uses its own bare HttpClient rather than the shared one. The shared instance's
// handler chain contains AntiforgeryHeaderHandler, which depends on this provider, so letting the
// provider resolve HttpClient would close a construction cycle (HttpClient -> provider -> HttpClient)
// and risk the request synchronously waiting on itself.
builder.Services.AddScoped<IAntiforgeryTokenProvider>(_ => new AntiforgeryTokenProvider(
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) }));

builder.Services.AddScoped(sp =>
{
    var cookieHandler = new CookieHandler
    {
        InnerHandler = new HttpClientHandler()
    };
    var antiforgeryHandler = new AntiforgeryHeaderHandler(sp.GetRequiredService<IAntiforgeryTokenProvider>())
    {
        InnerHandler = cookieHandler
    };
    return new HttpClient(antiforgeryHandler)
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
    };
});

builder.Services.AddScoped<IPollApiClient, PollApiClient>();
builder.Services.AddScoped<IAdminApiClient, AdminApiClient>();

// Lets read failures surface in the UI instead of rendering as an empty "no results" state.
builder.Services.AddScoped<ApiErrorState>();

await builder.Build().RunAsync();
