using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Handles the <c>_Layout</c> language-toggle form (CLAUDE.md convention 7: ar/en, ar default/RTL).
/// <c>[AllowAnonymous]</c> so the toggle works on the login page too, before any admin session exists.
/// Whitelists <c>culture</c> to <c>ar</c>/<c>en</c> (anything else falls back to <c>ar</c>) and writes
/// the standard <see cref="CookieRequestCultureProvider"/> cookie, which is registered first in
/// <c>Program.cs</c>'s <c>RequestLocalizationOptions.RequestCultureProviders</c> so it alone determines
/// the resolved culture on every subsequent request.
/// </summary>
[Area("Admin")]
[AllowAnonymous]
[Route("admin")]
public sealed class LanguageController : Controller
{
    [HttpPost("language")]
    [ValidateAntiForgeryToken]
    public IActionResult Set(string culture, string? returnUrl)
    {
        var allowed = culture is "ar" or "en" ? culture : "ar";
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(allowed)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = false, IsEssential = true });
        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/admin/login");
    }
}
