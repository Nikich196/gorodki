using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gorodki.Api.Tests;

/// <summary>
/// Сервер со строкой подключения к базе, которой нет (закрытый порт на этой машине): вход и все адреса подключены, а
/// любой запрос в базу сразу падает. Так без Docker проверяется то, что сервер решает до базы, и как он живёт без неё.
/// </summary>
internal static class UnreachableDatabase
{
    public const string ConnectionString = "Host=127.0.0.1;Port=1;Database=unreachable;Username=nobody;Password=nothing;Timeout=3";

    public static WebApplicationFactory<Program> Server() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Gorodki", ConnectionString);
            builder.UseSetting("Auth:SigningKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting(CaptureWorker.EnabledSetting, "false");
            builder.UseSetting(RealtimePump.EnabledSetting, "false");
        });
}
