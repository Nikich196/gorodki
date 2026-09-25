using System.Globalization;
using System.Net;
using System.Text;
using Gorodki.Domain.Territory;

namespace Gorodki.Calibration;

/// <summary>Таблица: одни и те же строки — в Markdown для экрана и в HTML для отчёта.</summary>
public sealed record Table(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public string ToMarkdown()
    {
        static string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(c => c.Replace("|", "\\|", StringComparison.Ordinal))) + " |";
        var lines = new List<string> { Row(Header), Row(Header.Select(_ => "---")) };
        lines.AddRange(Rows.Select(Row));
        return string.Join('\n', lines) + "\n";
    }

    public string ToHtml()
    {
        var html = new StringBuilder("<table><thead><tr>");
        foreach (var cell in Header)
        {
            html.Append("<th>").Append(WebUtility.HtmlEncode(cell)).Append("</th>");
        }

        html.Append("</tr></thead><tbody>");
        foreach (var row in Rows)
        {
            html.Append("<tr>");
            foreach (var cell in row)
            {
                html.Append("<td>").Append(WebUtility.HtmlEncode(cell)).Append("</td>");
            }

            html.Append("</tr>");
        }

        return html.Append("</tbody></table>").ToString();
    }
}

/// <summary>Отчёт разбора: заметки, итог по сетке, таблица петель, картинки.</summary>
public static class Report
{
    private static readonly string[] MainOutcomes = [Calibration.Applied, "too_small", "too_narrow"];

    /// <summary>Число по-русски: пробел между разрядами, запятая в дроби.</summary>
    public static string Number(double value, int decimals = 0) =>
        value.ToString(decimals == 0 ? "#,0" : "#,0." + new string('0', decimals), CultureInfo.InvariantCulture)
            .Replace(',', ' ')
            .Replace('.', ',');

    /// <summary>Число параметра: целое — без дроби.</summary>
    public static string Parameter(double value) => value == Math.Round(value) ? Number(value) : Number(value, 2).TrimEnd('0');

    public static string Detector(LoopDetectorSettings d)
    {
        var radius = d.RadiusFactor == 0 || d.MinRadiusMeters == d.MaxRadiusMeters
            ? $"R = {Parameter(d.MinRadiusMeters)}"
            : $"R {Parameter(d.MinRadiusMeters)}–{Parameter(d.MaxRadiusMeters)} (×{Parameter(d.RadiusFactor)})";
        return $"{radius}, путь ≥ {Parameter(d.MinPathMeters)}, оценка ≥ {Parameter(d.MinEstimatedAreaSquareMeters)}";
    }

    public static string Shape(ShapeVariant s) => $"{Parameter(s.MinAreaSquareMeters)}/{Parameter(s.MinHalfWidthMeters)}";

    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;

    private static IReadOnlyList<LoopDetectorSettings> Detectors(IReadOnlyList<RunEvaluation> runs) =>
        runs.Count == 0 ? [] : [.. runs[0].Run.Variants.Select(v => v.Detector)];

    public static IReadOnlyList<string> Notes(ReplayFile file, IReadOnlyList<RunEvaluation> runs)
    {
        var notes = new List<string>
        {
            "Детектор петли — код телефона (GameCore), судья, кольцо и контур — код сервера (Gorodki.Domain). "
                + "«applied» — контур прошёл шаг A; лимиты, «старше 3 часов», мультиаккаунты и земля на карте не проверяются.",
        };
        if (file.Newcomer)
        {
            notes.Add("Судья — с порогом точности новичка (35 м).");
        }

        foreach (var run in runs.Select(r => r.Run))
        {
            var note = $"Забег {ShortId(run.Id)}: точек {Number(run.Points.Count)}, судья — принято {Number(run.Judge.Accepted)}, "
                + $"отброшено {Number(run.Judge.Ignored)}, разрывов {Number(run.Judge.Breaks)}.";
            if (run.PointsAfterGap > 0)
            {
                note += $" После пропуска номеров не разобрано {Number(run.PointsAfterGap)} точек.";
            }

            if (!run.MotionAuthorized)
            {
                note += " Без разрешения «Движение»: сервер отклонил все заявки (motion_not_authorized).";
            }

            if (run.ServerCaptures.Count > 0)
            {
                note += " На сервере: " + string.Join(", ", run.ServerCaptures
                    .GroupBy(c => c.RejectCode is null ? c.Status : $"{c.Status}/{c.RejectCode}")
                    .Select(g => $"{g.Key} {g.Count()}")) + ".";
            }

            notes.Add(note);
        }

        return notes;
    }

    /// <summary>Итог по сетке: сколько петель при каждом сочетании чисел и чем они кончились.</summary>
    public static Table Summary(IReadOnlyList<RunEvaluation> runs, Options options)
    {
        var rows = new List<IReadOnlyList<string>>();
        var detectors = Detectors(runs);
        for (var v = 0; v < detectors.Count; v++)
        {
            var loops = runs.SelectMany(r => r.Loops).Where(l => l.VariantIndex == v).ToList();
            for (var s = 0; s < options.Shapes.Count; s++)
            {
                var outcomes = loops.Select(l => l.Outcomes[s]).ToList();
                var other = outcomes.Where(o => !MainOutcomes.Contains(o)).GroupBy(o => o).Select(g => $"{g.Key} {g.Count()}");
                rows.Add([
                    Detector(detectors[v]),
                    Parameter(options.Shapes[s].MinAreaSquareMeters),
                    Parameter(options.Shapes[s].MinHalfWidthMeters),
                    Number(loops.Count),
                    .. MainOutcomes.Select(o => Number(outcomes.Count(x => x == o))),
                    string.Join(", ", other),
                ]);
            }
        }

        return new Table(["Детектор", "A_min, м²", "R_min, м", "Петель", "applied", "too_small", "too_narrow", "Другие"], rows);
    }

    /// <summary>Номера картинок: одна петля (забег, начало, конец, замыкание), найденная разными вариантами, — одна картинка.</summary>
    public static Dictionary<(string Run, int Start, int End, string Closure), int> Figures(IReadOnlyList<RunEvaluation> runs)
    {
        var figures = new Dictionary<(string, int, int, string), int>();
        foreach (var run in runs)
        {
            foreach (var loop in run.Loops)
            {
                figures.TryAdd((run.Run.Id, loop.Loop.StartSeq, loop.Loop.EndSeq, loop.Loop.Closure), figures.Count + 1);
            }
        }

        return figures;
    }

    /// <summary>Каждая найденная петля каждого варианта детектора.</summary>
    public static Table Loops(IReadOnlyList<RunEvaluation> runs, Options options)
    {
        var figures = Figures(runs);
        var detectors = Detectors(runs);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var run in runs)
        {
            foreach (var evaluation in run.Loops)
            {
                var loop = evaluation.Loop;
                rows.Add([
                    Number(figures[(run.Run.Id, loop.StartSeq, loop.EndSeq, loop.Closure)]),
                    ShortId(run.Run.Id),
                    Detector(detectors[evaluation.VariantIndex]),
                    $"{loop.StartSeq}–{loop.EndSeq}",
                    loop.Closure,
                    Number(loop.RadiusMeters, 1),
                    Number(loop.GapMeters, 1),
                    Number(loop.EstimatedArea),
                    evaluation.AreaSquareMeters is { } area ? Number(area) : "—",
                    evaluation.HalfWidthMeters is { } half ? Number(half, 1) : "—",
                    Outcomes(evaluation, options.Shapes),
                ]);
            }
        }

        return new Table(
            ["Рис.", "Забег", "Детектор", "Точки", "Замыкание", "R, м", "Недоход, м", "Оценка телефона, м²", "Контур, м²", "Полуширина, м", "Исход (A_min/R_min)"],
            rows);
    }

    /// <summary>Исходы петли по сочетаниям: одинаковые — одной группой.</summary>
    public static string Outcomes(LoopEvaluation loop, IReadOnlyList<ShapeVariant> shapes)
    {
        var groups = loop.Outcomes.Select((outcome, i) => (Outcome: outcome, Shape: shapes[i])).GroupBy(x => x.Outcome).ToList();
        if (groups.Count == 1)
        {
            return shapes.Count == 1 ? groups[0].Key : $"{groups[0].Key} (все)";
        }

        return string.Join("; ", groups.Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => Shape(x.Shape)))}"));
    }

    public static string Markdown(ReplayFile file, IReadOnlyList<RunEvaluation> runs, Options options)
    {
        var text = new StringBuilder($"# Разбор забега: {file.Input}\n\n");
        foreach (var note in Notes(file, runs))
        {
            text.Append("- ").Append(note).Append('\n');
        }

        text.Append("\n## Итог по сетке\n\n").Append(Summary(runs, options).ToMarkdown());
        text.Append("\n## Петли\n\n");
        text.Append(runs.Any(r => r.Loops.Count > 0) ? Loops(runs, options).ToMarkdown() : "Петель не найдено.\n");
        return text.ToString();
    }

    public static string Html(ReplayFile file, IReadOnlyList<RunEvaluation> runs, Options options)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="ru">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Разбор забега — {{E(file.Input)}}</title>
            <style>
            body { font: 14px/1.45 system-ui, sans-serif; margin: 16px; max-width: 1200px; color: #1d1d1f; background: #fff; }
            table { border-collapse: collapse; margin: 8px 0 16px; font-size: 13px; }
            th, td { border: 1px solid #ccc; padding: 3px 6px; text-align: left; vertical-align: top; }
            figure { margin: 20px 0; padding-top: 10px; border-top: 1px solid #ddd; }
            svg { max-width: 100%; height: auto; border: 1px solid #e5e5e5; display: block; margin: 6px 0; }
            .applied { background: #dff3e3; } .too_small { background: #fde9cf; }
            .too_narrow { background: #eadff7; } .other { background: #f6d8d8; }
            .legend span { margin-right: 14px; white-space: nowrap; }
            </style>
            </head>
            <body>
            <h1>Разбор забега: {{E(file.Input)}}</h1>

            """);
        html.Append("<ul>");
        foreach (var note in Notes(file, runs))
        {
            html.Append("<li>").Append(E(note)).Append("</li>");
        }

        html.Append("</ul><h2>Итог по сетке</h2>").Append(Summary(runs, options).ToHtml());
        html.Append("<h2>Петли</h2>");
        html.Append(runs.Any(r => r.Loops.Count > 0) ? Loops(runs, options).ToHtml() : "<p>Петель не найдено.</p>");
        html.Append("<h2>Картинки «след + контур»</h2>");
        html.Append("""
            <p class="legend"><span>серое — весь забег</span><span>синее — след петли</span><span>× — точка, отброшенная судьёй</span>
            <span>зелёная точка — начало, красная — конец, пунктир между ними — хорда</span><span>зелёный пунктир — круг R вокруг начала</span>
            <span>заливка — контур P шага A</span><span>оранжевый пунктир — наибольший вписанный круг (полуширина)</span></p>
            """);

        var figures = Figures(runs);
        var detectors = Detectors(runs);
        foreach (var ((runId, start, end, closure), number) in figures)
        {
            var run = runs.First(r => r.Run.Id == runId);
            var same = run.Loops.Where(l => l.Loop.StartSeq == start && l.Loop.EndSeq == end && l.Loop.Closure == closure).ToList();
            var main = same.FirstOrDefault(l => l.Contour is not null) ?? same[0];
            var loop = main.Loop;
            html.Append(CultureInfo.InvariantCulture, $"<figure id=\"fig-{number}\"><figcaption><b>Рис. {number}.</b> ");
            html.Append(E($"Забег {ShortId(runId)}, точки {start}–{end}, замыкание {closure}, R {Number(loop.RadiusMeters, 1)} м, "
                + $"недоход {Number(loop.GapMeters, 1)} м (точность концов {Number(loop.StartAccuracy, 1)} и {Number(loop.EndAccuracy, 1)} м), "
                + $"оценка телефона {Number(loop.EstimatedArea)} м²"
                + (main.AreaSquareMeters is { } area ? $", контур {Number(area)} м²" : "")
                + (main.HalfWidthMeters is { } half ? $", полуширина {Number(half, 1)} м" : "")
                + (main.RingRejectCode is { } code ? $", кольцо: {code}" : "")
                + ". Нашли: " + string.Join("; ", same.Select(l => Detector(detectors[l.VariantIndex]))) + "."));
            foreach (var other in same.Where(l => !l.Outcomes.SequenceEqual(main.Outcomes)))
            {
                html.Append(E($" При «{Detector(detectors[other.VariantIndex])}» — {Outcomes(other, options.Shapes)}."));
            }

            html.Append("</figcaption>").Append(Figure.Svg(run, main)).Append(Matrix(main, options)).Append("</figure>\n");
        }

        return html.Append("</body>\n</html>\n").ToString();
    }

    /// <summary>Исходы петли: строки — A_min, столбцы — R_min.</summary>
    private static string Matrix(LoopEvaluation loop, Options options)
    {
        var html = new StringBuilder("<table><thead><tr><th>A_min \\ R_min</th>");
        foreach (var width in options.MinHalfWidths)
        {
            html.Append("<th>").Append(Parameter(width)).Append(" м</th>");
        }

        html.Append("</tr></thead><tbody>");
        for (var a = 0; a < options.MinAreas.Count; a++)
        {
            html.Append("<tr><th>").Append(Parameter(options.MinAreas[a])).Append(" м²</th>");
            for (var w = 0; w < options.MinHalfWidths.Count; w++)
            {
                var outcome = loop.Outcomes[(a * options.MinHalfWidths.Count) + w];
                var css = MainOutcomes.Contains(outcome) ? outcome : "other";
                html.Append("<td class=\"").Append(css).Append("\">").Append(WebUtility.HtmlEncode(outcome)).Append("</td>");
            }

            html.Append("</tr>");
        }

        return html.Append("</tbody></table>").ToString();
    }
}
