using System.Text.Json.Serialization;
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
    /// the upload endpoint, and the <see cref="NoOpPipelineQueue"/> fallback. Call it last: the fallback is a
    /// <c>TryAdd</c>, so a pipeline runner registered before it keeps its own <see cref="IPipelineQueue"/>.
    /// </summary>
    public static IServiceCollection AddDocProcApiServices(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = IngestionLimits.MaxRequestBodyBytes;
            options.ValueLengthLimit = int.MaxValue;
        });
        services.TryAddSingleton<IPipelineQueue, NoOpPipelineQueue>();
        return services;
    }
}
