using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using PdnodeVote.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

builder.Services.AddMudServices();

builder.Services.AddScoped(sp =>
{
    var handler = new CookieHandler
    {
        InnerHandler = new HttpClientHandler()
    };
    return new HttpClient(handler)
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
    };
});

builder.Services.AddScoped<IPollApiClient, PollApiClient>();
builder.Services.AddScoped<IAdminApiClient, AdminApiClient>();

// Lets read failures surface in the UI instead of rendering as an empty "no results" state.
builder.Services.AddScoped<ApiErrorState>();

await builder.Build().RunAsync();
