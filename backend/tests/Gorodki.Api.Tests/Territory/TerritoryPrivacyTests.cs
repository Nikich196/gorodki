using Gorodki.Api.Features.Territory;
using Gorodki.Domain.Geo;

namespace Gorodki.Api.Tests.Territory;

/// <summary>Публичная проекция карты (PLAN.md, §3.16): граница раскрытия и номера кусков от содержимого.</summary>
public sealed class TerritoryPrivacyTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(20);

    [Theory]
    [InlineData("2026-11-16T12:24:59Z", "2026-11-16T12:00:00Z")]
    [InlineData("2026-11-16T12:25:00Z", "2026-11-16T12:05:00Z")]
    [InlineData("2026-11-16T12:29:59.999Z", "2026-11-16T12:05:00Z")]
    public void Changes_become_public_in_five_minute_batches(string now, string horizon)
    {
        // Захват в 12:01 и захват в 12:04 раскрываются вместе, в 12:25: минута захвата не видна.
        Assert.Equal(DateTimeOffset.Parse(horizon), TerritoryReader.PublicHorizon(DateTimeOffset.Parse(now), Delay));
    }

    [Fact]
    public void Same_content_gives_the_same_id_and_any_visible_change_a_new_one()
    {
        var tile = new TileKey(684, 5775);
        var piece = new ParcelView(
            0, Guid.Parse("00000000-0000-0000-0000-00000000000a"), 3, 1, false, 1_790_000_000_000, null, null,
            [52.0976, 23.688, 52.0976, 23.6895, 52.0985, 23.6895, 52.0976, 23.688], []);

        var id = TerritoryReader.ContentId(tile, piece);

        Assert.Equal(id, TerritoryReader.ContentId(tile, piece with { Id = 99 })); // сам номер в нём не участвует
        Assert.NotEqual(id, TerritoryReader.ContentId(tile, piece with { Level = 2 }));
        Assert.NotEqual(id, TerritoryReader.ContentId(tile, piece with { ShieldUntilMs = 1_790_043_200_000 }));
        Assert.NotEqual(id, TerritoryReader.ContentId(new TileKey(684, 5776), piece));
        Assert.InRange(id, 0, (1L << 53) - 1); // точно представим в JavaScript
    }
}
