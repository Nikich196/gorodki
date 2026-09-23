using System.Net.Http.Headers;
using Gorodki.Api.Features.Auth;
using Gorodki.Api.Features.Config;
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

    /// <summary>
    /// Игрок прямо в базе и клиент с его access-токеном — для тестов, где сам вход не важен. По умолчанию — «бывалый»:
    /// аккаунту месяц и за ним 10 км старого забега, иначе он чужие уровни не снимает (защита от мультиаккаунтов, §3.3).
    /// <paramref name="newcomer"/> — аккаунт только что заведён и не бегал.
    /// </summary>
    public async Task<(HttpClient Client, Guid UserId)> CreatePlayerClientAsync(UserRole role = UserRole.Player, bool newcomer = false)
    {
        var id = Guid.CreateVersion7();
        var now = Time.GetUtcNow();
        var user = new UserEntity
        {
            Id = id,
            GoogleSubject = $"google:{id:N}",
            // Хвост UUIDv7 случайный (начало — время), поэтому ники разных игроков не совпадут.
            DisplayName = $"Тест-{id.ToString("N")[^10..]}",
            NormalizedName = $"тест-{id.ToString("N")[^10..]}",
            Role = role,
            CreatedAt = newcomer ? now : now.AddDays(-30),
        };
        if (!newcomer)
        {
            // Старый забег ссылается на версию конфига 1 — в новой базе её заводит хранилище при первом обращении.
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<GameConfigStore>().GetCurrentAsync(CancellationToken.None);
        }

        await using (var db = database.CreateContext())
        {
            db.Users.Add(user);
            if (!newcomer)
            {
                // Старый забег, уже обработанный и без точек: в тумане, визитах и хранении он ничего не меняет.
                var startedAt = now.AddDays(-29);
                db.Runs.Add(new RunEntity
                {
                    Id = Guid.CreateVersion7(),
                    UserId = id,
                    League = Gorodki.Domain.Leagues.League.Run,
                    Source = RunSource.Live,
                    ConfigVersion = 1,
                    StartedAt = startedAt,
                    EndedAt = startedAt.AddHours(1),
                    Status = RunStatus.Finished,
                    CreatedAt = startedAt,
                    DeviceId = Guid.NewGuid(),
                    AppVersion = "0.1.0 (1)",
                    MotionAuthorized = true,
                    LastSeq = -1,
                    FogStampedAt = startedAt.AddHours(1),
                    VisitsProcessedAt = startedAt.AddHours(1),
                    AcceptedMeters = 10_000,
                    PointsPurgedAt = startedAt.AddDays(14),
                });
            }

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
