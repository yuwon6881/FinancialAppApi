using System.Text.Json.Nodes;
using FinancialAppApi.Database;
using Microsoft.EntityFrameworkCore;

namespace FinancialAppApi.Services.AI.Tools;

// Subscriptions, bills, and instalments. The next due date is derived from the occurrence ledger
// (INVARIANTS LOAN-11) -- the stored NextDueDate column is a legacy cache that is often blank.
public sealed class GetRecurringTool : IAiTool
{
    private static readonly string[] Statuses = ["all", "active", "paused"];

    private readonly AppDbContext _context;
    private readonly RecurringOccurrenceLedgerService _occurrences;

    public GetRecurringTool(AppDbContext context, RecurringOccurrenceLedgerService occurrences)
    {
        _context = context;
        _occurrences = occurrences;
    }

    public string Name => "get_recurring";

    public string Description =>
        "List the user's recurring payments (subscriptions, bills, instalments) with id, amount, frequency, bucket, " +
        "manual or auto-deduct mode, active state, next due date, reminder settings, and any linked loan; the next " +
        "upcoming bills; and the total recurring cost normalised per month and per year. Use for subscription, bill, " +
        "and 'what's due next' questions, and to find a recurring id before proposing a change.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("status", AiToolSchema.Enum("Which payments to list. Default all.", Statuses))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public IReadOnlySet<string> AmountProperties { get; } = new HashSet<string>(StringComparer.Ordinal) { "monthly" };

    public string ProgressLabel(AiToolArgs args) => "Checking your recurring payments";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var status = args.OptionalEnum("status", Statuses) ?? "all";
        var payments = await (from payment in _context.RecurringPayments.AsNoTracking()
                join loan in _context.Loans.AsNoTracking() on payment.Id equals loan.RecurringPaymentId into linkedLoans
                from linkedLoan in linkedLoans.DefaultIfEmpty()
                orderby payment.Name
                select new { Payment = payment, LoanId = linkedLoan == null ? null : linkedLoan.Id, LoanName = linkedLoan == null ? null : linkedLoan.Name })
            .Take(100)
            .ToListAsync(cancellationToken);
        var nextPending = payments.Count == 0
            ? new Dictionary<string, Models.RecurringPaymentOccurrence>()
            : await _occurrences.GetNextPendingAsync(
                payments.Select(entry => entry.Payment).ToList(), context.Today, includeFrom: true, cancellationToken);

        var rows = payments.Select(entry => new AiAssistantService.AiRecurringRow(
            entry.Payment.Id, entry.Payment.Name, entry.Payment.Amount, entry.Payment.Category, entry.Payment.LedgerCategory,
            entry.Payment.StartDate, entry.Payment.EndDate, entry.Payment.DueDate, entry.Payment.Active, entry.Payment.Frequency,
            nextPending.TryGetValue(entry.Payment.Id, out var occurrence) ? occurrence.OccurrenceDate.ToString("yyyy-MM-dd") : "",
            entry.Payment.PushReminderEnabled, entry.Payment.PushReminderMode, entry.Payment.PushReminderLeadDays,
            entry.Payment.PaymentMode, entry.LoanId, entry.LoanName)).ToList();
        var listed = rows.Where(row => status == "all" || (status == "active") == row.Active).ToList();

        context.Evidence.RecordAll(AiEvidenceLedger.Recurring, listed.Select(row => row.Id));
        context.Evidence.RecordAll(AiEvidenceLedger.Loan, listed.Select(row => row.LinkedLoanId));
        return AiToolResult.Of(new
        {
            payments = listed.Select(row => new
            {
                id = row.Id,
                name = row.Name,
                amount = Math.Abs(row.Amount),
                frequency = row.Frequency,
                category = row.Category,
                ledgerCategory = row.LedgerCategory,
                paymentMode = row.PaymentMode,
                active = row.Active,
                nextDueDate = row.NextDueDate.Length > 0 ? row.NextDueDate : null,
                startDate = row.StartDate,
                endDate = row.EndDate,
                reminder = new { enabled = row.PushReminderEnabled, mode = row.PushReminderMode, leadDays = row.PushReminderLeadDays },
                // A loan-linked payment is protected: it cannot be deleted from chat.
                linkedLoanId = row.LinkedLoanId,
                linkedLoanName = row.LinkedLoanName
            }).ToList(),
            upcoming = rows
                .Where(row => row.Active && row.NextDueDate.Length > 0)
                .OrderBy(row => row.NextDueDate, StringComparer.Ordinal)
                .Take(5)
                .Select(row => new { id = row.Id, name = row.Name, nextDueDate = row.NextDueDate, amount = Math.Abs(row.Amount), frequency = row.Frequency })
                .ToList(),
            costSummary = AiAssistantService.BuildRecurringCostSummary(rows)
        });
    }
}
