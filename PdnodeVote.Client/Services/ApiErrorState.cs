namespace PdnodeVote.Client.Services;

/// <summary>
/// Tracks the most recent failed *read* (GET) so the UI can tell "the server returned nothing"
/// apart from "the request never succeeded".
/// </summary>
/// <remarks>
/// Every read used to swallow its exception and return an empty list / null, which rendered a
/// network error, a 500 or a 429 as content states such as "No polls found" or "User not found".
/// Write calls already surface failures through <see cref="ServiceResult"/> and do not use this.
///
/// Registered scoped, so it survives client-side navigation for the session and resets on reload.
/// </remarks>
public class ApiErrorState
{
    /// <summary>Technical description of the failure (exception message or HTTP status).</summary>
    public string? Message { get; private set; }

    /// <summary>What the user was trying to load, e.g. "the poll feed".</summary>
    public string? Context { get; private set; }

    /// <summary>Set by the page that owns the failed load so the banner can offer a retry.</summary>
    public Func<Task>? Retry { get; set; }

    public bool HasError => Message is not null;

    public event Action? Changed;

    public void Report(string context, Exception ex)
    {
        Message = Describe(ex);
        Context = context;
        NotifyChanged();
    }

    public void Clear()
    {
        if (Message is null && Context is null) return;

        Message = null;
        Context = null;
        Retry = null;
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        var handler = Changed;
        if (handler is null) return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action)subscriber)();
            }
            catch
            {
                // A failing subscriber must not hide the error from the others.
            }
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http when http.StatusCode is not null =>
            $"the server responded with {(int)http.StatusCode} ({http.StatusCode})",
        HttpRequestException => "the server could not be reached",
        TaskCanceledException => "the request timed out",
        _ => ex.Message
    };
}
