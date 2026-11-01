using System.Text.Json.Serialization;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Application.Pipeline;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Api.Pipeline;

public static class ApiServiceCollectionExtensions
{
    /// <summary>
    /// Everything the document endpoints need beyond persistence and blobs: enums as strings in every JSON body
    /// (status, type, severity read naturally in the clients and the OpenAPI document), multipart limits sized for
    /// the upload endpoint, the idempotency-key purge, and the <see cref="NoOpPipelineQueue"/> fallback. Call it
    /// last: the fallback is a <c>TryAdd</c>, so a pipeline runner registered before it keeps its own
    /// <see cref="IPipelineQueue"/>.
    /// </summary>
    public static IServiceCollection AddDocProcApiServices(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.Configure<FormOptions>(options =>
        {
            // The multipart body may carry up to twenty files; the files stream to the blob store, so this is a limit
            // on the whole body, not on memory. Non-file form values (channelId) keep the default 4 MB cap: with the
            // limit lifted a caller could post a 1 GB text field that is buffered in memory.
            options.MultipartBodyLengthLimit = IngestionLimits.MaxRequestBodyBytes;
        });
        services.AddSingleton<IdempotencyKeyPurge>();
        services.TryAddSingleton<IPipelineQueue, NoOpPipelineQueue>();
        return services;
    }
}
