using ExecPlan.Application.Shifts;
using ExecPlan.Domain.Enums;
using FluentAssertions;

namespace ExecPlan.UnitTests.Shifts;

public class KuwaitShiftCalculatorTests
{
    private static DateTime KwtToUtc(int y, int mo, int d, int h, int mi)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait");
        return TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), tz);
    }

    private readonly KuwaitShiftCalculator _c = new();

    [Fact]
    public void Morning_0900_is_Morning_today()
    {
        var r = _c.Resolve(KwtToUtc(2026, 6, 30, 9, 0));
        r.Band.Should().Be(ShiftBand.Morning);
        r.RosterDate.Should().Be(new DateTime(2026, 6, 30));
    }

    [Fact]
    public void Evening_1500_is_Evening_today()
    {
        var r = _c.Resolve(KwtToUtc(2026, 6, 30, 15, 0));
        r.Band.Should().Be(ShiftBand.Evening);
        r.RosterDate.Should().Be(new DateTime(2026, 6, 30));
    }

    [Fact]
    public void Night_2300_is_Night_today()
    {
        var r = _c.Resolve(KwtToUtc(2026, 6, 30, 23, 0));
        r.Band.Should().Be(ShiftBand.Night);
        r.RosterDate.Should().Be(new DateTime(2026, 6, 30));
    }

    [Fact]
    public void Night_0200_belongs_to_prev_day()
    {
        var r = _c.Resolve(KwtToUtc(2026, 7, 1, 2, 0));
        r.Band.Should().Be(ShiftBand.Night);
        r.RosterDate.Should().Be(new DateTime(2026, 6, 30));
    }

    [Fact]
    public void Boundary_0600_is_Morning()
    {
        _c.Resolve(KwtToUtc(2026, 6, 30, 6, 0)).Band.Should().Be(ShiftBand.Morning);
    }

    [Fact]
    public void Boundary_1400_is_Evening()
    {
        _c.Resolve(KwtToUtc(2026, 6, 30, 14, 0)).Band.Should().Be(ShiftBand.Evening);
    }

    [Fact]
    public void Boundary_2200_is_Night()
    {
        _c.Resolve(KwtToUtc(2026, 6, 30, 22, 0)).Band.Should().Be(ShiftBand.Night);
    }
}
