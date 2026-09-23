using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Tests.Runs;

public sealed class ClaimReadinessTests
{
    [Theory]
    [InlineData(120, 99, 5_000, null, false, false, ClaimWait.Points)] // конец петли дальше непрерывного начала
    [InlineData(120, 179, 5_000, null, false, false, ClaimWait.Points)] // кусок с концом петли не найден
    [InlineData(120, 179, 5_000, 6_000L, false, false, ClaimWait.Sensors)] // датчики переданы не до конца петли
    [InlineData(120, 179, 5_000, 6_000L, true, false, ClaimWait.Nothing)] // забег завершён, все точки есть — больше ничего не придёт
    [InlineData(120, 179, 6_000, 6_000L, false, true, ClaimWait.PreviousClaim)]
    [InlineData(120, 179, 6_000, 6_000L, false, false, ClaimWait.Nothing)]
    public void Claim_waits_for_points_then_sensors_then_its_turn(
        int endSeq, int prefixEnd, long sensorsMs, long? endChunkLastPointMs, bool complete, bool earlierWaiting, ClaimWait expected)
    {
        Assert.Equal(expected, ClaimReadiness.Check(endSeq, prefixEnd, sensorsMs, endChunkLastPointMs, complete, earlierWaiting));
    }

    [Fact]
    public void Codes_for_the_phone_are_stable()
    {
        Assert.Equal(
            new[] { "points", "sensors", "previous_claim", "queue" },
            new[] { ClaimWait.Points, ClaimWait.Sensors, ClaimWait.PreviousClaim, ClaimWait.Nothing }.Select(ClaimReadiness.Code));
    }
}
