namespace Gorodki.Domain.Geo;

/// <summary>
/// Перевод координат GPS (WGS 84, градусы) в метры проекции UTM, зона 34N (EPSG:32634), и обратно.
/// </summary>
/// <remarks>
/// Брест (≈23,7° в. д.) лежит в зоне 34 (18°–24° в. д.), поэтому вся геометрия участков хранится в ней:
/// в метрах, где площадь и расстояния считаются обычными формулами на плоскости.
/// Искажение масштаба по городу — около 0,002 %, заметно меньше шума GPS.
///
/// Формулы Крюгера в форме Карни (C. F. F. Karney, «Transverse Mercator with an accuracy of a few
/// nanometers», 2011) с рядами до n³: на расстоянии до 6° от осевого меридиана ошибка меньше миллиметра.
/// Проверено по библиотеке PROJ (тесты Utm34Tests).
/// </remarks>
public static class Utm34
{
    /// <summary>Код системы координат EPSG.</summary>
    public const int Srid = 32634;

    private const double SemiMajorAxis = 6_378_137.0; // a, WGS 84
    private const double Flattening = 1 / 298.257_223_563; // f, WGS 84
    private const double ScaleFactor = 0.9996; // k0 для всех зон UTM
    private const double FalseEasting = 500_000.0; // сдвиг на восток, чтобы координаты были положительными
    private const double CentralMeridianDegrees = 21.0; // осевой меридиан зоны 34

    private static readonly double CentralMeridian = CentralMeridianDegrees * Math.PI / 180;

    // Третий сплющённый параметр n и производные от него величины.
    private static readonly double N = Flattening / (2 - Flattening);
    private static readonly double Eccentricity = 2 * Math.Sqrt(N) / (1 + N);
    private static readonly double RectifyingRadius =
        SemiMajorAxis / (1 + N) * (1 + N * N / 4 + N * N * N * N / 64);

    private static readonly double[] Alpha =
    [
        N / 2 - 2 * N * N / 3 + 5 * N * N * N / 16,
        13 * N * N / 48 - 3 * N * N * N / 5,
        61 * N * N * N / 240,
    ];

    private static readonly double[] Beta =
    [
        N / 2 - 2 * N * N / 3 + 37 * N * N * N / 96,
        N * N / 48 + N * N * N / 15,
        17 * N * N * N / 480,
    ];

    private static readonly double[] Delta =
    [
        2 * N - 2 * N * N / 3 - 2 * N * N * N,
        7 * N * N / 3 - 8 * N * N * N / 5,
        56 * N * N * N / 15,
    ];

    /// <summary>Широта и долгота (градусы) → восток и север (метры).</summary>
    public static (double Easting, double Northing) Forward(double latitude, double longitude)
    {
        var phi = latitude * Math.PI / 180;
        var deltaLambda = longitude * Math.PI / 180 - CentralMeridian;

        var sinPhi = Math.Sin(phi);
        // Конформная широта через t = tg χ.
        var t = Math.Sinh(Math.Atanh(sinPhi) - Eccentricity * Math.Atanh(Eccentricity * sinPhi));
        var xiPrime = Math.Atan2(t, Math.Cos(deltaLambda));
        var etaPrime = Math.Atanh(Math.Sin(deltaLambda) / Math.Sqrt(1 + t * t));

        var xi = xiPrime;
        var eta = etaPrime;
        for (var j = 1; j <= Alpha.Length; j++)
        {
            xi += Alpha[j - 1] * Math.Sin(2 * j * xiPrime) * Math.Cosh(2 * j * etaPrime);
            eta += Alpha[j - 1] * Math.Cos(2 * j * xiPrime) * Math.Sinh(2 * j * etaPrime);
        }

        return (FalseEasting + ScaleFactor * RectifyingRadius * eta, ScaleFactor * RectifyingRadius * xi);
    }

    /// <summary>Восток и север (метры) → широта и долгота (градусы).</summary>
    public static (double Latitude, double Longitude) Inverse(double easting, double northing)
    {
        var xi = northing / (ScaleFactor * RectifyingRadius);
        var eta = (easting - FalseEasting) / (ScaleFactor * RectifyingRadius);

        var xiPrime = xi;
        var etaPrime = eta;
        for (var j = 1; j <= Beta.Length; j++)
        {
            xiPrime -= Beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            etaPrime -= Beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }

        var chi = Math.Asin(Math.Sin(xiPrime) / Math.Cosh(etaPrime));
        var phi = chi;
        for (var j = 1; j <= Delta.Length; j++)
        {
            phi += Delta[j - 1] * Math.Sin(2 * j * chi);
        }

        var lambda = CentralMeridian + Math.Atan2(Math.Sinh(etaPrime), Math.Cos(xiPrime));
        return (phi * 180 / Math.PI, lambda * 180 / Math.PI);
    }
}
