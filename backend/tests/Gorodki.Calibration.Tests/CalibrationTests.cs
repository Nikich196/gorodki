using System.Text.Json;
using Gorodki.Domain.Runs;

namespace Gorodki.Calibration.Tests;

/// <summary>
/// Половина сервера на синтетических следах. Ожидания — из кода шага A (<c>CaptureShapeBuilder</c>: сначала площадь,
/// потом ширина) и кольца петли (<c>LoopRing</c>: концы proximity-петли не дальше R), а не подобраны под результат.
/// </summary>
public sealed class CalibrationTests
{
    private static readonly ShapeVariant Current = ShapeVariant.Current;

    private static LoopEvaluation Evaluate(ReplayRun run, params ShapeVariant[] shapes) =>
        Assert.Single(Calibration.Evaluate(run, newcomer: false, shapes).Loops);

    [Fact]
    public void Current_numbers_are_the_ones_from_the_code()
    {
        Assert.Equal(new ShapeVariant(2_500, 9), Current);
    }

    [Fact]
    public void Square_60_m_with_5_m_noise_is_applied_and_the_grid_changes_the_verdict()
    {
        var points = Synthetic.Walk(Synthetic.Square, noiseLimit: 5);
        var end = Synthetic.Nearest(points, 0, 0, from: points.Count / 2);

        var loop = Evaluate(Synthetic.Run(points, Synthetic.Loop(0, end)), Current, new(4_000, 9), new(2_500, 40));

        Assert.Null(loop.RingRejectCode);
        Assert.Equal(["applied", "too_small", "too_narrow"], loop.Outcomes);
        Assert.InRange(loop.AreaSquareMeters!.Value, 3_000, 4_200);
        Assert.InRange(loop.HalfWidthMeters!.Value, 24, 36);
    }

    [Fact]
    public void Whole_strip_12_m_wide_is_too_narrow_although_it_is_large_enough()
    {
        var points = Synthetic.Walk(Synthetic.Strip, noiseLimit: 1);
        var end = Synthetic.Nearest(points, 0, 12, from: points.Count / 2);

        var loop = Evaluate(Synthetic.Run(points, Synthetic.Loop(0, end)), Current, new(2_500, 5), new(4_000, 5));

        Assert.Equal(["too_narrow", "applied", "too_small"], loop.Outcomes);
        Assert.InRange(loop.AreaSquareMeters!.Value, 3_300, 3_900);
        Assert.InRange(loop.HalfWidthMeters!.Value, 5, 7);
    }

    [Fact]
    public void Strip_loop_closed_as_early_as_the_phone_closes_it_is_too_small_first()
    {
        // Телефон при R = 20 м замыкает полосу 12 м, едва оценка площади дойдёт до 1 000 м² (тест LoopReplay):
        // начало на ≈ 16 м раньше конца по другой стороне.
        var points = Synthetic.Walk(Synthetic.Strip, noiseLimit: 1);
        var start = Synthetic.Nearest(points, 208, 0);
        var end = Synthetic.Nearest(points, 224, 12, from: points.Count / 2);

        var loop = Evaluate(Synthetic.Run(points, Synthetic.Loop(start, end)), Current, new(500, 9), new(500, 5));

        Assert.Equal(["too_small", "too_narrow", "applied"], loop.Outcomes);
        Assert.InRange(loop.AreaSquareMeters!.Value, 900, 1_300);
    }

    [Fact]
    public void Open_arc_claimed_anyway_is_not_closed_and_has_no_contour()
    {
        var points = Synthetic.Walk(Synthetic.Arc, noiseLimit: 1);

        var loop = Evaluate(Synthetic.Run(points, Synthetic.Loop(0, points.Count - 1)), Current, new(500, 5));

        Assert.Equal("not_closed", loop.RingRejectCode);
        Assert.Equal(["not_closed", "not_closed"], loop.Outcomes);
        Assert.Null(loop.Contour);
    }

    [Fact]
    public void Report_has_the_grid_the_loops_and_one_picture_per_loop_without_external_files()
    {
        var square = Synthetic.Walk(Synthetic.Square, noiseLimit: 5);
        var arc = Synthetic.Walk(Synthetic.Arc, noiseLimit: 1);
        var squareLoop = Synthetic.Loop(0, Synthetic.Nearest(square, 0, 0, from: square.Count / 2));
        var file = new ReplayFile("synthetic.json", false, [
            Synthetic.Run(square, squareLoop) with { Variants = [new(new(), [squareLoop]), new(new() { MinRadiusMeters = 25 }, [squareLoop])] },
            Synthetic.Run(arc, Synthetic.Loop(0, arc.Count - 1)) with { Id = "arc", Variants = [new(new(), [Synthetic.Loop(0, arc.Count - 1)]), new(new() { MinRadiusMeters = 25 }, [])] },
        ]);
        var options = Options.Parse(["loops.json", "--min-area", "2500,4000", "--min-half-width", "9"]);

        var runs = Calibration.Evaluate(file, options.Shapes);
        var markdown = Report.Markdown(file, runs, options);
        var html = Report.Html(file, runs, options);

        const string nb = " "; // разряды — неразрывным пробелом
        Assert.Contains($"| R 20–50 (×1,62), путь ≥ 150, оценка ≥ 1{nb}000 | 2{nb}500 | 9 | 2 | 1 | 0 | 0 | not_closed 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains($"| R 25–50 (×1,62), путь ≥ 150, оценка ≥ 1{nb}000 | 4{nb}000 | 9 | 1 | 0 | 1 | 0 |  |", markdown, StringComparison.Ordinal);
        Assert.Contains($"applied: 2{nb}500/9; too_small: 4{nb}000/9", markdown, StringComparison.Ordinal);
        Assert.Equal(2, html.Split("<figure").Length - 1); // квадрат двумя вариантами — одна картинка, дуга — вторая
        Assert.Equal(2, html.Split("<svg").Length - 1);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("src=", html, StringComparison.Ordinal);
        Assert.Equal(html.Split("http").Length - 1, html.Split("http://www.w3.org/2000/svg").Length - 1);
    }

    [Fact]
    public void Reads_the_output_of_the_swift_half_and_refuses_a_missing_field()
    {
        // Как пишет LoopReplay (JSONEncoder, ключи по алфавиту, nil не пишется).
        const string json = """
            {"input":"gorodki-my-data.json","newcomer":false,"runs":[{"id":"8d3f","judge":{"accepted":1,"breaks":0,"ignored":1},
            "league":"run","motion":[{"activity":"walking","t":1790000000000}],"motionAuthorized":true,
            "points":[{"acc":4.5,"flags":0,"lat":52.0976,"lon":23.688,"seq":0,"t":1790000000000},
                      {"acc":30,"flags":1,"lat":52.09761,"lon":23.68801,"seq":1,"speed":1.4,"t":1790000001000}],
            "pointsAfterGap":0,"serverCaptures":[{"runId":"8d3f","status":"applied","areaSquareMeters":3500}],
            "steps":[{"end":1790000001000,"start":1790000000000}],
            "variants":[{"detector":{"maxRadiusMeters":50,"minEstimatedAreaSquareMeters":1000,"minPathMeters":150,
              "minRadiusMeters":15,"radiusFactor":0},"loops":[]}]}]}
            """;

        var file = ReplayFile.Read(json);
        var run = Assert.Single(file.Runs);

        Assert.Equal(15, Assert.Single(run.Variants).Detector.MinRadiusMeters);
        Assert.Equal([null, 1.4], run.Points.Select(p => p.Speed));
        Assert.Null(Assert.Single(run.Steps).Steps);
        Assert.Equal([VerdictKind.Accepted, VerdictKind.Ignored], Calibration.Evaluate(file, [Current])[0].Verdicts.Select(v => v.Kind)); // точность 30 м > 25
        Assert.Throws<JsonException>(() => ReplayFile.Read(json.Replace("\"pointsAfterGap\":0,", "", StringComparison.Ordinal)));
    }

    [Fact]
    public void Options_default_to_the_current_numbers_and_take_lists()
    {
        var defaults = Options.Parse(["run.loops.json"]);
        var grid = Options.Parse(["loops.json", "--min-area", "1500,2500,3500", "--min-half-width", "6,9,12", "--out", "r.html"]);

        Assert.Equal([Current], defaults.Shapes);
        Assert.Equal("run.loops.html", defaults.Out);
        Assert.Equal(9, grid.Shapes.Count);
        Assert.Equal(new ShapeVariant(1_500, 12), grid.Shapes[2]);
        Assert.Throws<UsageException>(() => Options.Parse(["loops.json", "--min-area", "2,5k"]));
        Assert.Throws<UsageException>(() => Options.Parse(["--min-area", "2500"]));
    }
}
