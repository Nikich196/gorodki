using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Gorodki.Api.Infrastructure.OpenApi;

/// <summary>
/// Правки описания API, чтобы клиент для iOS генерировался точным (спайк S6, docs/adr/0004-openapi-swift.md).
/// Работает над готовым документом: обёртки «может быть null» .NET добавляет уже после преобразователей схем.
/// </summary>
public static class SwiftFriendlySchemas
{
    public static Task Apply(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        foreach (var (name, schema) in document.Components?.Schemas ?? new Dictionary<string, IOpenApiSchema>())
        {
            if (schema is not OpenApiSchema concrete)
            {
                continue;
            }

            // Перечисления у нас сериализуются строками (JsonStringEnumConverter) — так и объявляем.
            if (concrete.Enum is { Count: > 0 } && concrete.Type is null)
            {
                concrete.Type = JsonSchemaType.String;
            }

            if (concrete.Properties is { Count: > 0 } properties)
            {
                MarkRequired(concrete, properties);
            }

            // Ошибки — ProblemDetails с кодом для приложения (run_not_found, chunk_conflict…) и дополнительными полями
            // (problems, overlaps). Без этого генератор выбросил бы код, по которому приложение решает, что делать.
            if (name is "ProblemDetails" or "HttpValidationProblemDetails")
            {
                concrete.Properties ??= new Dictionary<string, IOpenApiSchema>();
                concrete.Properties["code"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String | JsonSchemaType.Null,
                    Description = "Код ошибки для приложения: docs/architecture/*.md.",
                };
                concrete.Required?.Remove("code");
                concrete.AdditionalProperties = new OpenApiSchema(); // любые: problems, overlaps…
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Сервер пишет в ответ все поля, в том числе null: поле без null в типе есть всегда — обязательное.
    /// Необязательная ссылка на объект приходит как <c>oneOf [null, $ref]</c> — генератор Swift такие поля молча
    /// выбрасывает (найдено в S6), поэтому она становится просто ссылкой без пометки «обязательное»: null → nil.
    /// </summary>
    private static void MarkRequired(OpenApiSchema schema, IDictionary<string, IOpenApiSchema> properties)
    {
        schema.Required ??= new HashSet<string>();
        foreach (var (name, property) in properties.ToList())
        {
            if (property.OneOf is { Count: 2 } variants && variants.Count(v => v.Type == JsonSchemaType.Null) == 1)
            {
                properties[name] = variants.Single(v => v.Type != JsonSchemaType.Null);
                schema.Required.Remove(name);
            }
            else if (IsNullable(property))
            {
                schema.Required.Remove(name);
            }
            else
            {
                schema.Required.Add(name);
            }
        }
    }

    private static bool IsNullable(IOpenApiSchema property) =>
        property.Type?.HasFlag(JsonSchemaType.Null) == true
        || property.AnyOf?.Any(s => s.Type == JsonSchemaType.Null) == true;
}
