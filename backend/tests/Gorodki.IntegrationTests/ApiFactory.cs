using System.Net.Http.Headers;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Gorodki.IntegrationTests;

/// <summary>Сервер в памяти, подключённый к базе в контейнере; время и Google — подставные.</summary>
internal sealed class ApiFactory(DatabaseFixture database) : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>Новый игрок прямо в базе и клиент с его access-токеном — для тестов, где сам вход не важен.</summary>
    public async Task<(HttpClient Client, Guid UserId)> CreatePlayerClientAsync(UserRole role = UserRole.Player)
    {
        var id = Guid.CreateVersion7();
        var user = new UserEntity
        {
            Id = id,
            GoogleSubject = $"google:{id:N}",
            // Хвост UUIDv7 случайный (начало — время), поэтому ники разных игроков не совпадут.
            DisplayName = $"Тест-{id.ToString("N")[^10..]}",
            NormalizedName = $"тест-{id.ToString("N")[^10..]}",
            Role = role,
            CreatedAt = Time.GetUtcNow(),
        };
        await using (var db = database.CreateContext())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var client = CreateClient();
        var token = Services.GetRequiredService<TokenService>().CreateAccessToken(user);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, id);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Gorodki", database.ConnectionString);
        builder.UseSetting("Auth:SigningKey", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        builder.UseSetting("Auth:GoogleClientIds:0", "test-client-id");
        builder.UseSetting(Gorodki.Api.Features.Captures.CaptureWorker.EnabledSetting, "false"); // тесты зовут обработчик сами
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IGoogleTokenValidator, FakeGoogle>();
        });
    }

    /// <summary>Подставной Google: «токен» вида <c>google:…</c> — это и есть идентификатор пользователя.</summary>
    private sealed class FakeGoogle : IGoogleTokenValidator
    {
        public Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken) =>
            Task.FromResult(idToken.StartsWith("google:", StringComparison.Ordinal)
                ? new GoogleIdentity(idToken, null, false)
                : null);
    }
}
