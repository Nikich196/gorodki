using System.Text.RegularExpressions;

namespace Gorodki.Api.Tests.Architecture;

/// <summary>
/// Каждое пространство блокировок PostgreSQL из кода сервера и инструментов вписано в список
/// docs/architecture/jobs.md, «Пространства блокировок». По этому списку берут номер для новой блокировки: незаписанный
/// номер взяли бы второй раз, и два разных дела молча ждали бы друг друга (срез — 4 и конвейер OSM — 5 были записаны
/// только в коде).
/// </summary>
public sealed class LockSpacesTests
{
    private static readonly Regex NumberedLock = new(@"pg_advisory_xact_lock\((\d+),", RegexOptions.Compiled);

    private static readonly Regex TileLock = new(@"lockSpace = 100 \+ \(int\)", RegexOptions.Compiled);

    [Fact]
    public void Every_lock_space_in_code_is_in_the_list_in_jobs_md()
    {
        var root = RepositoryRoot();
        var code = new[] { "src", "tools" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, "backend", folder), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();
        var spaces = code.SelectMany(text => NumberedLock.Matches(text).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        var listed = ListedSpaces(File.ReadAllText(Path.Combine(root, "docs", "architecture", "jobs.md")));

        Assert.NotEmpty(spaces);
        Assert.All(spaces, space => Assert.Contains(space, listed));
        Assert.Contains(code, TileLock.IsMatch); // блокировки тайлов — через переменную lockSpace
        Assert.Contains("100 + лига", listed);
    }

    /// <summary>Первый столбец таблицы в разделе «Пространства блокировок».</summary>
    private static HashSet<string> ListedSpaces(string markdown)
    {
        var start = markdown.IndexOf("\n## Пространства блокировок", StringComparison.Ordinal);
        Assert.True(start >= 0, "В jobs.md нет раздела «Пространства блокировок».");
        var end = markdown.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return markdown[start..(end < 0 ? markdown.Length : end)]
            .Split('\n')
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
            .Select(line => line.Split('|')[1].Trim())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "backend")) && Directory.Exists(Path.Combine(directory.FullName, "docs")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория (папки backend и docs).");
    }
}
