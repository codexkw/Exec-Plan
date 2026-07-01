using System.Text.Json;
using ExecPlan.Application.Common;

namespace ExecPlan.Api.Middleware;

/// <summary>
/// The single consistent <see cref="AppException"/> → HTTP mapper for the whole host, content-negotiated
/// (Task 4) between the two surfaces it fronts:
/// <list type="bullet">
/// <item><description><c>/api/*</c> requests, or any request whose <c>Accept</c> header asks for JSON
/// (and not HTML) — unchanged from Phase 1: a small JSON body
/// <c>{ "error": &lt;message&gt;, "kind": &lt;kind&gt; }</c> with the matching status code
/// (NotFound→404, Forbidden→403, Unauthorized→401, Conflict→409, Validation→400; any other unhandled
/// exception → 500 generic message, detail logged, never leaked to the client).</description></item>
/// <item><description>Everything else (the MVC admin's HTML pages) — redirected to a themed page by
/// <see cref="AppException.ErrorKind"/>: Unauthorized→login (with a <c>returnUrl</c> back to the page
/// that failed), Forbidden→the existing <c>Account.Denied</c> view (Task 3), NotFound→a shared NotFound
/// view, Validation/Conflict→a shared generic Error view. An unhandled non-<see cref="AppException"/> on
/// an HTML request redirects to the same generic Error page with no message (so a stack trace/internal
/// detail is never rendered to the browser) — the JSON 500 body for that case is unchanged.</description></item>
/// </list>
/// Registered early in the pipeline (before authentication) so it wraps every downstream handler.
/// </summary>
public sealed class AppExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AppExceptionMiddleware> _logger;

    public AppExceptionMiddleware(RequestDelegate next, ILogger<AppExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            if (WantsJson(context))
            {
                var status = ex.ErrorKind switch
                {
                    AppException.Kind.NotFound => StatusCodes.Status404NotFound,
                    AppException.Kind.Forbidden => StatusCodes.Status403Forbidden,
                    AppException.Kind.Unauthorized => StatusCodes.Status401Unauthorized,
                    AppException.Kind.Conflict => StatusCodes.Status409Conflict,
                    AppException.Kind.Validation => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError,
                };
                await WriteAsync(context, status, ex.Message, ex.ErrorKind.ToString());
                return;
            }

            RedirectHtml(context, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);

            if (WantsJson(context))
            {
                await WriteAsync(context, StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "Internal");
                return;
            }

            // HTML surface: never leak the exception message/stack trace — redirect to the generic
            // error page with no msg query param (unlike the AppException Validation/Conflict branch,
            // which is safe to surface because it originates from a deliberately-thrown, user-facing
            // AppException message rather than an arbitrary unhandled exception).
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.Redirect("/admin/error");
            }
        }
    }

    /// <summary>
    /// True when the caller wants a JSON error body: every <c>/api/*</c> route (Task 1-21's REST
    /// surface), or any request whose <c>Accept</c> header names <c>application/json</c> without also
    /// naming <c>text/html</c> (a browser navigation's <c>Accept</c> always includes <c>text/html</c>,
    /// so this correctly falls through to the HTML branch for real browser requests even against a
    /// non-/api path that happens to also accept JSON).
    /// </summary>
    private static bool WantsJson(HttpContext ctx)
        => ctx.Request.Path.StartsWithSegments("/api")
           || (ctx.Request.Headers.Accept.ToString() is var a
               && a.Contains("application/json") && !a.Contains("text/html"));

    /// <summary>Redirects an HTML request to the themed page matching <paramref name="ex"/>'s <see cref="AppException.ErrorKind"/>.</summary>
    private static void RedirectHtml(HttpContext context, AppException ex)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        switch (ex.ErrorKind)
        {
            case AppException.Kind.Unauthorized:
                context.Response.Redirect($"/admin/login?returnUrl={Uri.EscapeDataString(context.Request.Path)}");
                break;
            case AppException.Kind.Forbidden:
                context.Response.Redirect("/admin/denied");
                break;
            case AppException.Kind.NotFound:
                context.Response.Redirect("/admin/notfound");
                break;
            default: // Validation / Conflict
                context.Response.Redirect($"/admin/error?msg={Uri.EscapeDataString(ex.Message)}");
                break;
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string error, string kind)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error, kind }));
    }
}
