using DocProc.Pipeline.Queue;
using DocProc.Pipeline.Runner;

namespace DocProc.Pipeline.Stages;

/// <summary>
/// Serialises the decide-and-commit half of stage 1 per tenant within one process: duplicate detection reads the
/// other ingestions' items and the documents they own, so two ingestions of one tenant must not decide at the same
/// time or both could own a document for the same bytes (docs/03 "Implementation"). Unpacking and blob writes stay
/// outside the gate; only the queries and the final <c>SaveChanges</c> run one ingestion at a time. Across processes
/// the content-addressed rows and the strictly-earlier rule narrow the same race; a unique index closes it later.
/// </summary>
internal sealed class IngestCommitGate
{
    private readonly KeyedWorkGate _gate = new();

    /// <summary>Keys with a holder or a waiter right now (tests).</summary>
    public int ActiveKeys => _gate.ActiveKeys;

    public Task<IDisposable> EnterAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _gate.EnterAsync(new PipelineWorkKey(PipelineWorkKind.Tenant, tenantId), cancellationToken);
}
