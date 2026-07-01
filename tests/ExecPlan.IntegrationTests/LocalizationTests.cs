using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Proves the host-level ar/en request-localization wiring (CLAUDE.md convention 7) end to end over
/// real HTTP: the default culture is Arabic, and the <c>POST api/v1/set-language</c> cookie switches a
/// subsequent request to English. The cookie provider is registered first in
/// <c>RequestLocalizationOptions</c>, so the captured <c>.AspNetCore.Culture</c> cookie alone flips the
/// resolved culture.
/// </summary>
[Collection("WebHostSequential")]
public class LocalizationTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public LocalizationTests(TestAppFactory factory) => _factory = factory;

    private sealed record CultureResponse(string Culture);

    [Fact]
    public async Task Default_culture_is_arabic_with_no_cookie()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/culture");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CultureResponse>();
        body!.Culture.Should().StartWith("ar");
    }

    [Fact]
    public async Task Set_language_cookie_switches_culture_to_en()
    {
        // CreateClient with auto-redirect off so we read the raw Set-Cookie ourselves.
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var set = await client.PostAsync("/api/v1/set-language?culture=en", content: null);
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        set.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();

        var cultureCookie = cookies!.First(c => c.StartsWith(".AspNetCore.Culture"));
        // Send just the cookie pair (name=value) back on the follow-up request.
        var cookiePair = cultureCookie.Split(';')[0];

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/culture");
        request.Headers.Add("Cookie", cookiePair);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CultureResponse>();
        body!.Culture.Should().Be("en");
    }
}
