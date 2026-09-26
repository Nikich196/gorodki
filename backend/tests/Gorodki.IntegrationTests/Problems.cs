using System.Net.Http.Json;
using System.Text.Json;

namespace Gorodki.IntegrationTests;

/// <summary>Статус и код ошибки (<c>code</c> в ProblemDetails) — для тестов задач Егора.</summary>
internal static class Problems
{
    public static async Task<(int Status, string? Code)> OfAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (text.Length == 0)
        {
            return ((int)response.StatusCode, null);
        }

        var problem = JsonSerializer.Deserialize<JsonElement>(text);
        return ((int)response.StatusCode,
            problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    /// <summary>Ответ без полей широты и долготы — ни под каким именем (egor-server.md, раздел 4, п. 2).</summary>
    public static void HasNoCoordinates(string json)
    {
        foreach (var name in new[] { "\"lat", "\"lon", "\"latitude", "\"longitude", "\"coordinates" })
        {
            Assert.DoesNotContain(name, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Разобрать тело ответа, не проверяя статус.</summary>
    public static async Task<T> BodyAsync<T>(HttpResponseMessage response, CancellationToken cancel) =>
        (await response.Content.ReadFromJsonAsync<T>(RunRequests.Json, cancel))!;
}
