using System.Text.RegularExpressions;

namespace Gorodki.Api.Tests.Architecture;

/// <summary>
/// Инвариант хранилища земли (docs/architecture/data-model.md, аудит BE-01): строка куска после вставки на месте не
/// меняется — захват и откат удаляют строки и вставляют новые. На месте её меняют только визиты (<c>VisitProcessor</c>:
/// уровень и два времени) и удаление аккаунта (<c>AccountDeletion</c>: номер удалённого из списка снявших уровень). На этом
/// стоит точный откат скрытого захвата в публичной проекции (<c>ExactUndo</c>): вставленный захватом кусок он находит по
/// номеру строки и верит, что контур тот же, а состояние менялось только визитами (иначе — запасной путь). Новая правка
/// строки на месте тихо сломала бы это — тест заставит сначала подумать о проекции и дописать исключение.
/// </summary>
public sealed class ParcelWritersTests
{
    /// <summary>Правка строк <c>parcels</c> на месте: SQL, ExecuteUpdate, Update/Attach и присваивание полю куска.</summary>
    private static readonly Regex[] InPlace =
    [
        new(@"\bUPDATE\s+(app\.)?parcels\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bParcels\b[^;]*?\.\s*ExecuteUpdate", RegexOptions.Singleline | RegexOptions.Compiled),
        new(@"\bParcels\s*\.\s*(Update|UpdateRange|Attach|AttachRange)\s*\(", RegexOptions.Compiled),
        new(@"\.\s*(Geometry|OwnerId|Level|LastVisitAt|LastLevelUpAt|ShieldUntil|SiegeUntil|LossWindowSince|LossAttackers)\s*=(?![=>])", RegexOptions.Compiled),
    ];

    /// <summary>Кому можно: визиты и удаление аккаунта (их правки точный откат учитывает).</summary>
    private static readonly string[] Allowed =
    [
        Path.Combine("Features", "Captures", "VisitProcessor.cs"),
        Path.Combine("Features", "Me", "AccountDeletion.cs"),
    ];

    [Theory]
    [InlineData("await db.Database.ExecuteSqlAsync($\"UPDATE app.parcels SET level = 2\");", true)]
    [InlineData("\"update parcels set geometry = ...\"", true)]
    [InlineData("await db.Parcels\n    .Where(p => p.Id == id)\n    .ExecuteUpdateAsync(set => set.SetProperty(p => p.Level, 2));", true)]
    [InlineData("db.Parcels.Update(parcel);", true)]
    [InlineData("parcel.Geometry = shifted;", true)]
    [InlineData("entity.LastVisitAt = at;", true)]
    [InlineData("await db.Parcels.Where(p => p.OwnerId == userId).ExecuteDeleteAsync(cancellationToken);", false)]
    [InlineData("db.Parcels.AddRange(added); db.Captures.Where(c => c.Id == id).ExecuteUpdateAsync(set => set);", false)]
    [InlineData("var visited = state with { Level = 2, LastVisitAt = at };", false)]
    [InlineData("new CaptureJournalParcelEntity { Level = (short)row.State.Level, Geometry = row.Geometry };", false)]
    [InlineData("if (p.Level == 3 || parcel.LastVisitAt >= at) { }", false)]
    public void Rule_catches_in_place_parcel_edits(string code, bool caught)
    {
        Assert.Equal(caught, InPlace.Any(rule => rule.IsMatch(code)));
    }

    [Fact]
    public void Only_visits_and_account_deletion_edit_parcel_rows_in_place()
    {
        var sourceRoot = Path.Combine(FindBackendRoot(), "src");
        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Allowed.Any(allowed => path.EndsWith(allowed, StringComparison.Ordinal)))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return InPlace
                    .SelectMany(rule => rule.Matches(text))
                    .Select(match => $"{Path.GetRelativePath(sourceRoot, path)}:{text[..match.Index].Count(c => c == '\n') + 1}: {match.Value.Split('\n')[0].Trim()}");
            })
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Allowed_writers_still_exist_and_are_the_ones_that_edit()
    {
        // Если визиты или удаление переедут, исключение должно уехать с ними, а не остаться лазейкой для другого файла.
        var sourceRoot = Path.Combine(FindBackendRoot(), "src", "Gorodki.Api");
        foreach (var allowed in Allowed)
        {
            var text = File.ReadAllText(Path.Combine(sourceRoot, allowed));
            Assert.True(InPlace.Any(rule => rule.IsMatch(text)), $"{allowed} больше не правит parcels на месте — убери его из исключений");
        }
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
