using DocProc.Domain.Documents;
using DocProc.Llm.Gateway;
using DocProc.Llm.Providers;
using DocProc.Llm.Schema;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Llm.Tests;

public class LlmGatewayTests
{
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly LlmProviderConfig Platform = new()
    {
        Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        Scope = ConfigScope.Platform,
        Kind = LlmProviderKind.Replay,
        Auth = new LlmAuth(LlmAuthType.ApiKey, "llm-platform-eu"),
        Models = new Dictionary<LlmStage, string> { [LlmStage.Extract] = "claude-opus-5-5", [LlmStage.Classify] = "claude-haiku-5-5" },
        Limits = new LlmLimits(MonthlySpendCapUsd: 100m),
    };

    private static LlmRequest Request(LlmStage stage = LlmStage.Extract) => new()
    {
        Stage = stage,
        Model = "ignored-by-gateway",
        Prompt = new PromptBundle { Id = "extract-invoice", Version = "3", System = "You extract invoices.", Task = "Tenant: Gasthaus zur Post" },
        OutputSchema = CanonicalSchemas.InvoiceExtraction,
        DocumentId = Guid.Parse("dddddddd-0000-0000-0000-000000000001"),
        Document = new DocumentInput { Pdf = new byte[] { 0x25, 0x50, 0x44, 0x46 }, PageTexts = ["Rechnung Nr. 42"] },
    };

    private sealed class Fixture
    {
        public InMemoryReplayStore Store { get; } = new();
        public List<LlmUsageRecord> Records { get; } = [];
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        public decimal Spent { get; set; }
        public TenantLlmPolicy? Policy { get; set; }
        public List<LlmProviderConfig> TenantConfigs { get; } = [];
        public List<LlmProviderConfig> RequestedCredentials { get; } = [];

        public LlmGateway Build() => new(
            new PolicyStore(this),
            new CredentialStore(this),
            [new ReplayLlmProviderFactory(Store)],
            new Recorder(this),
            PriceTable.Default,
            Clock);

        private sealed class PolicyStore(Fixture f) : ILlmPolicyStore
        {
            public Task<TenantProviderSetup> GetAsync(Guid tenantId, CancellationToken ct) =>
                Task.FromResult(new TenantProviderSetup(f.Policy, f.TenantConfigs.ToDictionary(c => c.Id), Platform, f.Spent));
        }

        private sealed class CredentialStore(Fixture f) : ILlmCredentialStore
        {
            public Task<LlmCredential> GetAsync(LlmProviderConfig config, CancellationToken ct)
            {
                f.RequestedCredentials.Add(config);
                return Task.FromResult(new LlmCredential("sk-test"));
            }
        }

        private sealed class Recorder(Fixture f) : ILlmUsageRecorder
        {
            public Task RecordAsync(LlmUsageRecord record, CancellationToken ct)
            {
                f.Records.Add(record);
                return Task.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task Resolves_model_fetches_credential_calls_provider_and_records_usage_with_cost()
    {
        var f = new Fixture();
        var request = Request();
        var recorded = new LlmResult
        {
            Json = "{}",
            Usage = new LlmUsage(InputTokens: 10_000, OutputTokens: 1_000, CacheReadTokens: 2_000),
            Model = "claude-opus-5-5",
            StopReason = StopReasons.EndTurn,
            ProviderRequestId = "req_123",
        };
        f.Store.Put(RequestHasher.Hash(request with { Model = "claude-opus-5-5" }), recorded);

        var result = await f.Build().CompleteAsync(TenantId, request, CancellationToken.None);

        result.Json.ShouldBe("{}");
        result.Provider.ShouldBe(LlmProviderKind.Replay, "the resolved provider kind travels on the result for cost attribution");
        result.KeyOwner.ShouldBe(KeyOwner.Platform);
        f.RequestedCredentials.ShouldHaveSingleItem().ShouldBe(Platform);
        var row = f.Records.ShouldHaveSingleItem();
        row.TenantId.ShouldBe(TenantId);
        row.DocumentId.ShouldBe(request.DocumentId);
        row.Stage.ShouldBe(LlmStage.Extract);
        row.ConfigId.ShouldBe(Platform.Id);
        row.KeyOwner.ShouldBe(KeyOwner.Platform);
        row.Provider.ShouldBe(LlmProviderKind.Replay);
        row.Model.ShouldBe("claude-opus-5-5");
        row.PromptVersion.ShouldBe("extract-invoice@3");
        row.SchemaVersion.ShouldBe("invoice_extraction@1");
        row.Usage.ShouldBe(recorded.Usage);
        // 10k × $4 + 1k × $20 + 2k × $0.40 per million = 0.04 + 0.02 + 0.0008
        row.EstimatedCostUsd.ShouldBe(0.0608m);
        row.ProviderRequestId.ShouldBe("req_123");
        row.At.ShouldBe(f.Clock.GetUtcNow());
        row.Error.ShouldBeNull();
        row.RequestHash.Length.ShouldBe(64);
    }

    [Fact]
    public async Task Spend_cap_reached_is_a_quota_error_and_the_provider_is_not_called()
    {
        var f = new Fixture { Spent = 100m };

        var ex = await Should.ThrowAsync<LlmException>(() => f.Build().CompleteAsync(TenantId, Request(), CancellationToken.None));

        ex.Kind.ShouldBe(LlmErrorKind.Quota);
        ex.BlocksTenant.ShouldBeTrue();
        f.RequestedCredentials.ShouldBeEmpty();
        f.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Provider_errors_are_recorded_and_rethrown()
    {
        var f = new Fixture(); // empty replay store: every call misses

        var ex = await Should.ThrowAsync<LlmException>(() => f.Build().CompleteAsync(TenantId, Request(), CancellationToken.None));

        ex.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
        var row = f.Records.ShouldHaveSingleItem();
        row.Error.ShouldBe(LlmErrorKind.InvalidRequest);
        row.Usage.ShouldBe(LlmUsage.Zero);
        row.StopReason.ShouldBe("error");
    }

    [Fact]
    public async Task Unknown_provider_kind_is_unavailable()
    {
        var f = new Fixture();
        var openAi = Platform with { Id = Guid.NewGuid(), Scope = ConfigScope.Tenant, TenantId = TenantId, Kind = LlmProviderKind.OpenAi };
        f.TenantConfigs.Add(openAi);
        f.Policy = new TenantLlmPolicy { TenantId = TenantId, DefaultConfigId = openAi.Id };

        var ex = await Should.ThrowAsync<LlmException>(() => f.Build().CompleteAsync(TenantId, Request(), CancellationToken.None));

        ex.Kind.ShouldBe(LlmErrorKind.Unavailable);
        ex.Message.ShouldContain("OpenAi");
    }

    [Fact]
    public void Request_hash_changes_with_anything_the_provider_would_see_and_nothing_else()
    {
        var request = Request();
        var baseline = RequestHasher.Hash(request);

        RequestHasher.Hash(request with { DocumentId = Guid.NewGuid() }).ShouldBe(baseline);
        RequestHasher.Hash(request with { Model = "other" }).ShouldNotBe(baseline);
        RequestHasher.Hash(request with { Prompt = request.Prompt with { Task = "Tenant: Pizzeria" } }).ShouldNotBe(baseline);
        RequestHasher.Hash(request with { Prompt = request.Prompt with { Version = "4" } }).ShouldNotBe(baseline);
        RequestHasher.Hash(request with { Document = request.Document! with { PageTexts = ["Rechnung Nr. 43"] } }).ShouldNotBe(baseline);
        RequestHasher.Hash(request with { OutputSchema = CanonicalSchemas.Classification }).ShouldNotBe(baseline);
        RequestHasher.Hash(request with { Options = new LlmOptions { Effort = ReasoningEffort.High } }).ShouldNotBe(baseline);
    }

    [Fact]
    public void Price_table_matches_deployment_names_that_embed_the_model_and_halves_batch_cost()
    {
        var usage = new LlmUsage(InputTokens: 1_000_000, OutputTokens: 0);

        PriceTable.Default.Estimate("docproc-claude-haiku-5-5-eu", usage).ShouldBe(0.10m);
        PriceTable.Default.Estimate("claude-sonnet-5-5", usage, batch: true).ShouldBe(1m);
        PriceTable.Default.Estimate("gpt-unknown", usage).ShouldBeNull();
    }

    [Fact]
    public async Task Replay_batches_complete_immediately_with_per_item_outcomes()
    {
        var store = new InMemoryReplayStore();
        var provider = new ReplayLlmProvider(store);
        var hit = Request(LlmStage.Classify) with { Model = "m" };
        var miss = hit with { Prompt = hit.Prompt with { Task = "other" } };
        store.Put(RequestHasher.Hash(hit), new LlmResult { Json = "{\"documentType\":\"Invoice\"}", Usage = LlmUsage.Zero, Model = "m", StopReason = StopReasons.EndTurn });

        var handle = await provider.SubmitBatchAsync([hit, miss], CancellationToken.None);
        var batch = await provider.GetBatchAsync(handle, CancellationToken.None);

        batch.Status.ShouldBe(BatchStatus.Completed);
        batch.Items[0].Result.ShouldNotBeNull();
        batch.Items[1].Error!.Kind.ShouldBe(LlmErrorKind.InvalidRequest);
    }
}
