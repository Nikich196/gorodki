using Gorodki.Domain.Geo;
using Gorodki.Domain.Territory;
using NetTopologySuite.Geometries;
using static Gorodki.Domain.Tests.Geo.TestGeometry;

namespace Gorodki.Domain.Tests.Territory;

/// <summary>
/// Корпус патологических петель для шага A (PLAN.md, §7.3, «Тесты»): как бегают на самом деле
/// и как пытаются схитрить. Для каждого случая — ожидаемый итог и площадь.
/// </summary>
public sealed class CaptureShapeCorpusTests
{
    private static readonly CaptureShapeSettings Settings = new();
    private const double Closing = 40; // R по умолчанию

    private static CaptureShape Build(List<Coordinate> trail, Geometry? masks = null) =>
        CaptureShapeBuilder.Build(trail, Closing, masks, Settings);

    [Fact]
    public void Square_block_gives_exactly_its_area()
    {
        var shape = Build(Trail(Rectangle(0, 0, 100, 100)));

        Assert.True(shape.IsAccepted);
        Assert.InRange(shape.AreaSquareMeters, 9_990, 10_010);
        Assert.True(GeoOps.IsOnGrid(shape.Area));
    }

    [Fact]
    public void Bow_tie_gives_both_lobes()
    {
        var shape = Build(Trail([(0, 0), (150, 150), (150, 0), (0, 150)]));

        Assert.True(shape.IsAccepted);
        Assert.InRange(shape.AreaSquareMeters, 2 * 5_625 - 20, 2 * 5_625 + 20);
    }

    [Fact]
    public void Loops_in_opposite_directions_leave_no_hole()
    {
        // Сначала малый квадрат по часовой стрелке, потом большой вокруг него — против.
        // MakeValid сделал бы на месте малого квадрата дыру; у нас обведено всё.
        (double, double)[] path =
        [
            (0, 0), (0, 50), (50, 50), (50, 0), (0, 0),
            (-100, -100), (150, -100), (150, 150), (-100, 150), (-100, -100),
        ];
        var shape = Build(Trail(path));

        Assert.True(shape.IsAccepted);
        Assert.InRange(shape.AreaSquareMeters, 62_500 - 50, 62_500 + 50);
        Assert.Empty(((Polygon)shape.Area).Holes);
    }

    [Fact]
    public void Running_the_block_twice_counts_it_once()
    {
        // Второй круг на 2 м внутри первого: «рамка» между кругами тоже обведена.
        (double, double)[] path = [.. Rectangle(0, 0, 100, 100), (0, 0), .. Rectangle(2, 2, 96, 96)];
        var shape = Build(Trail(path));

        Assert.True(shape.IsAccepted);
        Assert.InRange(shape.AreaSquareMeters, 9_950, 10_010);
    }

    [Fact]
    public void Out_and_back_gives_nothing()
    {
        // 500 м туда и обратно по той же улице: обратный путь петляет вокруг прямого (GPS ±3 м).
        var trail = new List<Coordinate>();
        for (var x = 0.0; x <= 500; x += 5)
        {
            trail.Add(At(x, 0));
        }

        for (var x = 500.0; x >= 0; x -= 5)
        {
            trail.Add(At(x, 3 * Math.Sin(x / 20)));
        }

        var shape = Build(trail);

        Assert.False(shape.IsAccepted);
        Assert.Equal(CaptureRejection.Empty, shape.Rejection);
    }

    [Fact]
    public void Spur_outside_the_loop_adds_no_area()
    {
        // Квадрат 100 × 100 и отросток 150 м наружу и обратно от середины верхней стороны.
        var path = new List<(double, double)> { (0, 0), (100, 0), (100, 100), (50, 100) };
        for (var y = 105.0; y <= 250; y += 5)
        {
            path.Add((50, y));
        }

        for (var y = 250.0; y >= 100; y -= 5)
        {
            path.Add((50 + 3 * Math.Sin(y / 15), y));
        }

        path.Add((0, 100));
        var shape = Build(Trail(path));

        Assert.True(shape.IsAccepted);
        Assert.InRange(shape.AreaSquareMeters, 9_950, 10_150);
    }

    [Fact]
    public void Spur_inside_the_loop_does_not_cut_the_area()
    {
        // Отросток внутрь квадрата и обратно: площадь всё равно вся.
        var path = new List<(double, double)> { (0, 0), (100, 0), (100, 100), (50, 100) };
        for (var y = 95.0; y >= 20; y -= 5)
        {
            path.Add((50, y));
        }

        for (var y = 20.0; y <= 100; y += 5)
        {
            path.Add((50 + 3 * Math.Sin(y / 15), y));
        }

        path.Add((0, 100));
        var shape = Build(Trail(path));

        Assert.True(shape.IsAccepted);
        Assert.InRange(shape.AreaSquareMeters, 9_950, 10_050);
    }

    [Fact]
    public void Gps_spike_does_not_add_a_sliver()
    {
        // Одна точка «улетела» на 200 м вверх и вернулась.
        var trail = Trail(Rectangle(0, 0, 100, 100));
        var index = trail.FindIndex(c => Math.Abs(c.Y - (OriginY + 100)) < 0.01 && Math.Abs(c.X - (OriginX + 50)) < 0.01);
        trail[index] = At(50, 300);

        var shape = Build(trail);

        Assert.True(shape.IsAccepted);
        Assert.InRange(shape.AreaSquareMeters, 9_950, 10_050);
    }

    [Fact]
    public void Near_duplicate_points_change_nothing()
    {
        var clean = Build(Trail(Rectangle(0, 0, 100, 100)));
        var noisy = new List<Coordinate>();
        var random = new Random(7);
        foreach (var point in Trail(Rectangle(0, 0, 100, 100)))
        {
            for (var i = 0; i < 5; i++)
            {
                noisy.Add(new Coordinate(point.X + random.NextDouble() * 0.1 - 0.05, point.Y + random.NextDouble() * 0.1 - 0.05));
            }
        }

        var shape = Build(noisy);

        Assert.True(shape.IsAccepted);
        Assert.InRange(shape.AreaSquareMeters, clean.AreaSquareMeters - 20, clean.AreaSquareMeters + 20);
    }

    [Fact]
    public void Tiny_loop_is_too_small()
    {
        var shape = Build(Trail(Rectangle(0, 0, 30, 30)));

        Assert.Equal(CaptureRejection.TooSmall, shape.Rejection);
    }

    [Fact]
    public void Long_thin_loop_is_too_narrow()
    {
        // 400 × 15 м: площадь 6 000 м² больше минимума, но пятна радиусом 9 м нет.
        var shape = Build(Trail(Rectangle(0, 0, 400, 15)));

        Assert.Equal(CaptureRejection.TooNarrow, shape.Rejection);
    }

    [Fact]
    public void Huge_loop_is_rejected()
    {
        var shape = Build(Trail(Rectangle(0, 0, 2_000, 2_000), step: 20));

        Assert.Equal(CaptureRejection.TooLarge, shape.Rejection);
    }

    [Fact]
    public void Open_trail_is_not_a_loop()
    {
        var trail = Trail(Rectangle(0, 0, 100, 100));
        trail.RemoveRange(trail.Count - 16, 16); // конец в 80 м от старта

        Assert.Equal(CaptureRejection.NotClosed, Build(trail).Rejection);
    }

    [Fact]
    public void Masks_are_cut_out()
    {
        // «Река» шириной 20 м поперёк квадрата 200 × 200.
        var river = RectanglePolygon(-50, 90, 300, 20);

        var shape = Build(Trail(Rectangle(0, 0, 200, 200)), river);

        Assert.True(shape.IsAccepted);
        Assert.InRange(shape.AreaSquareMeters, 40_000 - 4_000 - 30, 40_000 - 4_000 + 30);
    }
}
