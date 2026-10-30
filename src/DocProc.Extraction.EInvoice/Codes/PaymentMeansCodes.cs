using DocProc.Domain.Common;

namespace DocProc.Extraction.EInvoice.Codes;

/// <summary>UNCL 4461 payment means codes (BT-81) to the canonical <see cref="PaymentMeans"/>.</summary>
public static class PaymentMeansCodes
{
    public static PaymentMeans? ToPaymentMeans(string? code) => code?.Trim() switch
    {
        null => null,
        "10" => PaymentMeans.Cash,
        "30" or "31" or "42" or "58" => PaymentMeans.BankTransfer,
        "49" or "59" => PaymentMeans.DirectDebit,
        "48" or "54" or "55" => PaymentMeans.Card,
        _ => PaymentMeans.Other,
    };
}
