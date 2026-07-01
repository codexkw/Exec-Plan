using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ExecPlan.IntegrationTests.Web;

public class StaticAssetsTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public StaticAssetsTests(TestAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/css/execplan.css")]
    [InlineData("/lib/bootstrap/bootstrap.rtl.min.css")]
    [InlineData("/lib/bootstrap/bootstrap.min.css")]
    [InlineData("/lib/bootstrap/bootstrap.bundle.min.js")]
    [InlineData("/lib/signalr/signalr.min.js")]
    [InlineData("/fonts/IBMPlexSansArabic-Regular.woff2")]
    public async Task Static_asset_is_served_locally(string path)
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
