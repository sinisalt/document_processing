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

    [Fact]
    public void AddEInvoiceParsing_applies_the_given_resource_limits()
    {
        var xml = Fixtures.Bytes(Fixtures.PeppolInvoice);
        var services = new ServiceCollection().AddEInvoiceParsing(new EInvoiceParserOptions { MaxDocumentBytes = 16 });
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<EInvoiceParserOptions>().MaxDocumentBytes.ShouldBe(16);
        provider.GetRequiredService<IEInvoiceParser>().Detect(xml).ShouldBeNull();
    }
}
