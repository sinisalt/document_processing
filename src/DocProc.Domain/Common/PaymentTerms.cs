namespace DocProc.Domain.Common;

public enum PaymentMeans
{
    BankTransfer,
    DirectDebit,
    Card,
    Cash,
    Other,
}

public sealed record PaymentTerms(
    DateOnly? DueDate = null,
    decimal? DiscountPercent = null,
    DateOnly? DiscountUntil = null,
    PaymentMeans? PaymentMeans = null,
    string? PaymentReference = null,
    string? Iban = null,
    string? Bic = null,
    string? RawText = null);

public sealed record DatePeriod(DateOnly From, DateOnly To)
{
    public bool IsValid => To >= From;
}
