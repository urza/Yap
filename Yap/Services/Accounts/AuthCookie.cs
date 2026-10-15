namespace Yap.Services.Accounts;

/// <summary>
/// The one place that writes the sign-in cookie. Its value is the account's token (User.Token).
/// Installed apps and every current session hold this exact cookie, so the name, the flags and the
/// lifetime never change.
///
/// A sign-out also leaves a second cookie, the sign-out mark. The installed app's start URL carries a
/// login token that stays valid for days, and without the mark the next launch after a sign-out would
/// redeem it and sign the person in again (AU-22). The mark lives in the same cookie jar as the sign-in
/// cookie, so it is as device-bound as the sign-out itself, and the next sign-in removes it.
///
/// A third cookie carries the token of a pending sign-up request (AU-05): the admin approves a username, and
/// only the browser that holds the token turns the approval into an account (RegistrationGateService). A day
/// gives the admin time to answer; the sign-in that follows removes it.
/// </summary>
public static class AuthCookie
{
    public const string Name = "yap_auth";
    public const string SignedOutName = "yap_signed_out";
    public const string PendingName = "yap_pending";

    private static readonly CookieOptions Options = new()
    {
        HttpOnly = true,
        Secure = true,
        // Lax, not Strict: launching an installed PWA or tapping a push notification is an
        // app-initiated navigation, and browsers withhold Strict cookies from those. Every PWA
        // launch looked signed-out and one user made seven accounts. Lax still keeps the cookie
        // off cross-site POSTs and subresource requests, which is the CSRF protection that matters.
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(365),   // a device stays signed in for a year after its last visit (AU-18)
        Path = "/",
        IsEssential = true,
    };
    private static readonly CookieOptions PendingOptions = new(Options) { MaxAge = TimeSpan.FromDays(1) };

    public static void Set(HttpResponse response, string token)
    {
        response.Cookies.Append(Name, token, Options);
        response.Cookies.Delete(SignedOutName, Options);
    }

    public static void Clear(HttpResponse response)
    {
        response.Cookies.Delete(Name, Options);
        response.Cookies.Append(SignedOutName, "1", Options);
    }

    /// <summary>True when this device signed out and did not sign in again since.</summary>
    public static bool SignedOut(HttpRequest request) => request.Cookies.ContainsKey(SignedOutName);

    public static void SetPending(HttpResponse response, string token) => response.Cookies.Append(PendingName, token, PendingOptions);
    public static void ClearPending(HttpResponse response) => response.Cookies.Delete(PendingName, PendingOptions);
    public static string? Pending(HttpRequest request) => request.Cookies[PendingName];
}
