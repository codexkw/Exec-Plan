using System.Net;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 3: the MVC admin area's cookie sign-in flow end to end over real HTTP (login POST, bad
/// credentials, Member rejection, logout, open-redirect guard) against the hosted app via
/// <see cref="TestAppFactory"/>/<see cref="WebTestHelpers"/>.
///
/// <see cref="TestAppFactory"/> seeds only its own single "admin" user (a different account than the
/// dev-only <c>DataSeeder</c>'s), so this class seeds its own manager/leader/member users directly into
/// the shared SQLite database (idempotently — <see cref="EnsureRoleUsersSeeded"/> guards against
/// re-adding them across the class's several test methods, which all share one <see cref="TestAppFactory"/>
/// instance per xUnit's <c>IClassFixture</c>).
///
/// <c>/admin/plans</c> and <c>/admin/activations</c> are not built yet (later Phase 2 tasks add them) —
/// attribute + conventional routing both 404 for them right now, for anyone, authenticated or not. So
/// tests that need "unauthenticated access to a protected route redirects to login" target <c>/admin</c>
/// itself (<c>HomeController</c>, already gated behind the AdminCookie scheme) instead of
/// <c>/admin/plans</c> — same property under test (the cookie, not a specific downstream page), without
/// depending on controllers this task doesn't build.
/// </summary>
public class AuthFlowTests : IClassFixture<TestAppFactory>
{
    private const string Password = "Passw0rd!";
    private const string ManagerUserName = "manager";
    private const string LeaderUserName = "leader";
    private const string MemberUserName = "member";

    private readonly TestAppFactory _factory;

    public AuthFlowTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureRoleUsersSeeded();
    }

    private void EnsureRoleUsersSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var ctx = sp.GetRequiredService<ExecPlanDbContext>();
        if (ctx.Users.Any(u => u.UserName == ManagerUserName))
        {
            return; // already seeded by an earlier test method in this class
        }

        var hasher = sp.GetRequiredService<IPasswordHasher>();
        var org = new Organization { Name = "AuthFlow Test Org" };
        ctx.Organizations.Add(org);

        var hash = hasher.Hash(Password);
        ctx.Users.AddRange(
            new User
            {
                UserName = ManagerUserName, PasswordHash = hash, FullName = "Auth Flow Manager",
                Phone = "+96500000201", Role = UserRole.PlanManager, OrganizationId = org.Id, IsActive = true,
            },
            new User
            {
                UserName = LeaderUserName, PasswordHash = hash, FullName = "Auth Flow Leader",
                Phone = "+96500000202", Role = UserRole.TeamLeader, OrganizationId = org.Id, IsActive = true,
            },
            new User
            {
                UserName = MemberUserName, PasswordHash = hash, FullName = "Auth Flow Member",
                Phone = "+96500000203", Role = UserRole.TeamMember, OrganizationId = org.Id, IsActive = true,
            });
        ctx.SaveChanges();
    }

    /// <summary>
    /// Reads a value straight out of <c>SharedResource.ar.resx</c> so the test asserts against the real
    /// localized string instead of a second, hand-copied literal that could silently drift from it.
    /// <c>[CallerFilePath]</c> resolves relative to this source file's own path (not the test run's
    /// working/output directory), so it's independent of how/where the test binaries get built.
    /// </summary>
    private static string ResxValue(string key, [CallerFilePath] string here = "")
    {
        var webDir = Path.GetDirectoryName(here)!; // .../tests/ExecPlan.IntegrationTests/Web
        var repoRoot = Path.GetFullPath(Path.Combine(webDir, "..", "..", ".."));
        var resxPath = Path.Combine(repoRoot, "src", "ExecPlan.Api", "Resources", "SharedResource.ar.resx");
        var doc = XDocument.Load(resxPath);
        return doc.Root!.Elements("data")
            .First(e => (string)e.Attribute("name")! == key)
            .Element("value")!.Value;
    }

    /// <summary>
    /// Razor HTML-encodes <c>@Localizer[...]</c> output by default (its <c>HtmlEncoder</c> represents
    /// non-ASCII characters, including Arabic, as <c>&amp;#xNNNN;</c> numeric character references —
    /// standard, browser-correct HTML, not a bug), so a raw-Arabic-string <c>Contain</c> check against
    /// the wire response body needs the entities decoded back to literal characters first.
    /// </summary>
    private static async Task<string> DecodedBodyAsync(HttpResponseMessage res)
        => WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());

    [Fact]
    public async Task Manager_logs_in_and_lands_on_plans()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var res = await client.GetAsync("/admin");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/plans");
    }

    [Fact]
    public async Task Bad_credentials_re_render_with_generic_error()
    {
        var client = WebTestHelpers.NewClient(_factory);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/login", "/admin/login",
            new Dictionary<string, string> { ["UserName"] = ManagerUserName, ["Password"] = "wrong-password" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DecodedBodyAsync(res);
        body.Should().Contain(ResxValue("Login.Invalid"));

        // No auth cookie was issued: the same client still can't reach a protected route.
        var admin = await client.GetAsync("/admin");
        admin.StatusCode.Should().Be(HttpStatusCode.Redirect);
        admin.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Member_is_rejected_from_web()
    {
        var client = WebTestHelpers.NewClient(_factory);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/login", "/admin/login",
            new Dictionary<string, string> { ["UserName"] = MemberUserName, ["Password"] = Password });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DecodedBodyAsync(res);
        body.Should().Contain(ResxValue("Login.MemberBlocked"));

        var admin = await client.GetAsync("/admin");
        admin.StatusCode.Should().Be(HttpStatusCode.Redirect);
        admin.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Logout_clears_cookie()
    {
        // Logout is [Authorize(AdminCookie)] + [ValidateAntiForgeryToken]. ASP.NET binds the antiforgery
        // token pair to the request's identity, so a token scraped from the anonymous /admin/login page
        // is rejected once the authenticated principal is in play — and at this point in the build no
        // authenticated page renders a form to scrape a valid token from. So we mint a token+cookie pair
        // bound to the manager principal via IAntiforgery and inject the cookie into the client's own jar
        // (via an explicit CookieContainerHandler) alongside the real AdminCookie that login sets.
        var cookieHandler = new CookieContainerHandler();
        var client = _factory.CreateDefaultClient(new Uri("https://localhost"), cookieHandler);

        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var afterLogin = await client.GetAsync("/admin");
        afterLogin.StatusCode.Should().Be(HttpStatusCode.Redirect);
        afterLogin.Headers.Location!.ToString().Should().Be("/admin/plans"); // proves the cookie DID authenticate

        string requestToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var manager = ctx.Users.Single(u => u.UserName == ManagerUserName);

            var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
            var fakeContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            fakeContext.Request.Scheme = "https";
            fakeContext.Request.Host = new HostString("localhost");
            fakeContext.User = AdminClaimsPrincipalFactory.Create(
                new AppUserPrincipal(manager.Id, manager.Role, manager.FullName, manager.UserName));

            var tokens = antiforgery.GetAndStoreTokens(fakeContext);
            requestToken = tokens.RequestToken!;

            // The matching antiforgery cookie was written to the fake response; feed the raw Set-Cookie
            // into the client jar (SetCookies handles the base64 value + attributes safely) so the real
            // logout request carries a cookie/token pair bound to the manager identity. Same cookie name
            // as the anonymous one from the login GET, so it replaces it.
            var setCookie = fakeContext.Response.Headers["Set-Cookie"].ToString();
            cookieHandler.Container.SetCookies(new Uri("https://localhost"), setCookie);
        }

        var logout = await client.PostAsync("/admin/logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = requestToken }));
        logout.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var afterLogout = await client.GetAsync("/admin");
        afterLogout.StatusCode.Should().Be(HttpStatusCode.Redirect);
        afterLogout.Headers.Location!.ToString().Should().Contain("/admin/login");
    }

    [Fact]
    public async Task Open_redirect_is_blocked()
    {
        var client = WebTestHelpers.NewClient(_factory);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/login", "/admin/login",
            new Dictionary<string, string>
            {
                ["UserName"] = ManagerUserName,
                ["Password"] = Password,
                ["ReturnUrl"] = "https://evil.com",
            });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin");
    }
}
