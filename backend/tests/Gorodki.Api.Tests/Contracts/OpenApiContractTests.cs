using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorodki.Api.Features.Captures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gorodki.Api.Tests.Contracts;

/// <summary>
/// Описание API — контракт с приложением (спайк S6): <c>contracts/openapi.v1.json</c>. Из него генерируется Swift-клиент
/// (<c>ios/Packages/GorodkiAPI</c>, там лежит копия). Изменил адреса — обнови файлы: <c>GORODKI_UPDATE_CONTRACTS=1 dotnet test</c>.
/// База для описания не нужна: сервер подключается к ней только по запросу, а описанию запросы не нужны.
/// </summary>
public sealed class OpenApiContractTests
{
    private static readonly string Root = RepositoryRoot();
    private static readonly string ContractPath = Path.Combine(Root, "contracts", "openapi.v1.json");
    private static readonly string SwiftCopyPath = Path.Combine(Root, "ios", "Packages", "GorodkiAPI", "Sources", "GorodkiAPI", "openapi.json");

    [Fact]
    public async Task Api_description_matches_the_contract_file_and_its_swift_copy()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Gorodki", "Host=localhost;Database=openapi-only");
            builder.UseSetting("Auth:SigningKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting(CaptureWorker.EnabledSetting, "false");
        });
        var live = JsonNode.Parse(await app.CreateClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))!;
        live.AsObject().Remove("servers"); // адрес сервера зависит от окружения — в контракт не входит
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var text = live.ToJsonString(options).ReplaceLineEndings("\n") + "\n";

        if (Environment.GetEnvironmentVariable("GORODKI_UPDATE_CONTRACTS") == "1")
        {
            File.WriteAllText(ContractPath, text);
            Directory.CreateDirectory(Path.GetDirectoryName(SwiftCopyPath)!);
            File.WriteAllText(SwiftCopyPath, text);
        }

        Assert.True(
            JsonNode.DeepEquals(live, JsonNode.Parse(File.ReadAllText(ContractPath))),
            "Описание API изменилось, а contracts/openapi.v1.json — нет. Обнови: GORODKI_UPDATE_CONTRACTS=1 dotnet test.");
        Assert.Equal(File.ReadAllText(ContractPath), File.ReadAllText(SwiftCopyPath));
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
