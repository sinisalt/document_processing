namespace DocProc.Domain.Common;

public sealed record Address(
    string? Line1 = null,
    string? Line2 = null,
    string? PostalCode = null,
    string? City = null,
    string? Region = null,
    string? CountryCode = null);

/// <summary>A legal party on a document: supplier, customer, buyer, recipient.</summary>
public sealed record Party(
    string? Name,
    string? VatId = null,
    string? TaxId = null,
    string? RegistrationNumber = null,
    Address? Address = null,
    string? Email = null,
    string? Phone = null,
    string? Iban = null,
    string? Bic = null)
{
    /// <summary>VAT id with whitespace, dots and dashes removed, upper-cased; used for matching.</summary>
    public string? NormalizedVatId => Identifiers.NormalizeCompact(VatId);

    public string? NormalizedIban => Identifiers.NormalizeCompact(Iban);
}
