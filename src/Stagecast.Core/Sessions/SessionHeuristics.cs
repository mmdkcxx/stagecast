namespace Stagecast.Core.Sessions;

public enum SessionState
{
    /// <summary>Not a website (a blank page or a local file): there is nothing to log in to.</summary>
    NoLogin,

    /// <summary>The page is not a login page and the browser holds a session cookie for the site.</summary>
    LoggedIn,

    /// <summary>The page looks like a login page, or the site has no session cookie.</summary>
    NeedsLogin,
}

/// <summary>
/// A best guess about whether a tab is logged in, made from the address and the cookies of its site. It can be wrong in both
/// directions, so the operator still looks at the preview; it exists to catch the obvious "this tab is on the sign-in page".
/// </summary>
public static class SessionHeuristics
{
    private static readonly string[] LoginPathParts =
    [
        "login", "log-in", "log_in", "signin", "sign-in", "sign_in", "auth", "oauth", "sso", "masuk", "session/new", "accounts/login", "u/login",
    ];

    private static readonly string[] LoginHostPrefixes = ["login.", "signin.", "auth.", "sso.", "id.", "accounts.", "idp."];

    /// <param name="url">The tab's current address.</param>
    /// <param name="sessionCookies">How many HttpOnly cookies (the kind sites use for sessions, unlike advertising cookies) the site has.</param>
    public static SessionState Classify(string? url, int sessionCookies)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return SessionState.NoLogin;
        if (LooksLikeLoginPage(uri)) return SessionState.NeedsLogin;
        return sessionCookies > 0 ? SessionState.LoggedIn : SessionState.NeedsLogin;
    }

    public static bool LooksLikeLoginPage(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        if (LoginHostPrefixes.Any(host.StartsWith)) return true;
        var segments = uri.AbsolutePath.ToLowerInvariant().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string path = string.Join('/', segments);
        foreach (string part in LoginPathParts)
        {
            if (part.Contains('/') ? path.Contains(part) : segments.Any(s => s == part || s.StartsWith(part + "-") || s.StartsWith(part + "_"))) return true;
        }
        return false;
    }
}
