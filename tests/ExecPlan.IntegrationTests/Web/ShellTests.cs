using System.Net;
using FluentAssertions;
using Xunit;

namespace ExecPlan.IntegrationTests.Web;

[Collection("WebHostSequential")]
public class ShellTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ShellTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Root_redirects_to_admin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var res = await client.GetAsync("/");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin");
    }

    [Fact]
    public async Task Login_page_renders_rtl_arabic_by_default()
    {
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/admin/login");
        html.Should().Contain("dir=\"rtl\"").And.Contain("lang=\"ar\"");
        html.Should().Contain("execplan.css").And.Contain("bootstrap.rtl.min.css");
    }

    [Fact]
    public async Task Admin_root_requires_auth_redirects_to_login()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var res = await client.GetAsync("/admin");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("/admin/login");
    }
}
