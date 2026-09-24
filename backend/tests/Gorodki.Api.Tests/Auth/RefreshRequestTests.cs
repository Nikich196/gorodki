using System.Net;
using System.Text;

namespace Gorodki.Api.Tests.Auth;

/// <summary>
/// Тело без refresh-токена: JSON не проверяет, что поле пришло, и в обработчик попадает null. Ответ — как на любой
/// негодный токен, а не 500 с ошибкой в логе (база до этого не нужна — сервер без неё и проверяем).
/// </summary>
public sealed class RefreshRequestTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"refreshToken":null}""")]
    [InlineData("""{"refreshToken":""}""")]
    public async Task Refresh_without_a_token_asks_to_sign_in_again(string body)
    {
        await using var app = UnreachableDatabase.Server();

        var response = await app.CreateClient().PostAsync("/auth/refresh", Json(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("refresh_invalid", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"refreshToken":null}""")]
    [InlineData("""{"refreshToken":""}""")]
    public async Task Logout_without_a_token_has_nothing_to_revoke(string body)
    {
        await using var app = UnreachableDatabase.Server();

        var response = await app.CreateClient().PostAsync("/auth/logout", Json(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
