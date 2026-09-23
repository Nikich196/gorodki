using Gorodki.Api.Features.Territory;
using Gorodki.Domain.Geo;

namespace Gorodki.Api.Tests.Territory;

public sealed class TileQueryTests
{
    [Fact]
    public void Tiles_with_and_without_known_versions_are_read()
    {
        var tiles = TerritoryEndpoints.ParseTiles("684:5775, 685:5775@3,684:5775");

        Assert.NotNull(tiles);
        Assert.Equal(new (TileKey, long?)[] { (new(684, 5775), null), (new(685, 5775), 3) }, tiles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("684")]
    [InlineData("684:x")]
    [InlineData("684:5775@-1")]
    [InlineData("684:5775@1@2")]
    public void Broken_tile_list_is_refused(string? text)
    {
        Assert.Null(TerritoryEndpoints.ParseTiles(text));
    }

    [Fact]
    public void More_than_25_tiles_is_refused()
    {
        var text = string.Join(',', Enumerable.Range(0, 26).Select(i => $"{684 + i}:5775"));

        Assert.Null(TerritoryEndpoints.ParseTiles(text));
    }
}
