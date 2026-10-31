using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;

namespace DocProc.Domain.Validation.Rules;

/// <summary>DATE_IMPLAUSIBLE for issue dates in the future or too old, due dates before issue, inverted service periods.</summary>
public sealed class DatePlausibilityRule : IInvoiceRule
{
    public IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context)
    {
        var today = context.Today;
        var opts = context.Options;

        if (invoice.IssueDate.HasValue)
        {
            var issue = invoice.IssueDate.Value;
            if (issue > today.AddDays(opts.MaxFutureDays))
            {
                yield return FlagCodes.Create(FlagCodes.DateImplausible, $"Issue date {issue:yyyy-MM-dd} is in the future.", "issueDate", FlagParams.Of((FlagParams.Reason, "future"), ("issueDate", FlagParams.Date(issue))));
            }
            else if (issue < today.AddDays(-opts.MaxDocumentAgeDays))
            {
                yield return FlagCodes.Create(FlagCodes.DateImplausible, $"Issue date {issue:yyyy-MM-dd} is more than {opts.MaxDocumentAgeDays} days old.", "issueDate", FlagParams.Of((FlagParams.Reason, "tooOld"), ("issueDate", FlagParams.Date(issue)), ("maxAgeDays", FlagParams.Number(opts.MaxDocumentAgeDays))));
            }

            if (invoice.DueDate.HasValue && invoice.DueDate.Value < issue)
            {
                yield return FlagCodes.Create(FlagCodes.DateImplausible, $"Due date {invoice.DueDate.Value:yyyy-MM-dd} is before the issue date {issue:yyyy-MM-dd}.", "dueDate", FlagParams.Of((FlagParams.Reason, "dueBeforeIssue"), ("dueDate", FlagParams.Date(invoice.DueDate.Value)), ("issueDate", FlagParams.Date(issue))));
            }
        }

        if (invoice.ServicePeriod.HasValue && !invoice.ServicePeriod.Value!.IsValid)
        {
            yield return FlagCodes.Create(FlagCodes.DateImplausible, "Service period ends before it starts.", "servicePeriod", FlagParams.Of((FlagParams.Reason, "servicePeriodInverted")));
        }
    }
}
