using System.Text.Json.Nodes;
using DocProc.Application.Outputs;
using DocProc.Infrastructure.Outputs;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests.Delivery;

/// <summary>
/// The lookup data one render receives (docs/11 §13.1): only the tables the expression names, each up to
/// <see cref="OutputRenderService.MaxLookupEntries"/>, and together at most the per-render bound
/// (<see cref="RenderLimits.MaxLookupEntriesPerRender"/> entries, <see cref="RenderLimits.MaxLookupCharactersPerRender"/>
/// characters): a render naming more fails for good, before anything is sent to the renderer.
/// </summary>
public sealed class OutputRenderServiceTests
{
    private static readonly Guid TenantA = PipelineTestHost.TenantA;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_lookup_tables_one_render_receives_are_bounded_in_total()
    {
        await using var host = await PipelineTestHost.StartAsync();
        await host.WithDbAsync(async db =>
        {
            foreach (var key in new[] { "t1", "t2", "t3" })
            {
                var table = new LookupTable { Id = Guid.CreateVersion7(), TenantId = TenantA, Key = key, CreatedAt = host.Clock.GetUtcNow(), UpdatedAt = host.Clock.GetUtcNow() };
                for (var i = 0; i < 6; i++)
                {
                    table.Entries.Add(new LookupEntry { TenantId = TenantA, From = $"k{i}", To = new string('v', 10) });
                }

                db.LookupTables.Add(table);
            }

            await db.SaveChangesAsync();
            return 0;
        });

        using var scope = host.CreateTenantScope(TenantA);
        var db = scope.ServiceProvider.GetRequiredService<DocProcDbContext>();
        var renderer = scope.ServiceProvider.GetRequiredService<IOutputRenderer>();
        var input = new JsonObject();
        var today = new DateOnly(2026, 11, 3);

        // 12 entries in two tables fit a bound of 12; a third table passes it.
        var service = new OutputRenderService(db, renderer) { EntryBudget = 12 };
        var two = await service.RenderAsync(OutputEngines.Jsonata, """$table("t1", "k1") & $table("t2", "k2")""", null, input, null, today, Ct);
        two.Error.ShouldBeNull();
        two.Output!.GetValue<string>().ShouldBe(new string('v', 20));

        var three = await service.RenderAsync(OutputEngines.Jsonata, """$table("t1", "k1") & $table("t2", "k2") & $table("t3", "k3")""", null, input, null, today, Ct);
        three.Error.ShouldNotBeNull();
        three.Error.Kind.ShouldBe(RenderErrorKinds.Function);
        three.Error.Code.ShouldBe("LOOKUPS");
        three.Error.IsPermanent.ShouldBeTrue();
        three.Error.Message.ShouldContain("t1, t2, t3");

        // The characters count too: 6 entries of 2 + 10 characters per table.
        var characters = new OutputRenderService(db, renderer) { CharacterBudget = 100 };
        (await characters.RenderAsync(OutputEngines.Jsonata, """$table("t1", "k1")""", null, input, null, today, Ct)).Error.ShouldBeNull();
        (await characters.RenderAsync(OutputEngines.Jsonata, """$table("t1", "k1") & $table("t2", "k1")""", null, input, null, today, Ct)).Error!.Code.ShouldBe("LOOKUPS");

        // The defaults are the documented bounds.
        var defaults = new OutputRenderService(db, renderer);
        defaults.EntryBudget.ShouldBe(RenderLimits.MaxLookupEntriesPerRender);
        defaults.CharacterBudget.ShouldBe(RenderLimits.MaxLookupCharactersPerRender);
        (await defaults.RenderAsync(OutputEngines.Jsonata, """$table("t1", "k1") & $table("t2", "k2") & $table("t3", "k3")""", null, input, null, today, Ct)).Error.ShouldBeNull();
    }
}
