using ExecPlan.Application.Common;
using FluentAssertions;

namespace ExecPlan.UnitTests.Common;

public class AppExceptionTests
{
    [Fact]
    public void NotFound_sets_NotFound_kind()
    {
        AppException.NotFound("missing").ErrorKind.Should().Be(AppException.Kind.NotFound);
    }

    [Fact]
    public void Forbidden_sets_Forbidden_kind()
    {
        AppException.Forbidden("nope").ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public void Unauthorized_sets_Unauthorized_kind()
    {
        AppException.Unauthorized("bad creds").ErrorKind.Should().Be(AppException.Kind.Unauthorized);
    }

    [Fact]
    public void Conflict_sets_Conflict_kind()
    {
        AppException.Conflict("conflict").ErrorKind.Should().Be(AppException.Kind.Conflict);
    }

    [Fact]
    public void Validation_sets_Validation_kind()
    {
        AppException.Validation("invalid").ErrorKind.Should().Be(AppException.Kind.Validation);
    }

    [Fact]
    public void Factory_message_is_preserved_on_the_exception()
    {
        AppException.NotFound("custom message").Message.Should().Be("custom message");
    }
}
