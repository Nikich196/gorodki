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
        // ExecuteUpdate по запросу к кускам — в той же цепочке…
        new(@"(\bParcels\b|\bSet\s*<\s*ParcelEntity\s*>)[^;]*?\.\s*ExecuteUpdate", RegexOptions.Singleline | RegexOptions.Compiled),
        // …и по запросу, сохранённому в переменную раньше: цепочка выше обрывается на «;».
        new(
            @"\b(?<query>\w+)\s*=(?![=>])[^;]*?(\bParcels\b|\bSet\s*<\s*ParcelEntity\s*>)[^;]*;.*?\b\k<query>\b[^;]*?\.\s*ExecuteUpdate",
            RegexOptions.Singleline | RegexOptions.Compiled),
        new(@"(\bParcels|\bSet\s*<\s*ParcelEntity\s*>\s*\(\s*\))\s*\.\s*(Update|UpdateRange|Attach|AttachRange)\s*\(", RegexOptions.Compiled),
        // Присваивание полю куска — и составное (+=, ??=), и ++/-- с любой стороны.
        new(
            @"\.\s*(Geometry|OwnerId|Level|LastVisitAt|LastLevelUpAt|ShieldUntil|SiegeUntil|LossWindowSince|LossAttackers)\s*((?:[+\-*/%&|^]|\?\?|<<|>>)?=(?![=>])|\+\+|--)",
            RegexOptions.Compiled),
        new(@"(\+\+|--)\s*[\w.]+\.\s*(Level|LastVisitAt|LastLevelUpAt)\b", RegexOptions.Compiled),
    ];

    /// <summary>
    /// Правка через трекер изменений EF — <c>Entry(…)</c>, <c>SetValues(…)</c>, <c>CurrentValue =</c> — в файлах, где есть
    /// куски (<see cref="MentionsParcels"/>): у других сущностей так править можно, а тип сущности в <c>Entry(x)</c> по
    /// тексту не виден.
    /// </summary>
    private static readonly Regex[] InPlaceWhereParcels =
    [
        new(@"\.\s*Entry\s*\(", RegexOptions.Compiled),
        new(@"\bSetValues\s*\(", RegexOptions.Compiled),
        new(@"\.\s*CurrentValue\s*=(?![=>])", RegexOptions.Compiled),
    ];

    private static readonly Regex MentionsParcels = new(@"\bParcelEntity\b|\bParcels\b", RegexOptions.Compiled);

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
    [InlineData("parcel.Level += 1;", true)]
    [InlineData("parcel.LossAttackers ??= [];", true)]
    [InlineData("parcel.Level++;", true)]
    [InlineData("++parcel.Level;", true)]
    [InlineData("var parcel = await db.Parcels.SingleAsync(p => p.Id == id);\ndb.Entry(parcel).Property(p => p.Level).CurrentValue = 2;", true)]
    [InlineData("var parcel = await db.Parcels.SingleAsync(p => p.Id == id);\ndb.Entry(parcel).CurrentValues.SetValues(copy);", true)]
    [InlineData("foreach (var entry in db.ChangeTracker.Entries<ParcelEntity>()) { entry.Property(\"Level\").CurrentValue = 2; }", true)]
    [InlineData("var query = db.Parcels.Where(p => p.Id == id);\nawait query.ExecuteUpdateAsync(set => set.SetProperty(p => p.Level, 2));", true)]
    [InlineData("await db.Set<ParcelEntity>().Where(p => p.Id == id).ExecuteUpdateAsync(set => set);", true)]
    [InlineData("db.Set<ParcelEntity>().Update(parcel);", true)]
    [InlineData("await db.Parcels.Where(p => p.OwnerId == userId).ExecuteDeleteAsync(cancellationToken);", false)]
    [InlineData("db.Parcels.AddRange(added); db.Captures.Where(c => c.Id == id).ExecuteUpdateAsync(set => set);", false)]
    [InlineData("var visited = state with { Level = 2, LastVisitAt = at };", false)]
    [InlineData("new CaptureJournalParcelEntity { Level = (short)row.State.Level, Geometry = row.Geometry };", false)]
    [InlineData("if (p.Level == 3 || parcel.LastVisitAt >= at) { }", false)]
    [InlineData("if (p.Level != 3 && p.Level <= 2 && p.LossAttackers.Length > 0) { }", false)]
    [InlineData("db.Entry(capture).CurrentValues.SetValues(copy); entry.Property(\"Status\").CurrentValue = 2;", false)] // не куски
    [InlineData("var parcels = await db.Parcels.ToListAsync();\nawait db.Captures.Where(p => p.Id == id).ExecuteUpdateAsync(set => set);", false)]
    [InlineData("var stored = await db.Parcels.Where(p => p.Id == id).ToListAsync();\nvar same = stored.Count == 1;", false)]
    public void Rule_catches_in_place_parcel_edits(string code, bool caught)
    {
        Assert.Equal(caught, Offences(code).Any());
    }

    /// <summary>Все места в тексте файла, где строки кусков правятся на месте.</summary>
    private static IEnumerable<Match> Offences(string code) =>
        InPlace.Concat(MentionsParcels.IsMatch(code) ? InPlaceWhereParcels : []).SelectMany(rule => rule.Matches(code));

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
                return Offences(text)
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
            Assert.True(Offences(text).Any(), $"{allowed} больше не правит parcels на месте — убери его из исключений");
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
