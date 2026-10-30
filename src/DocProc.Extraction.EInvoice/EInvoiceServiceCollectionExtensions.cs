using DocProc.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Extraction.EInvoice;

public static class EInvoiceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the structured e-invoice path (docs/03-pipeline.md stage 6a): the <see cref="IEInvoiceParser"/>
    /// backed by <see cref="EInvoiceParser"/>. It is stateless, so it is a singleton.
    /// </summary>
    public static IServiceCollection AddEInvoiceParsing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IEInvoiceParser, EInvoiceParser>();
        return services;
    }
}
