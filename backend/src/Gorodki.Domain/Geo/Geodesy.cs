namespace Gorodki.Domain.Geo;

/// <summary>
/// Расстояния на поверхности Земли — та же формула и тот же порядок действий, что в <c>Geodesy</c> из GameCore (Swift):
/// судья отрезков на сервере должен получить те же числа, что телефон.
/// </summary>
public static class Geodesy
{
    /// <summary>Средний радиус Земли по IUGG, метры.</summary>
    public const double MeanEarthRadius = 6_371_008.8;

    /// <summary>Расстояние по дуге большого круга (формула гаверсинусов), метры.</summary>
    public static double Distance(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var lat1 = Radians(latitude1);
        var lat2 = Radians(latitude2);
        var deltaLat = Radians(latitude2 - latitude1);
        var deltaLon = Radians(longitude2 - longitude1);

        var h =
            (Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2))
            + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2));
        return 2 * MeanEarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
