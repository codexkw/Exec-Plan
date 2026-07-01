using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Shared HTTP helpers for exercising the MVC admin area's cookie sign-in flow end to end. Every web
/// test built from Task 3 onward should create its client via <see cref="NewClient"/> rather than the
/// bare <c>WebApplicationFactory.CreateClient()</c> so the Secure <c>AdminCookie</c> (Program.cs sets
/// <c>Cookie.SecurePolicy = CookieSecurePolicy.Always</c>) actually round-trips: TestServer derives the
/// request's <c>HttpContext.Request.Scheme</c>/<c>IsHttps</c> from the <see cref="HttpClient"/>'s
/// <c>BaseAddress</c>, so with the default <c>http://localhost</c> base address the Secure cookie would
/// never be sent back by the client's cookie container, and every "authenticated vs not" assertion below
/// would spuriously read as "never authenticated" regardless of what the controllers actually did.
/// </summary>
public static class WebTestHelpers
{
    /// <summary>
    /// Canonical client factory for web tests: cookie-aware, no auto-redirect (so tests can assert on
    /// the raw 3xx + Location instead of following it), and an HTTPS base address so the Secure
    /// AdminCookie survives the round trip inside TestServer.
    /// </summary>
    public static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    /// <summary>Scrapes the antiforgery hidden field's current value out of a rendered form page.</summary>
    private static async Task<string> AntiForgeryTokenAsync(HttpClient c, string getUrl)
    {
        var html = await c.GetStringAsync(getUrl);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// GETs <paramref name="getUrl"/> to obtain a live antiforgery token (its paired cookie is captured
    /// automatically by the client's cookie handler), then POSTs <paramref name="fields"/> plus that
    /// token, form-url-encoded, to <paramref name="postUrl"/>. <paramref name="getUrl"/> and
    /// <paramref name="postUrl"/> need not be the same page — the antiforgery cookie/token pair is valid
    /// for any <c>[ValidateAntiForgeryToken]</c> action in the app, not just the one that rendered it.
    /// </summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient c, string getUrl, string postUrl, IDictionary<string, string> fields)
    {
        var token = await AntiForgeryTokenAsync(c, getUrl);
        fields["__RequestVerificationToken"] = token;
        return await c.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    /// <summary>
    /// Posts credentials to <c>/admin/login</c> using the antiforgery token scraped from that same page,
    /// and returns the same client (now cookie-authenticated on success, per the caller's redirect/OK
    /// check). Throws if the login endpoint responded with anything other than a redirect (success) or
    /// 200 (re-rendered form, e.g. invalid credentials) — a genuinely unexpected status (500, 400, ...)
    /// should fail loudly rather than be silently treated as "not logged in".
    /// </summary>
    public static async Task<HttpClient> LoginAsync(HttpClient c, string user, string pass)
    {
        var res = await PostFormAsync(c, "/admin/login", "/admin/login",
            new Dictionary<string, string> { ["UserName"] = user, ["Password"] = pass });

        if (res.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.OK))
        {
            throw new InvalidOperationException($"login failed: {res.StatusCode}");
        }

        return c;
    }
}
