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
builder.Services.AddScoped<IAntiforgeryTokenProvider>(sp =>
    new AntiforgeryTokenProvider(sp.GetRequiredService<HttpClient>()));

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
