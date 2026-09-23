namespace Gorodki.Domain.Fog;

/// <summary>Клетка тумана — пиксель веб-меркатора уровня 22 (PLAN.md, §3.10, «сетка G22»); в Бресте ≈5,87 м.</summary>
public readonly record struct FogCell(int X, int Y)
{
    /// <summary>Тайл уровня 14, в котором лежит клетка.</summary>
    public FogTileKey Tile => new(X >> 8, Y >> 8);

    /// <summary>Номер бита внутри тайла: строка × 256 + столбец.</summary>
    public int BitIndex => ((Y & 255) * 256) + (X & 255);
}

/// <summary>Тайл веб-меркатора уровня 14 (~1,5 × 1,5 км в Бресте): единица хранения тумана.</summary>
public readonly record struct FogTileKey(int X, int Y) : IComparable<FogTileKey>
{
    public int CompareTo(FogTileKey other) => X != other.X ? X.CompareTo(other.X) : Y.CompareTo(other.Y);
}

/// <summary>
/// Сетка тумана — перенос <c>FogGrid</c> из GameCore (Swift) с тем же порядком действий: клетки на сервере и телефоне
/// совпадают бит в бит (эталоны <c>contracts/fog.v1.json</c>).
/// </summary>
public static class FogGrid
{
    public const int Zoom = 22;

    public const int CellsPerTileSide = 256;

    private const double WorldCells = 1 << Zoom;

    /// <summary>Длина экватора в веб-меркаторе (радиус 6 378 137 м), метры.</summary>
    private const double EquatorMeters = 40_075_016.685_578_49;

    /// <summary>Дробные координаты пикселя уровня 22 (x — на восток, y — на юг).</summary>
    public static (double X, double Y) Pixel(double latitude, double longitude)
    {
        var lat = Radians(Math.Min(Math.Max(latitude, -85.051_128_78), 85.051_128_78));
        var x = (longitude + 180) / 360 * WorldCells;
        var y = (1 - (Math.Log(Math.Tan(lat) + (1 / Math.Cos(lat))) / Math.PI)) / 2 * WorldCells;
        return (x, y);
    }

    public static FogCell Cell(double latitude, double longitude)
    {
        var (x, y) = Pixel(latitude, longitude);
        return new FogCell((int)Math.Floor(x), (int)Math.Floor(y));
    }

    /// <summary>Центр клетки в градусах.</summary>
    public static (double Latitude, double Longitude) Center(FogCell cell)
    {
        var x = (cell.X + 0.5) / WorldCells;
        var y = (cell.Y + 0.5) / WorldCells;
        var latitude = Degrees(Math.Atan(Math.Sinh(Math.PI * (1 - (2 * y)))));
        return (latitude, (x * 360) - 180);
    }

    /// <summary>Сторона клетки на данной широте, метры (меркатор сохраняет форму: клетка — квадрат).</summary>
    public static double CellSizeMeters(double latitude) => EquatorMeters / WorldCells * Math.Cos(Radians(latitude));

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static double Degrees(double radians) => radians * 180 / Math.PI;
}
