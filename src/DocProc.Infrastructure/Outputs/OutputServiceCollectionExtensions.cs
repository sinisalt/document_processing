using DocProc.Application.Outputs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using DocProc.Infrastructure.Outputs.Delivery;

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

        // Delivery (docs/11 §13.3-13.6): the fan-out the publish stage runs, the processor of ProcessDelivery, and the
        // transports. Delivery:DevelopmentHosts is honoured only in the Development environment.
        services.AddOptions<DeliveryOptions>().Configure<IServiceProvider>((o, sp) => sp.GetService<IConfiguration>()?.GetSection(DeliveryOptions.SectionName).Bind(o));
        services.TryAddSingleton(sp =>
        {
            var environment = sp.GetService<IHostEnvironment>();
            var hosts = sp.GetRequiredService<IOptions<DeliveryOptions>>().Value.DevelopmentHosts;
            return environment?.IsDevelopment() == true && hosts.Count > 0 ? new DeliveryEndpointPolicy(hosts) : DeliveryEndpointPolicy.Strict;
        });
        services.TryAddSingleton<DeliveryHttpClient>();
        services.TryAddSingleton<OAuthTokenCache>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDeliveryTransport, HttpDeliveryTransport>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDeliveryTransport, BlobDropTransport>(sp =>
        {
            var root = sp.GetRequiredService<IOptions<DeliveryOptions>>().Value.DropRoot;
            var contentRoot = sp.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory();
            return new BlobDropTransport(Path.GetFullPath(root, contentRoot));
        }));
        services.TryAddScoped<Lookups.HttpLookupService>();
        services.TryAddScoped<DeliveryFanOut>();
        services.TryAddScoped<DeliveryProcessor>();
        services.TryAddSingleton<DestinationReader>();
        services.TryAddSingleton<Acknowledgements.ExternalStatusPoller>();
        services.TryAddSingleton<MasterData.MasterDataSync>();
        return services;
    }
}
