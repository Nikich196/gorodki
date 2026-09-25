using System.Globalization;

namespace Gorodki.Calibration;

/// <summary>Неверные параметры запуска.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>Параметры командной строки половины сервера.</summary>
public sealed record Options(string Loops, string Out, IReadOnlyList<double> MinAreas, IReadOnlyList<double> MinHalfWidths)
{
    public const string Usage = """
        Разбор записанного забега, половина сервера: контур шага A с другими A_min и R_min.

          Gorodki.Calibration <петли.json> [--min-area 1500,2500,3500] [--min-half-width 6,9,12] [--out отчёт.html]

        <петли.json> — выход ios/Tools/LoopReplay. Числа — через запятую, дробь через точку; по умолчанию — из кода
        (A_min 2500 м², R_min 9 м). Таблицы — на экран (Markdown), картинки «след + контур» — в HTML (по умолчанию рядом
        с файлом петель).
        """;

    /// <summary>Все сочетания A_min × R_min.</summary>
    public IReadOnlyList<ShapeVariant> Shapes =>
        [.. MinAreas.SelectMany(area => MinHalfWidths.Select(width => new ShapeVariant(area, width)))];

    public static Options Parse(IReadOnlyList<string> args)
    {
        string? loops = null;
        string? output = null;
        IReadOnlyList<double> areas = [ShapeVariant.Current.MinAreaSquareMeters];
        IReadOnlyList<double> widths = [ShapeVariant.Current.MinHalfWidthMeters];
        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new UsageException($"После {args[i]} нужно значение.");
            switch (args[i])
            {
                case "--min-area":
                    areas = Numbers(Value(), "--min-area");
                    break;
                case "--min-half-width":
                    widths = Numbers(Value(), "--min-half-width");
                    break;
                case "--out":
                    output = Value();
                    break;
                case var path when !path.StartsWith('-') && loops is null:
                    loops = path;
                    break;
                default:
                    throw new UsageException($"Неизвестный параметр: {args[i]}");
            }
        }

        if (loops is null)
        {
            throw new UsageException("Не указан файл петель (выход LoopReplay).");
        }

        return new Options(loops, output ?? Path.ChangeExtension(loops, ".html"), areas, widths);
    }

    private static List<double> Numbers(string text, string name)
    {
        var values = new List<double>();
        foreach (var part in text.Split(','))
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value < 0)
            {
                throw new UsageException($"{name}: нужны неотрицательные числа через запятую (дробь — через точку).");
            }

            values.Add(value);
        }

        return values;
    }
}
