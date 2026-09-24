using Gorodki.Api.Features.Captures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gorodki.Api.Tests.Architecture;

/// <summary>Запуск сервера и его журнал — то, что видно на Render.</summary>
public sealed class StartupTests
{
    [Fact]
    public void Server_without_a_connection_string_refuses_to_start_outside_development()
    {
        // Потерянная на Render переменная: иначе сервер без базы и входа отвечал бы «healthy», а игра лежала бы.
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Gorodki", "");
        });

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains("ConnectionStrings__Gorodki", Messages(error));
    }

    [Fact]
    public void Sql_commands_are_not_written_to_the_log_one_by_one()
    {
        // Проход обработчика раз в 5 секунд — это сотни тысяч строк «Executed DbCommand» в сутки: в них тонут ошибки.
        // Ошибки команд EF пишет уровнем Error — они остаются.
        using var app = new WebApplicationFactory<Program>();
        var loggers = app.Services.GetRequiredService<ILoggerFactory>();

        var commands = loggers.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");
        Assert.False(commands.IsEnabled(LogLevel.Information));
        Assert.True(commands.IsEnabled(LogLevel.Error));
        Assert.True(loggers.CreateLogger<CaptureWorker>().IsEnabled(LogLevel.Information));
    }

    private static string Messages(Exception error)
    {
        var messages = new List<string>();
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(" | ", messages);
    }
}
