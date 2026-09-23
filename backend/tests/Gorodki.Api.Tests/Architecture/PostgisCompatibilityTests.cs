using System.Text.RegularExpressions;

namespace Gorodki.Api.Tests.Architecture;

/// <summary>
/// На Supabase стоит PostGIS 3.3.7: функций из 3.4+ там нет. Если такая функция попадёт в миграцию или SQL,
/// всё заработает в тестах на новой PostGIS и упадёт в проде — поэтому ловим это заранее (PLAN.md, §7.3, ADR 0003).
/// </summary>
public sealed class PostgisCompatibilityTests
{
    private static readonly Regex Banned = new(
        // Проверенные исследованием функции PostGIS 3.4+ (ST_CoverageInvalidEdges/Simplify/Union/Clean, ST_LargestEmptyCircle).
        // Если понадобится другая новая функция — сначала проверить её версию в документации PostGIS и дописать сюда.
        @"\bST_(Coverage\w*|LargestEmptyCircle)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Theory]
    [InlineData("SELECT ST_CoverageUnion(geom) FROM app.parcels", true)]
    [InlineData("SELECT st_largestemptycircle(geom)", true)]
    [InlineData("SELECT ST_Union(geom, 0.1), ST_IsValid(geom)", false)]
    public void Rule_catches_functions_missing_on_PostGIS_3_3(string sql, bool banned)
    {
        Assert.Equal(banned, Banned.IsMatch(sql));
    }

    [Fact]
    public void Server_code_and_migrations_use_only_PostGIS_3_3_functions()
    {
        var sourceRoot = Path.Combine(FindBackendRoot(), "src");
        var offenders = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".sql", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, index))
                .Where(x => Banned.IsMatch(x.line)))
            .Select(x => $"{Path.GetRelativePath(sourceRoot, x.path)}:{x.index + 1}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gorodki.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Не найден backend/Gorodki.slnx");
    }
}
