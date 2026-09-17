using Microsoft.AspNetCore.Antiforgery;
using PdnodeVote.Client.Models;

namespace PdnodeVote.Endpoints;

/// <summary>
/// Wires up antiforgery for the minimal-API surface.
/// </summary>
/// <remarks>
/// Minimal APIs receive no antiforgery validation from <c>UseAntiforgery()</c> alone, so state-changing
/// endpoints add <see cref="Services.AntiforgeryEndpointFilter"/> explicitly and clients fetch the token
/// from <see cref="MapAntiforgeryTokenEndpoint"/>. See the filter for the full reasoning.
/// </remarks>
public static class AntiforgeryEndpoints
{
    /// <summary>
    /// Exposes the antiforgery request token. Safe to call anonymously: it only issues a token and the
    /// cookie pair it is bound to.
    /// </summary>
    public static void MapAntiforgeryTokenEndpoint(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/antiforgery/token", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            return Results.Ok(new AntiforgeryTokenDto { Token = tokens.RequestToken });
        });
    }
}

/// <summary>Response body for <c>GET /api/antiforgery/token</c>.</summary>
public class AntiforgeryTokenDto
{
    public string? Token { get; set; }
}
