using ExecPlan.Domain.Common;
using FluentAssertions;
using Xunit;

namespace ExecPlan.UnitTests.Domain;

public class BaseEntityTests
{
    private sealed class Sample : BaseEntity { }

    [Fact]
    public void New_entity_gets_nonempty_guid_id() => new Sample().Id.Should().NotBe(Guid.Empty);

    [Fact]
    public void Two_entities_get_distinct_ids()
    {
        var a = new Sample();
        var b = new Sample();
        a.Id.Should().NotBe(b.Id);
    }
}
