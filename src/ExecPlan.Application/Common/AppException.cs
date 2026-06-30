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

    private AppException(Kind kind, string message) : base(message) => ErrorKind = kind;

    public static AppException NotFound(string message = "The requested resource was not found.") => new(Kind.NotFound, message);

    public static AppException Forbidden(string message = "You are not allowed to perform this action.") => new(Kind.Forbidden, message);

    public static AppException Unauthorized(string message = "Invalid credentials or token.") => new(Kind.Unauthorized, message);

    public static AppException Conflict(string message = "The request conflicts with the current state.") => new(Kind.Conflict, message);

    public static AppException Validation(string message = "The request is invalid.") => new(Kind.Validation, message);
}
