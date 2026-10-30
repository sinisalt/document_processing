using DocProc.Llm.OpenAi;
using DocProc.Llm.Providers;
using DocProc.Pipeline.Stages;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>What <c>AddDocProcProcessingStack</c> wires beyond the handlers the other tests drive.</summary>
public class ProcessingStackCompositionTests
{
    [Fact]
    public async Task The_openai_adapter_times_its_requests_on_the_host_clock()
    {
        await using var host = await PipelineTestHost.StartAsync();

        var factory = host.Services.GetServices<ILlmProviderFactory>().OfType<OpenAiCompatibleProviderFactory>().ToList().ShouldHaveSingleItem();
        factory.Clock.ShouldBeSameAs(host.Clock, "the per-request timeout waits on the host's TimeProvider, not on the system clock");
    }

    [Fact]
    public async Task The_ingest_commit_gate_is_one_per_process_and_idle_between_messages()
    {
        await using var host = await PipelineTestHost.StartAsync();

        using var scope = host.CreateTenantScope(PipelineTestHost.TenantA);
        var gate = scope.ServiceProvider.GetRequiredService<IngestCommitGate>();
        gate.ShouldBeSameAs(host.Services.GetRequiredService<IngestCommitGate>(), "every ingest handler serialises through the same gate");
        gate.ActiveKeys.ShouldBe(0);
    }
}
