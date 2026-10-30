using DocProc.Application.Abstractions;
using DocProc.Llm.Extraction;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocProc.Llm;

/// <summary>The <c>Llm:Replay</c> section (docs/09 "LLM providers").</summary>
public sealed class LlmReplayOptions
{
    /// <summary>
    /// Fixture directory of the <see cref="FileReplayStore"/>; unset means no replay provider. A relative path is
    /// resolved with <see cref="RepositoryPaths.ResolveUpwards"/> when the host passes its content root, so
    /// <c>fixtures/demo/llm-replay</c> finds the repository's folder from any project (docs/09 "Demo data").
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>Wrap every real adapter so misses are recorded into <see cref="Directory"/>; otherwise misses throw.</summary>
    public bool Record { get; set; }

    /// <summary>Serve the Replay kind from a <see cref="ScriptedLlmProvider"/> instead of fixtures (tests).</summary>
    public bool Scripted { get; set; }
}

/// <summary>
/// Registers the gateway with its ports, the prompt catalogue, the price table, the LLM stage implementations and,
/// when configured, the replay provider. Adapters (Anthropic, OpenAI) register their own <see cref="ILlmProviderFactory"/>;
/// the host registers <see cref="ILlmPolicyStore"/>, <see cref="ILlmCredentialStore"/> and <see cref="ILlmUsageRecorder"/>.
/// </summary>
public static class LlmServiceCollectionExtensions
{
    public const string SectionName = "Llm";

    /// <param name="services">The host's services.</param>
    /// <param name="configuration">The host configuration; <c>Llm:*</c> is read from it.</param>
    /// <param name="contentRootPath">
    /// The host's content root. When given, a relative <c>Llm:Replay:Directory</c> is resolved upwards from it
    /// (<see cref="RepositoryPaths.ResolveUpwards"/>); without it a relative directory is relative to the working directory.
    /// </param>
    public static IServiceCollection AddDocProcLlm(this IServiceCollection services, IConfiguration configuration, string? contentRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var replay = section.GetSection("Replay").Get<LlmReplayOptions>() ?? new LlmReplayOptions();
        if (!string.IsNullOrWhiteSpace(replay.Directory) && !string.IsNullOrWhiteSpace(contentRootPath))
        {
            replay.Directory = RepositoryPaths.ResolveUpwards(replay.Directory, contentRootPath);
        }

        var extraction = section.GetSection("Extraction").Get<LlmExtractionOptions>() ?? new LlmExtractionOptions();
        if (replay.Record && string.IsNullOrWhiteSpace(replay.Directory))
        {
            throw new InvalidOperationException("Llm:Replay:Record requires Llm:Replay:Directory.");
        }

        services.AddSingleton(replay);
        services.AddSingleton(extraction);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(PriceTable.Default);
        services.TryAddSingleton(_ => PromptCatalogue.Load());

        if (replay.Scripted)
        {
            services.TryAddSingleton<ScriptedLlmProvider>();
            services.AddSingleton<ILlmProviderFactory>(sp => new ScriptedLlmProviderFactory(sp.GetRequiredService<ScriptedLlmProvider>()));
        }
        else if (!string.IsNullOrWhiteSpace(replay.Directory))
        {
            services.TryAddSingleton<FileReplayStore>(sp => new FileReplayStore(replay.Directory, sp.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<IReplayStore>(sp => sp.GetRequiredService<FileReplayStore>());
            services.TryAddSingleton<IRecordingReplayStore>(sp => sp.GetRequiredService<FileReplayStore>());
            services.AddSingleton<ILlmProviderFactory>(sp => new ReplayLlmProviderFactory(sp.GetRequiredService<IReplayStore>()));
        }

        services.TryAddSingleton<ILlmGateway>(sp => new LlmGateway(
            sp.GetRequiredService<ILlmPolicyStore>(),
            sp.GetRequiredService<ILlmCredentialStore>(),
            ProviderFactories(sp, replay),
            sp.GetRequiredService<ILlmUsageRecorder>(),
            sp.GetRequiredService<PriceTable>(),
            sp.GetRequiredService<TimeProvider>()));

        services.TryAddSingleton<LlmInvoiceExtractor>();
        // One LLM extractor in the IEnumerable<IDocumentExtractor> however many times a host composes this helper.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, LlmInvoiceExtractor>(sp => sp.GetRequiredService<LlmInvoiceExtractor>()));
        services.TryAddSingleton<LlmDocumentClassifier>();
        services.TryAddSingleton<IDocumentClassifier>(sp => sp.GetRequiredService<LlmDocumentClassifier>());
        services.TryAddSingleton<LlmDocumentSplitter>();
        services.TryAddSingleton<IDocumentSplitter>(sp => sp.GetRequiredService<LlmDocumentSplitter>());
        return services;
    }

    /// <summary>In record mode every adapter is wrapped so the gateway cannot bypass the recorder whatever the registration order.</summary>
    private static List<ILlmProviderFactory> ProviderFactories(IServiceProvider sp, LlmReplayOptions replay)
    {
        var factories = sp.GetServices<ILlmProviderFactory>().ToList();
        return replay.Record
            ? [new RecordingLlmProviderFactory(factories, sp.GetRequiredService<IRecordingReplayStore>())]
            : factories;
    }
}
