using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 4: <see cref="ExecPlan.Api.Middleware.AppExceptionMiddleware"/>'s content negotiation between
/// the MVC admin's HTML surface and the REST <c>/api/*</c> JSON surface. HTML assertions hit the
/// Development-only <c>GET /admin/_throw/{kind}</c> route (<c>ThrowController</c>, confirmed active
/// under <see cref="TestAppFactory"/> because its hosted <c>Program</c> runs as
/// <c>EnvironmentName=Development</c> — the same gate that route uses in production) which throws each
/// <see cref="ExecPlan.Application.Common.AppException.Kind"/> on demand, so this class doesn't need a
/// real business action that happens to fail with a particular kind. The JSON assertion instead reuses
/// a genuine Phase 1 API 404 (<c>GET /api/v1/activations/{id}/dashboard</c> for a non-existent
/// activation, which <c>DashboardService</c> reports via <c>AppException.NotFound</c>) authenticated as
/// the seeded admin — proving the middleware's <c>/api/*</c> branch is untouched by this task.
/// </summary>
public class ErrorMappingTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ErrorMappingTests(TestAppFactory factory) => _factory = factory;

    private sealed record LoginResponseDto(string AccessToken);

    /// <summary>
    /// Reads a value straight out of <c>SharedResource.ar.resx</c> so the assertion tracks the real
    /// localized string instead of a second, hand-copied literal that could silently drift from it
    /// (same technique as <see cref="AuthFlowTests"/>).
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

    /// <summary>Razor HTML-encodes <c>@Localizer[...]</c> output, so Arabic text round-trips the wire as numeric character references.</summary>
    private static async Task<string> DecodedBodyAsync(HttpResponseMessage res)
        => WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());

    [Fact]
    public async Task Html_notfound_renders_404_view()
    {
        // Default client follows the middleware's redirect automatically, landing on the final themed
        // 404 page — proving both the redirect target AND the rendered view content.
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/admin/_throw/notfound");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await DecodedBodyAsync(res);
        body.Should().Contain(ResxValue("Error.NotFound"));
    }

    [Fact]
    public async Task Html_forbidden_redirects_to_denied()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });

        var res = await client.GetAsync("/admin/_throw/forbidden");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/denied");
    }

    [Fact]
    public async Task Html_unauthorized_redirects_to_login()
    {
        // Uses WebTestHelpers.NewClient (no auto-redirect, https base) so the raw 302 + Location can be
        // asserted directly instead of following it — this is the returnUrl-escaping branch of
        // AppExceptionMiddleware.RedirectHtml (AppException.Kind.Unauthorized).
        var client = WebTestHelpers.NewClient(_factory);

        var res = await client.GetAsync("/admin/_throw/unauthorized");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().StartWith("/admin/login?returnUrl=");
    }

    [Fact]
    public async Task Html_validation_shows_message()
    {
        // Covers the middleware's Validation/Conflict HTML branch: redirect to /admin/error?msg=<escaped
        // ex.Message>, then follow it manually to confirm ErrorPageController.Error echoes that message
        // via ViewBag.Msg into the rendered page (Error.cshtml).
        var client = WebTestHelpers.NewClient(_factory);

        var res = await client.GetAsync("/admin/_throw/validation");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = res.Headers.Location!.ToString();
        location.Should().StartWith("/admin/error?msg=");

        var errorPage = await client.GetAsync(location);
        errorPage.StatusCode.Should().Be(HttpStatusCode.BadRequest); // ErrorPageController.Error sets 400
        var body = await DecodedBodyAsync(errorPage);
        body.Should().Contain("Test validation (ThrowController).");
    }

    [Fact]
    public async Task Html_unhandled_non_app_exception_redirects_to_generic_error()
    {
        // An unknown {kind} makes ThrowController throw a plain ArgumentOutOfRangeException (not an
        // AppException) — exercising the middleware's second catch block's HTML branch.
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/admin/_throw/bogus-kind");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest); // ErrorPageController.Error sets 400
        var body = await DecodedBodyAsync(res);
        body.Should().Contain(ResxValue("Error.Generic"));
        body.Should().NotContain("ArgumentOutOfRangeException"); // no stack trace / exception detail leaked
    }

    [Fact]
    public async Task Api_notfound_still_returns_json()
    {
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            userName = TestAppFactory.AdminUserName,
            password = TestAppFactory.AdminPassword,
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // SystemAdmin satisfies ActivationsController.Dashboard's Roles gate; the id is guaranteed not
        // to exist, so DashboardService throws AppException.NotFound.
        var res = await client.GetAsync($"/api/v1/activations/{Guid.NewGuid()}/dashboard");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("\"kind\"").And.Contain("NotFound");
        body.Should().NotContain("<html");
    }
}
