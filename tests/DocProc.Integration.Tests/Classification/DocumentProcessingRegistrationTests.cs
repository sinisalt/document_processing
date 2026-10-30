using DocProc.Application.Abstractions;
using DocProc.Infrastructure;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Pdf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocProc.Integration.Tests.Classification;

/// <summary>The DI wiring the worker uses for docs/03 stages 2 to 4.</summary>
public class DocumentProcessingRegistrationTests
{
    [Fact]
    public void Registers_the_pdf_processor_classifier_and_splitter_as_singletons()
    {
        var services = new ServiceCollection().AddDocProcDocumentProcessing();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IPdfProcessor>().ShouldBeOfType<PdfPigPdfProcessor>();
        provider.GetRequiredService<IDocumentClassifier>().ShouldBeOfType<KeywordDocumentClassifier>();
        var splitter = provider.GetRequiredService<IDocumentSplitter>();
        splitter.ShouldBeOfType<HeuristicDocumentSplitter>();
        provider.GetRequiredService<IHeuristicSplitter>().ShouldBeSameAs(splitter);
        provider.GetRequiredService<IPdfProcessor>().ShouldBeSameAs(provider.GetRequiredService<IPdfProcessor>());
    }

    [Fact]
    public void Binds_options_from_the_document_processing_configuration_sections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{KeywordClassifierOptions.SectionName}:ConfidenceThreshold"] = "0.85",
                [$"{HeuristicSplitterOptions.SectionName}:MaxConfidentPageCount"] = "10",
                [$"{PdfProcessingOptions.SectionName}:TextLayerMinimumWordsPerPage"] = "12",
            })
            .Build();
        var services = new ServiceCollection().AddDocProcDocumentProcessing(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<KeywordClassifierOptions>>().Value.ConfidenceThreshold.ShouldBe(0.85);
        provider.GetRequiredService<IOptions<HeuristicSplitterOptions>>().Value.MaxConfidentPageCount.ShouldBe(10);
        provider.GetRequiredService<IOptions<PdfProcessingOptions>>().Value.TextLayerMinimumWordsPerPage.ShouldBe(12);
    }

    [Fact]
    public void Defaults_match_the_design_documents()
    {
        var classifier = new KeywordClassifierOptions();
        var splitter = new HeuristicSplitterOptions();

        classifier.ConfidenceThreshold.ShouldBe(0.6);
        splitter.MaxConfidentPageCount.ShouldBe(6);
    }
}
