using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Pdf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Infrastructure;

/// <summary>Wiring for the non-LLM parts of docs/03 stages 2 to 4: PDF processing, keyword classification and heuristic splitting.</summary>
public static class DocumentProcessingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPdfProcessor"/> (PdfPig + PDFtoImage), the keyword <see cref="IDocumentClassifier"/> and the
    /// heuristic <see cref="IDocumentSplitter"/> / <see cref="IHeuristicSplitter"/> as singletons. When
    /// <paramref name="configuration"/> is given, the option classes bind to their <c>DocumentProcessing:*</c> sections.
    /// </summary>
    public static IServiceCollection AddDocProcDocumentProcessing(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddOptions<PdfProcessingOptions>();
        services.AddOptions<KeywordClassifierOptions>();
        services.AddOptions<HeuristicSplitterOptions>();

        if (configuration is not null)
        {
            services.AddOptions<PdfProcessingOptions>().Configure(o => configuration.GetSection(PdfProcessingOptions.SectionName).Bind(o));
            services.AddOptions<KeywordClassifierOptions>().Configure(o => configuration.GetSection(KeywordClassifierOptions.SectionName).Bind(o));
            services.AddOptions<HeuristicSplitterOptions>().Configure(o => configuration.GetSection(HeuristicSplitterOptions.SectionName).Bind(o));
        }

        services.TryAddSingleton<IPdfProcessor, PdfPigPdfProcessor>();

        services.TryAddSingleton<KeywordDocumentClassifier>();
        services.TryAddSingleton<IDocumentClassifier>(sp => sp.GetRequiredService<KeywordDocumentClassifier>());

        services.TryAddSingleton<HeuristicDocumentSplitter>();
        services.TryAddSingleton<IHeuristicSplitter>(sp => sp.GetRequiredService<HeuristicDocumentSplitter>());
        services.TryAddSingleton<IDocumentSplitter>(sp => sp.GetRequiredService<HeuristicDocumentSplitter>());

        return services;
    }
}
