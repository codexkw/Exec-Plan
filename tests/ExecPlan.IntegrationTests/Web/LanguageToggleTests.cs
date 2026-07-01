using System.Net;
using FluentAssertions;
using Xunit;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Regression guard for the "language switch 400 after login" bug: the layout's culture-toggle form is
/// <c>[AllowAnonymous]</c> but its antiforgery token is minted on cookie-authenticated pages, so the POST
/// must authenticate the AdminCookie scheme (default host scheme is JWT) or antiforgery validation
/// compares token(cookie-user) vs current(anonymous) and 400s. Both the anonymous (login page) and the
/// authenticated toggles must redirect (302), never 400.
/// </summary>
public sealed class LanguageToggleTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public LanguageToggleTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Toggle_before_login_redirects()
    {
        var c = WebTestHelpers.NewClient(_factory);
        var res = await WebTestHelpers.PostFormAsync(c, "/admin/login", "/admin/language",
            new Dictionary<string, string> { ["culture"] = "en", ["returnUrl"] = "/admin/login" });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/login");
    }

    [Fact]
    public async Task Toggle_after_login_redirects_not_400()
    {
        var c = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(c, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        var res = await WebTestHelpers.PostFormAsync(c, "/admin/plans", "/admin/language",
            new Dictionary<string, string> { ["culture"] = "en", ["returnUrl"] = "/admin/plans" });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect, "authenticated toggle must not 400 on antiforgery");
        res.Headers.Location!.ToString().Should().Be("/admin/plans");
    }

    [Fact]
    public async Task Toggle_after_login_actually_sets_culture_cookie()
    {
        var c = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(c, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        var res = await WebTestHelpers.PostFormAsync(c, "/admin/plans", "/admin/language",
            new Dictionary<string, string> { ["culture"] = "en", ["returnUrl"] = "/admin/plans" });

        res.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        string.Join(";", cookies!).Should().Contain(".AspNetCore.Culture");
    }
}
