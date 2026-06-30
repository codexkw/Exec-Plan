using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>
/// Host-level ar/en localization control (CLAUDE.md convention 7 — Arabic-first, default <c>ar</c>/RTL,
/// with <c>en</c>/LTR). <see cref="SetLanguage"/> writes the standard <c>.AspNetCore.Culture</c> cookie
/// so subsequent requests resolve via the cookie provider (registered first in
/// <c>RequestLocalizationOptions</c>); <see cref="Culture"/> is an anonymous diagnostic echoing the
/// culture the request pipeline actually resolved, so the wiring is testable end to end. The admin
/// Razor views that consume this arrive in the web increment.
/// </summary>
[ApiController]
[Route("api/v1")]
public sealed class LocalizationController : ControllerBase
{
    private static readonly string[] Supported = ["ar", "en"];

    public sealed record CultureResponse(string Culture);

    /// <summary>Persist the UI culture in the <c>.AspNetCore.Culture</c> cookie. Unsupported values 400.</summary>
    [HttpPost("set-language")]
    [AllowAnonymous]
    public IActionResult SetLanguage([FromQuery] string culture)
    {
        if (string.IsNullOrWhiteSpace(culture) || !Supported.Contains(culture))
        {
            return BadRequest(new { error = "Unsupported culture.", kind = "Validation" });
        }

        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Path = "/", HttpOnly = false });

        return Ok();
    }

    /// <summary>Diagnostic: the culture the request-localization pipeline resolved for this request.</summary>
    [HttpGet("culture")]
    [AllowAnonymous]
    public ActionResult<CultureResponse> Culture() =>
        Ok(new CultureResponse(CultureInfo.CurrentCulture.Name));
}
