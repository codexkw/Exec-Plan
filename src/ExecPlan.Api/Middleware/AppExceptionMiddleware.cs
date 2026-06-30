using System.Text.Json;
using ExecPlan.Application.Common;

namespace ExecPlan.Api.Middleware;

/// <summary>
/// The single consistent <see cref="AppException"/> → HTTP mapper for the REST surface. Catches the
/// classified application errors thrown by the Application services and writes a small JSON body
/// <c>{ "error": &lt;message&gt;, "kind": &lt;kind&gt; }</c> with the matching status code
/// (NotFound→404, Forbidden→403, Unauthorized→401, Conflict→409, Validation→400). Any other unhandled
/// exception becomes a 500 with a generic message (the detail is logged, never leaked to the client).
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "Internal");
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
