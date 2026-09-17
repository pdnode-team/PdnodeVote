using System.Net;
using System.Net.Http.Json;
using PdnodeVote.Client.Models;

namespace PdnodeVote.Client.Services;

internal static class ApiResponse
{
    public static async Task<ServiceResult> ReadAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return ServiceResult.Fail("Please log in to continue.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return ServiceResult.Fail("You do not have permission to do this.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            try
            {
                var limited = await response.Content.ReadFromJsonAsync<ServiceResult>();
                if (limited != null && !string.IsNullOrWhiteSpace(limited.Message))
                {
                    return limited;
                }
            }
            catch
            {
                // Non-JSON 429 bodies fall through to the shared message.
            }

            return ServiceResult.Fail("Too many requests. Please wait a moment and try again.");
        }

        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ServiceResult>();
            if (payload != null)
            {
                return payload;
            }
        }
        catch
        {
            // Login redirects and empty 401/403 bodies are not JSON.
        }

        return response.IsSuccessStatusCode
            ? ServiceResult.Ok()
            : ServiceResult.Fail($"Request failed ({(int)response.StatusCode}).");
    }
}
