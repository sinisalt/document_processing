using DocProc.Application.Abstractions;
using DocProc.Llm.Extraction;
using DocProc.Llm.Gateway;
using DocProc.Llm.Prompts;
using DocProc.Llm.Providers;
using DocProc.Llm.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Llm.Tests;

public class LlmServiceCollectionTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "docproc-replay-" + Guid.NewGuid().ToString("N"));

    private static readonly LlmProviderConfig Platform = new()
    {
        Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        Scope = ConfigScope.Platform,
        Kind = LlmProviderKind.Replay,
        Auth = new LlmAuth(LlmAuthType.ApiKey, "llm-platform-eu"),
        Models = new Dictionary<LlmStage, string> { [LlmStage.Extract] = "claude-opus-5-5", [LlmStage.Classify] = "claude-haiku-5-5", [LlmStage.Split] = "claude-haiku-5-5" },
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static ServiceProvider Build(IDictionary<string, string?> settings, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ILlmPolicyStore, PolicyStore>();
        services.AddSingleton<ILlmCredentialStore, CredentialStore>();
        services.AddSingleton<UsageRecorder>();
        services.AddSingleton<ILlmUsageRecorder>(sp => sp.GetRequiredService<UsageRecorder>());
        configure?.Invoke(services);
        services.AddDocProcLlm(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static LlmRequest Request() => new()
    {
        Stage = LlmStage.Extract,
        Model = LlmRequestDefaults.ModelResolvedByGateway,
        Prompt = PromptCatalogue.Load().ExtractInvoice with { Task = "task" },
        OutputSchema = CanonicalSchemas.InvoiceExtraction,
    };

    [Fact]
    public void Registers_the_gateway_the_prompts_the_prices_and_the_stage_implementations()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Llm:Extraction:MaxOutputTokens"] = "4096", ["Llm:Extraction:Effort"] = "High" });

        provider.GetRequiredService<ILlmGateway>().ShouldBeOfType<LlmGateway>();
        provider.GetRequiredService<PromptCatalogue>().ExtractInvoice.Id.ShouldBe(PromptIds.ExtractInvoice);
        provider.GetRequiredService<PriceTable>().ShouldBeSameAs(PriceTable.Default);
        provider.GetRequiredService<LlmExtractionOptions>().ShouldSatisfyAllConditions(
            o => o.MaxOutputTokens.ShouldBe(4096),
            o => o.Effort.ShouldBe(ReasoningEffort.High));
        provider.GetServices<IDocumentExtractor>().ShouldHaveSingleItem().ShouldBeOfType<LlmInvoiceExtractor>();
        provider.GetRequiredService<IDocumentClassifier>().ShouldBeOfType<LlmDocumentClassifier>();
        provider.GetRequiredService<IDocumentSplitter>().ShouldBeOfType<LlmDocumentSplitter>();
        provider.GetServices<ILlmProviderFactory>().ShouldBeEmpty("no replay directory and no adapters were registered");
    }

    [Fact]
    public void A_rule_based_classifier_registered_first_wins_over_the_llm_one()
    {
        using var provider = Build(new Dictionary<string, string?>(), services => services.AddSingleton<IDocumentClassifier, RuleClassifier>());

        provider.GetRequiredService<IDocumentClassifier>().ShouldBeOfType<RuleClassifier>();
        provider.GetRequiredService<LlmDocumentClassifier>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Scripted_replay_serves_the_gateway_through_the_real_resolution_and_usage_path()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Llm:Replay:Scripted"] = "true" });
        var scripted = provider.GetRequiredService<ScriptedLlmProvider>().Enqueue("{\"kind\":\"Invoice\"}", usage: new LlmUsage(10, 5));

        var result = await provider.GetRequiredService<ILlmGateway>().CompleteAsync(TenantId, Request(), CancellationToken.None);

        result.Json.ShouldBe("{\"kind\":\"Invoice\"}");
        result.Model.ShouldBe("claude-opus-5-5");
        scripted.Requests.Single().Model.ShouldBe("claude-opus-5-5");
        provider.GetRequiredService<UsageRecorder>().Records.ShouldHaveSingleItem().PromptVersion.ShouldBe(PromptCatalogue.Load().ExtractInvoice.CacheKey);
    }

    [Fact]
    public async Task Replay_directory_registers_the_file_store_and_misses_throw_unless_recording()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Llm:Replay:Directory"] = _directory });

        provider.GetRequiredService<IReplayStore>().ShouldBeOfType<FileReplayStore>().Directory.ShouldBe(Path.GetFullPath(_directory));
        provider.GetServices<ILlmProviderFactory>().ShouldHaveSingleItem().ShouldBeOfType<ReplayLlmProviderFactory>();
        var ex = await Should.ThrowAsync<LlmException>(() => provider.GetRequiredService<ILlmGateway>().CompleteAsync(TenantId, Request(), CancellationToken.None));
        ex.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
    }

    [Fact]
    public async Task Record_mode_wraps_every_adapter_regardless_of_registration_order_and_writes_misses()
    {
        var scripted = new ScriptedLlmProvider().Enqueue("{\"kind\":\"Invoice\"}");
        using var provider = Build(
            new Dictionary<string, string?> { ["Llm:Replay:Directory"] = _directory, ["Llm:Replay:Record"] = "true" },
            services => services.AddSingleton<ILlmProviderFactory>(new ScriptedLlmProviderFactory(scripted)));
        var gateway = provider.GetRequiredService<ILlmGateway>();

        var first = await gateway.CompleteAsync(TenantId, Request(), CancellationToken.None);
        var second = await gateway.CompleteAsync(TenantId, Request(), CancellationToken.None);

        first.Json.ShouldBe("{\"kind\":\"Invoice\"}");
        second.Json.ShouldBe(first.Json);
        scripted.Requests.Count.ShouldBe(1, "the second call replays the recorded fixture");
        Directory.GetFiles(_directory, "*.json").ShouldHaveSingleItem();
    }

    [Fact]
    public void Record_mode_without_a_directory_is_a_configuration_error()
    {
        Should.Throw<InvalidOperationException>(() => Build(new Dictionary<string, string?> { ["Llm:Replay:Record"] = "true" }))
            .Message.ShouldContain("Llm:Replay:Directory");
    }

    private sealed class PolicyStore : ILlmPolicyStore
    {
        public Task<TenantProviderSetup> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantProviderSetup(null, new Dictionary<Guid, LlmProviderConfig>(), Platform, 0m));
    }

    private sealed class CredentialStore : ILlmCredentialStore
    {
        public Task<LlmCredential> GetAsync(LlmProviderConfig config, CancellationToken cancellationToken) => Task.FromResult(LlmCredential.None);
    }

    private sealed class UsageRecorder : ILlmUsageRecorder
    {
        public List<LlmUsageRecord> Records { get; } = [];

        public Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class RuleClassifier : IDocumentClassifier
    {
        public Task<ClassificationOutcome> ClassifyAsync(Guid tenantId, Domain.Text.TextLayer? textLayer, ReadOnlyMemory<byte>? firstPagePng, CancellationToken cancellationToken) =>
            Task.FromResult(new ClassificationOutcome(Domain.Documents.DocumentType.Invoice, 1, Domain.Documents.TypeSource.Rule));
    }
}
