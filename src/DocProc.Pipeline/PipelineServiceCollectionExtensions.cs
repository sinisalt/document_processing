using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Pipeline;
using DocProc.Domain.Validation;
using DocProc.Extraction.EInvoice;
using DocProc.Infrastructure;
using DocProc.Infrastructure.Blobs;
using DocProc.Infrastructure.Llm;
using DocProc.Infrastructure.Security;
using DocProc.Llm;
using DocProc.Llm.Anthropic;
using DocProc.Llm.OpenAi;
using DocProc.Llm.Providers;
using DocProc.Pipeline.Extraction;
using DocProc.Pipeline.Queue;
using DocProc.Pipeline.Runner;
using DocProc.Pipeline.Stages;
using DocProc.Pipeline.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DocProc.Pipeline;

/// <summary>Wiring for the runner (docs/03 "Implementation") and the processing stack both hosts share.</summary>
public static class PipelineServiceCollectionExtensions
{
    /// <summary>
    /// The in-process queue only: <see cref="IPipelineQueue"/> for a host that enqueues work but runs no stages. Until
    /// the Service Bus transport lands, messages queued this way are consumed by nobody and lost at shutdown.
    /// </summary>
    public static IServiceCollection AddDocProcPipelineQueue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<InProcessPipelineQueue>();
        services.TryAddSingleton<IPipelineQueue>(sp => sp.GetRequiredService<InProcessPipelineQueue>());
        services.TryAddSingleton<IPipelineQueueReader>(sp => sp.GetRequiredService<InProcessPipelineQueue>());
        return services;
    }

    /// <summary>
    /// The queue, the runner (<see cref="PipelineRunnerHostedService"/>), the publish sweeper
    /// (<see cref="PublishSweeperHostedService"/>), every stage handler, the rule validator and the structured
    /// e-invoice extractor, with <see cref="PipelineOptions"/> bound from <c>Pipeline:*</c> and validated at start-up
    /// (<see cref="PipelineOptionsValidator"/>). Expects persistence, blobs,
    /// document processing, e-invoice parsing and the LLM services to be registered by the host; see
    /// <see cref="AddDocProcProcessingStack"/> for the complete set.
    /// </summary>
    public static IServiceCollection AddDocProcPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Validated at start-up: a section the runner or the stages cannot work with fails the host, not every document.
        services.AddOptions<PipelineOptions>().Configure(o => configuration.GetSection(PipelineOptions.SectionName).Bind(o)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PipelineOptions>, PipelineOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddDocProcPipelineQueue();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PipelineRunnerHostedService>());
        // Next to the runner: the publish messages it queues go to the queue this host's runner reads.
        services.TryAddSingleton<PublishSweeperHostedService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PublishSweeperHostedService>(sp => sp.GetRequiredService<PublishSweeperHostedService>()));
        // The channel pollers (docs/11 §7.1): mailboxes and watched folders of the tenants with sourceRouting on, read next
        // to the runner that processes what they queue.
        services.AddOptions<Intake.IntakeOptions>().Configure(o => configuration.GetSection(Intake.IntakeOptions.SectionName).Bind(o));
        services.TryAddSingleton<Intake.ChannelPollerHostedService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Intake.ChannelPollerHostedService>(sp => sp.GetRequiredService<Intake.ChannelPollerHostedService>()));
        // And the delivery sweeper (docs/11 §13.6), for deliveries whose message was lost.
        services.TryAddSingleton<DeliverySweeperHostedService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DeliverySweeperHostedService>(sp => sp.GetRequiredService<DeliverySweeperHostedService>()));

        services.TryAddSingleton<PipelineBlobs>();
        services.TryAddSingleton(DocumentValidator.Default);
        // P0: every tenant has adopted the standard templates, so a document's definition is its legacy type's template.
        services.TryAddSingleton<IEffectiveConfigResolver>(new StandardTemplateConfigResolver(DocProc.Llm.Schema.DefinitionSchemaCompiler.Options));
        services.TryAddScoped<DocumentTimeline>();
        services.TryAddScoped<DocumentFlagWriter>();
        services.TryAddScoped<FailureRecorder>();
        services.TryAddScoped<Routing.SourceRouting>();
        services.TryAddScoped<Layouts.LayoutIdentification>();
        services.TryAddScoped<Layouts.LayoutExtractor>();
        services.TryAddScoped<Layouts.LayoutReviewGate>();
        services.TryAddScoped<Layouts.LayoutPrefiller>();

        services.TryAddScoped<IIngestionHandler, IngestHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStageHandler, NormalizeHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStageHandler, SplitHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStageHandler, ClassifyHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStageHandler, MatchHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStageHandler, ExtractHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStageHandler, ValidateHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStageHandler, PublishHandler>());

        services.TryAddSingleton<EInvoiceDocumentExtractor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentExtractor, EInvoiceDocumentExtractor>(sp => sp.GetRequiredService<EInvoiceDocumentExtractor>()));
        return services;
    }

    /// <summary>
    /// Everything a host needs to process documents, on top of persistence: PDF, classification and splitting
    /// (<c>DocumentProcessing:*</c>), e-invoice parsing, the LLM gateway with its EF-backed ports and the vendor
    /// adapters, blob storage and the secret store (unless the host registered them already), and the pipeline itself. The runner is
    /// registered when <c>Pipeline:RunInProcess</c> is true; unset, it defaults to <paramref name="runInProcessDefault"/>
    /// or, when that is null too, to "not in Production". Without the runner only the queue is registered.
    /// </summary>
    public static IHostApplicationBuilder AddDocProcProcessingStack(this IHostApplicationBuilder builder, bool? runInProcessDefault = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.AddDocProcDocumentProcessing(configuration);
        services.AddEInvoiceParsing();
        services.AddDocProcLlm(configuration, builder.Environment.ContentRootPath);
        services.AddDocProcLlmPersistence();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILlmProviderFactory, AnthropicProviderFactory>());
        // The host's clock drives the per-request timeout, so the tests' fake clock bounds those calls too.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILlmProviderFactory, OpenAiCompatibleProviderFactory>(
            sp => new OpenAiCompatibleProviderFactory(clock: sp.GetRequiredService<TimeProvider>())));

        if (!services.Any(d => d.ServiceType == typeof(IContainerBlobStore)))
        {
            services.AddDocProcBlobs(configuration, builder.Environment);
        }

        // The credential store reads the BYOK and platform keys the API wrote to the secret store (docs/08 "Key
        // handling"), so the worker needs the same store: the file store under Secrets:Path unless the host has one.
        if (!services.Any(d => d.ServiceType == typeof(ISecretStore)))
        {
            services.AddDocProcSecrets(configuration, builder.Environment);
        }

        // The mailbox settings: the pollers read them, and the channel API checks a mailbox's host against them.
        services.AddOptions<DocProc.Infrastructure.Mail.MailOptions>().Configure(o => configuration.GetSection(DocProc.Infrastructure.Mail.MailOptions.SectionName).Bind(o));

        var runInProcess = configuration.GetValue<bool?>(PipelineOptions.RunInProcessKey)
                           ?? runInProcessDefault
                           ?? !builder.Environment.IsProduction();
        if (runInProcess)
        {
            services.AddDocProcPipeline(configuration);
        }
        else
        {
            services.AddDocProcPipelineQueue();
        }

        // The publish sweeper demo's crash between commit and enqueue (docs/09); never outside Development.
        if (builder.Environment.IsDevelopment() && configuration[PipelineOptions.CrashBeforePublishEnqueueMarkerKey] is { Length: > 0 } marker)
        {
            services.Replace(ServiceDescriptor.Singleton<IPipelineQueue>(sp => new CrashBeforePublishEnqueueQueue(
                sp.GetRequiredService<InProcessPipelineQueue>(),
                marker,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CrashBeforePublishEnqueueQueue>>())));
        }

        return builder;
    }
}
