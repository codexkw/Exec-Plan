namespace ExecPlan.Application.Common;

/// <summary>
/// Application-level error with a classification (<see cref="Kind"/>) that hosts (MVC admin, REST API)
/// map to the appropriate status code / view. Construct via the static factory helpers.
/// </summary>
public sealed class AppException : Exception
{
    public enum Kind
    {
        NotFound,
        Forbidden,
        Unauthorized,
        Conflict,
        Validation,
    }

    public Kind ErrorKind { get; }

    /// <summary>
    /// Optional stable, machine-readable error code (e.g. <c>"NoOneOnDuty"</c>) the HTML admin surface
    /// maps to a localized <c>AppError.&lt;Code&gt;</c> resx message instead of rendering the raw (English)
    /// <see cref="Exception.Message"/>. Null for exceptions that were never given one — those fall back to
    /// <c>AppError.Generic</c>. The <c>/api</c> JSON surface keeps using <see cref="Exception.Message"/>.
    /// </summary>
    public string? Code { get; }

    private AppException(Kind kind, string message, string? code) : base(message)
    {
        ErrorKind = kind;
        Code = code;
    }

    public static AppException NotFound(string message = "The requested resource was not found.", string? code = null) => new(Kind.NotFound, message, code);

    public static AppException Forbidden(string message = "You are not allowed to perform this action.", string? code = null) => new(Kind.Forbidden, message, code);

    public static AppException Unauthorized(string message = "Invalid credentials or token.", string? code = null) => new(Kind.Unauthorized, message, code);

    public static AppException Conflict(string message = "The request conflicts with the current state.", string? code = null) => new(Kind.Conflict, message, code);

    public static AppException Validation(string message = "The request is invalid.", string? code = null) => new(Kind.Validation, message, code);
}
