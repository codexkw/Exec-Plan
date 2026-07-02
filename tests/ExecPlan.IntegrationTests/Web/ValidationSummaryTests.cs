using System.Net;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Phase 2.1 follow-up: the CRUD + wizard forms validated server-side and blocked bad saves, but added
/// their <c>ModelState</c> errors with hard-coded English literals that no view ever rendered — so a
/// rejected submit silently re-showed the form with no message. These tests pin the fix: an invalid POST
/// re-renders the form (200) AND surfaces the localized (Arabic, default culture) validation message via
/// the shared <c>_ValidationSummary</c> partial. All three scenarios use the admin account, which
/// satisfies both the <c>Admin</c> (CRUD create) and <c>ManagerOrAdmin</c> (wizard) gates, so no extra
/// role seeding is needed beyond <see cref="TestAppFactory"/>'s own admin.
/// </summary>
[Collection("WebHostSequential")]
public class ValidationSummaryTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public ValidationSummaryTests(TestAppFactory factory) => _factory = factory;

    /// <summary>
    /// Reads a value straight out of <c>SharedResource.ar.resx</c> (default culture is <c>ar</c>) so the
    /// assertion tracks the real localized string instead of a hand-copied literal. <c>[CallerFilePath]</c>
    /// resolves relative to this source file, independent of the test run's working directory — same
    /// approach as <c>AuthFlowTests.ResxValue</c>.
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
    /// Razor HTML-encodes non-ASCII (Arabic) output as numeric character references, so a raw-Arabic
    /// <c>Contain</c> check against the wire body needs the entities decoded back to literal characters
    /// first — same helper shape as <c>AuthFlowTests.DecodedBodyAsync</c>.
    /// </summary>
    private static async Task<string> DecodedBodyAsync(HttpResponseMessage res)
        => WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());

    [Fact]
    public async Task Org_create_with_blank_name_shows_localized_validation_summary()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/organizations/create", "/admin/organizations/create",
            new Dictionary<string, string> { ["Name"] = "" });

        res.StatusCode.Should().Be(HttpStatusCode.OK); // re-rendered form, not a redirect
        var body = await DecodedBodyAsync(res);
        body.Should().Contain(ResxValue("Validation.NameRequired"));
        body.Should().Contain("data-validation-summary");
    }

    [Fact]
    public async Task User_create_with_blank_fields_shows_localized_validation_summary()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/users/create", "/admin/users/create",
            new Dictionary<string, string> { ["UserName"] = "", ["Password"] = "" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DecodedBodyAsync(res);
        body.Should().Contain(ResxValue("Validation.RequiredFields"));
    }

    [Fact]
    public async Task Wizard_step1_with_blank_name_shows_localized_validation_summary()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/plans/create", "/admin/plans/create",
            new Dictionary<string, string> { ["Name"] = "", ["Type"] = "Emergency" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DecodedBodyAsync(res);
        body.Should().Contain(ResxValue("Validation.NameRequired"));
    }
}
