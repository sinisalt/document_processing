using DocProc.Application.Abstractions;
using DocProc.Extraction.EInvoice;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.EInvoice.Tests;

public class ServiceRegistrationTests
{
    [Fact]
    public void AddEInvoiceParsing_registers_a_singleton_parser_behind_the_application_interface()
    {
        var services = new ServiceCollection().AddEInvoiceParsing().AddEInvoiceParsing();
        using var provider = services.BuildServiceProvider();

        var parser = provider.GetRequiredService<IEInvoiceParser>();

        parser.ShouldBeOfType<EInvoiceParser>();
        provider.GetRequiredService<IEInvoiceParser>().ShouldBeSameAs(parser);
        services.Count(d => d.ServiceType == typeof(IEInvoiceParser)).ShouldBe(1);
        parser.Detect(Fixtures.Bytes(Fixtures.PeppolInvoice)).ShouldNotBeNull().Flavour.ShouldBe(EInvoiceFlavour.Ubl);
    }
}
