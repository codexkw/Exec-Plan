using System.Net;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 5: <c>LanguageController</c>'s ar/en culture-cookie toggle, exercised over real HTTP via
/// <see cref="WebTestHelpers.NewClient"/>/<see cref="WebTestHelpers.PostFormAsync"/> (Task 3's helpers).
/// The toggle is <c>[AllowAnonymous]</c>, so every test scrapes its antiforgery token from the anonymous
/// <c>/admin/login</c> page and posts to <c>/admin/language</c> — both anonymous, so the token/cookie
/// pair validates without needing an authenticated session.
///
/// Assertions check both the structural signal (the rendered <c>&lt;html&gt;</c> tag's <c>dir</c>/<c>lang</c>
/// attributes) and a real localized string pulled straight out of <c>SharedResource.{ar,en}.resx</c> —
/// Task 3 fixed <c>_ViewImports</c> to inject <c>IStringLocalizer&lt;SharedResource&gt;</c>, so
/// <c>@Localizer[...]</c> now resolves genuine Arabic/English text instead of the raw resx key, and the
/// tests should assert against that real text rather than just the dir/lang attributes.
/// </summary>
[Collection("WebHostSequential")]
public class LocalizationTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public LocalizationTests(TestAppFactory factory) => _factory = factory;

    /// <summary>
    /// Reads a value straight out of <c>SharedResource.{culture}.resx</c> so the assertion tracks the
    /// real localized string instead of a second, hand-copied literal that could silently drift from it
    /// (same technique as <see cref="AuthFlowTests"/>/<see cref="ErrorMappingTests"/>).
    /// </summary>
    private static string ResxValue(string culture, string key, [CallerFilePath] string here = "")
    {
        var webDir = Path.GetDirectoryName(here)!; // .../tests/ExecPlan.IntegrationTests/Web
        var repoRoot = Path.GetFullPath(Path.Combine(webDir, "..", "..", ".."));
        var resxPath = Path.Combine(repoRoot, "src", "ExecPlan.Api", "Resources", $"SharedResource.{culture}.resx");
        var doc = XDocument.Load(resxPath);
        return doc.Root!.Elements("data")
            .First(e => (string)e.Attribute("name")! == key)
            .Element("value")!.Value;
    }

    /// <summary>
    /// Razor HTML-encodes <c>@Localizer[...]</c> output by default (non-ASCII, including Arabic, comes
    /// out as <c>&amp;#xNNNN;</c> numeric character references — standard, browser-correct HTML), so a
    /// raw-Arabic-string <c>Contain</c> check against the wire response body needs the entities decoded
    /// back to literal characters first.
    /// </summary>
    private static async Task<string> DecodedBodyAsync(HttpResponseMessage res)
        => WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());

    [Fact]
    public async Task Default_is_arabic_rtl()
    {
        var client = WebTestHelpers.NewClient(_factory);

        var res = await client.GetAsync("/admin/login");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DecodedBodyAsync(res);
        body.Should().Contain("dir=\"rtl\"").And.Contain("lang=\"ar\"");
        body.Should().Contain(ResxValue("ar", "Login.Title"));
    }

    [Fact]
    public async Task Toggle_to_english_sets_ltr_and_cookie()
    {
        var client = WebTestHelpers.NewClient(_factory);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/login", "/admin/language",
            new Dictionary<string, string> { ["culture"] = "en", ["returnUrl"] = "/admin/login" });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/login");
        res.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.StartsWith(".AspNetCore.Culture"));

        // Client's cookie container (HandleCookies = true) now carries the culture cookie forward.
        var next = await client.GetAsync("/admin/login");
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DecodedBodyAsync(next);
        body.Should().Contain("dir=\"ltr\"").And.Contain("lang=\"en\"");
        body.Should().Contain(ResxValue("en", "Login.Title"));
    }

    [Fact]
    public async Task Invalid_culture_falls_back_to_arabic()
    {
        var client = WebTestHelpers.NewClient(_factory);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/login", "/admin/language",
            new Dictionary<string, string> { ["culture"] = "zz", ["returnUrl"] = "/admin/login" });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var next = await client.GetAsync("/admin/login");
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DecodedBodyAsync(next);
        body.Should().Contain("dir=\"rtl\"").And.Contain("lang=\"ar\"");
    }
}
