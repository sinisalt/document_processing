using DocProc.Domain.Common;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation.Rules;

/// <summary>
/// CUSTOMER_MISMATCH when the customer block does not look like the tenant: VAT id differs when both are known,
/// or too few tokens of the tenant's legal name (or an alias) appear in the printed customer name.
/// </summary>
public sealed class CustomerMatchRule : IInvoiceRule
{
    public IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context)
    {
        if (context.Tenant is null || !invoice.Customer.HasValue)
        {
            yield break;
        }

        var tenant = context.Tenant;
        var customer = invoice.Customer.Value!;

        var tenantVat = Identifiers.NormalizeCompact(tenant.VatId);
        var customerVat = customer.NormalizedVatId;
        if (tenantVat is not null && customerVat is not null)
        {
            if (string.Equals(tenantVat, customerVat, StringComparison.Ordinal))
            {
                yield break;
            }

            yield return FlagCodes.Create(
                FlagCodes.CustomerMismatch,
                $"Customer VAT id {customer.VatId} does not match the tenant's {tenant.VatId}.",
                "customer.vatId",
                FlagParams.Of((FlagParams.Reason, "vatId"), ("customerVatId", customer.VatId), ("tenantVatId", tenant.VatId)));
            yield break;
        }

        if (string.IsNullOrWhiteSpace(customer.Name))
        {
            yield return FlagCodes.Create(FlagCodes.CustomerMismatch, "The customer block has no name to compare with the tenant.", "customer.name", FlagParams.Of((FlagParams.Reason, "noName")));
            yield break;
        }

        var names = new List<string> { tenant.LegalName };
        if (tenant.NameAliases is not null)
        {
            names.AddRange(tenant.NameAliases);
        }

        if (!names.Any(n => NameMatches(n, customer.Name, context.Options.CustomerNameMatchRatio)))
        {
            yield return FlagCodes.Create(
                FlagCodes.CustomerMismatch,
                $"Customer '{customer.Name}' does not look like the tenant '{tenant.LegalName}'.",
                "customer.name",
                FlagParams.Of((FlagParams.Reason, "name"), ("customerName", customer.Name), ("tenantName", tenant.LegalName)));
        }
    }

    internal static bool NameMatches(string expected, string printed, double minRatio)
    {
        var expectedTokens = Identifiers.NormalizeName(expected).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToArray();
        if (expectedTokens.Length == 0)
        {
            return false;
        }

        var printedTokens = Identifiers.NormalizeName(printed).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var hits = expectedTokens.Count(printedTokens.Contains);
        return (double)hits / expectedTokens.Length >= minRatio;
    }
}
