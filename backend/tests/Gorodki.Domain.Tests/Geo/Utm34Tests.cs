using Gorodki.Domain.Geo;

namespace Gorodki.Domain.Tests.Geo;

public sealed class Utm34Tests
{
    /// <summary>
    /// Эталоны посчитаны независимо библиотекой PROJ 9.8.1 (pyproj 3.8.0): EPSG:4326 → EPSG:32634.
    /// Точки — в Бресте и вокруг, плюс крайние случаи: пересечение осевого меридиана с экватором
    /// и точка в 6° от осевого меридиана (там ошибка рядов больше всего).
    /// </summary>
    [Theory]
    [InlineData(52.0930, 23.7314, 687105.9496, 5774902.0559)]
    [InlineData(52.0830, 23.6560, 681982.5253, 5773598.4011)]
    [InlineData(52.0976, 23.6880, 684114.5597, 5775302.5525)]
    [InlineData(52.1100, 23.8300, 693785.1757, 5777051.1033)]
    [InlineData(0.0, 21.0, 500000.0000, 0.0000)]
    [InlineData(55.8, 27.0, 875883.3442, 6200122.8124)]
    public void Forward_matches_PROJ_within_a_millimetre(double latitude, double longitude, double easting, double northing)
    {
        var (e, n) = Utm34.Forward(latitude, longitude);

        Assert.InRange(e - easting, -0.001, 0.001);
        Assert.InRange(n - northing, -0.001, 0.001);
    }

    [Theory]
    [InlineData(684114.5597, 5775302.5525)]
    [InlineData(690000.0, 5780000.0)]
    [InlineData(875883.3442, 6200122.8124)]
    public void Inverse_then_forward_returns_to_the_same_point(double easting, double northing)
    {
        var (latitude, longitude) = Utm34.Inverse(easting, northing);
        var (e, n) = Utm34.Forward(latitude, longitude);

        Assert.InRange(e - easting, -0.001, 0.001);
        Assert.InRange(n - northing, -0.001, 0.001);
    }

    [Fact]
    public void Inverse_matches_PROJ()
    {
        var (latitude, longitude) = Utm34.Inverse(684114.5597, 5775302.5525);

        // 1e-8° ≈ 1 мм.
        Assert.InRange(latitude - 52.0976, -1e-8, 1e-8);
        Assert.InRange(longitude - 23.6880, -1e-8, 1e-8);
    }
}
