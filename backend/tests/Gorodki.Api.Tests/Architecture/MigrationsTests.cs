using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Tests.Architecture;

public sealed class MigrationsTests
{
    [Fact]
    public void Every_model_change_has_a_migration()
    {
        // К базе не подключается: сравнивает модель в коде с последним снимком миграций.
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, "Host=localhost;Database=not-used");
        using var db = new AppDbContext(options.Options);

        Assert.False(
            db.Database.HasPendingModelChanges(),
            "Модель изменилась, а миграции нет: dotnet ef migrations add <Имя> (docs/guides/getting-started-windows.md).");
    }
}
