using DocProc.Application.Outputs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Infrastructure.Outputs;

/// <summary>Wiring of the output paths (docs/11 §13): the renderer pool, the values view builder and the render service.</summary>
public static class OutputServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IOutputRenderer"/> (<see cref="JsonataRenderer"/>, one pool of renderer processes per host,
    /// started on first use), <see cref="IDocumentDataViewBuilder"/> and <see cref="OutputRenderService"/>; the
    /// options bind to <c>Outputs</c> and <c>Outputs:Renderer</c>.
    /// </summary>
    public static IServiceCollection AddDocProcOutputs(this IServiceCollection services)
    {
        services.AddOptions<OutputOptions>().Configure<IServiceProvider>((o, sp) => sp.GetService<IConfiguration>()?.GetSection(OutputOptions.SectionName).Bind(o));
        services.AddOptions<JsonataRendererOptions>().Configure<IServiceProvider>((o, sp) => sp.GetService<IConfiguration>()?.GetSection(JsonataRendererOptions.SectionName).Bind(o));
        services.TryAddSingleton<JsonataRenderer>();
        services.TryAddSingleton<IOutputRenderer>(sp => sp.GetRequiredService<JsonataRenderer>());
        services.TryAddScoped<IDocumentDataViewBuilder, DocumentDataViewBuilder>();
        services.TryAddScoped<OutputRenderService>();
        return services;
    }
}
