using DocProc.Domain.Documents;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DocProc.Api.Documents;

/// <summary>
/// Documents <see cref="DocumentTypeKey"/> as what it is on the wire: a string of the key syntax (docs/11 §6.2, §18).
/// Its JSON converter makes it opaque to the schema generator, which would otherwise describe it as "any value". The
/// legacy keys are listed as examples only: a tenant's own types are keys too, so the schema is not an enum.
/// </summary>
public sealed class DocumentTypeKeySchemaTransformer : IOpenApiSchemaTransformer
{
    public const string Pattern = "^[A-Za-z][A-Za-z0-9_.-]{0,63}$";

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (type != typeof(DocumentTypeKey))
        {
            return Task.CompletedTask;
        }

        schema.Type = context.JsonTypeInfo.Type == type ? JsonSchemaType.String : JsonSchemaType.String | JsonSchemaType.Null;
        schema.Pattern = Pattern;
        schema.MaxLength = DocumentTypeKey.MaxLength;
        schema.Description = "A document type key: a legacy type (" + string.Join(", ", WellKnownTypeKeys.Legacy) + ") or a key of one of the tenant's types.";
        schema.Properties?.Clear();
        return Task.CompletedTask;
    }
}
