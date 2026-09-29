using System.Text.Json.Nodes;
using FinancialAppApi.Models;
using FinancialAppApi.Services.Loans;

namespace FinancialAppApi.Services.AI.Tools;

// Loan terms plus the server replay. A loan stores terms and schedule snapshots, never a balance,
// so every owed/interest figure here comes from LoanService's replay -- and only when the schedule
// is complete. An incomplete schedule has no honest forecast, so none is offered.
public sealed class GetLoansTool : IAiTool
{
    private readonly LoanService _loans;

    public GetLoansTool(LoanService loans) => _loans = loans;

    public string Name => "get_loans";

    public string Description =>
        "List the user's loans, or one loan by id: terms, linked bill, schedule status, and when the schedule is " +
        "complete the replayed amount still owed, scheduled payment, interest paid and scheduled, payoff date, recent " +
        "payment history, and the next planned payments. Read-only; loans cannot be changed from chat.";

    public JsonObject ParametersSchema { get; } = AiToolSchema.Object(
    [
        ("loanId", AiToolSchema.String("One loan's id. Omit to list all loans."))
    ]);

    public AiToolSensitivity Sensitivity => AiToolSensitivity.MasksAmounts;

    public IReadOnlySet<string> AmountProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "openingPrincipal", "annualRatePercent", "amountStillOwed", "scheduledPayment",
        "totalScheduledInterest", "interestPaid", "interest", "balanceAfter", "recordedHistory", "plannedSchedule"
    };

    public string ProgressLabel(AiToolArgs args) => "Checking your loans";

    public async Task<AiToolResult> ExecuteAsync(AiToolArgs args, AiToolContext context, CancellationToken cancellationToken)
    {
        var loanId = args.OptionalString("loanId", 100);
        IReadOnlyList<LoanView> views;
        if (loanId != null)
        {
            var view = await _loans.GetLoanAsync(loanId, cancellationToken)
                ?? throw new AiToolArgumentException($"No loan has id '{loanId}'. Call get_loans without loanId to list them.");
            views = [view];
        }
        else
        {
            views = await _loans.GetLoansAsync(cancellationToken);
        }

        context.Evidence.RecordAll(AiEvidenceLedger.Loan, views.Select(view => view.Loan.Id));
        context.Evidence.RecordAll(AiEvidenceLedger.Recurring, views.Select(view => view.Loan.RecurringPaymentId));
        return AiToolResult.Of(new
        {
            loans = views.Take(20).Select(view => Describe(view, context.SensitiveMode)).ToList(),
            totalLoans = views.Count
        });
    }

    private static Dictionary<string, object?> Describe(LoanView view, bool sensitive)
    {
        var loan = view.Loan;
        var forecastAvailable = loan.ScheduleStatus == LoanScheduleStatus.Complete;
        var row = new Dictionary<string, object?>
        {
            ["id"] = loan.Id,
            ["name"] = loan.Name,
            ["linkedBillId"] = loan.RecurringPaymentId,
            ["linkedBillName"] = view.RecurringPayment?.Name,
            ["trackingStartDate"] = loan.TrackingStartDate.ToString("yyyy-MM-dd"),
            ["interestMethod"] = loan.InterestMethod,
            ["rateBasis"] = loan.RateBasis,
            ["termPeriods"] = loan.TermPeriods,
            ["scheduleStatus"] = loan.ScheduleStatus,
            ["scheduleFrequency"] = loan.ScheduleFrequency,
            ["scheduleDueDay"] = loan.ScheduleDueDay,
            ["scheduleStartDate"] = loan.ScheduleStartDate?.ToString("yyyy-MM-dd"),
            ["forecastAvailable"] = forecastAvailable
        };
        if (sensitive) return row;

        row["openingPrincipal"] = loan.OpeningPrincipal;
        row["annualRatePercent"] = loan.AnnualRatePercent;
        if (!forecastAvailable) return row;

        row["amountStillOwed"] = view.Replay.OutstandingBalance;
        row["scheduledPayment"] = view.Replay.ScheduledPayment;
        row["totalScheduledInterest"] = view.Replay.TotalScheduledInterest;
        row["interestPaid"] = view.Replay.TotalInterestPaid;
        row["payoffDate"] = view.Replay.PayoffDate?.ToString("yyyy-MM-dd");
        row["recordedHistory"] = view.Replay.Payments.TakeLast(12).Select(payment => new
        {
            occurrenceDate = payment.OccurrenceDate.ToString("yyyy-MM-dd"),
            payment = payment.Payment,
            interest = payment.Interest,
            principal = payment.Principal,
            balanceAfter = payment.BalanceAfter
        }).ToList();
        row["plannedSchedule"] = view.Replay.FutureSchedule.Take(6).Select(payment => new
        {
            occurrenceDate = payment.OccurrenceDate.ToString("yyyy-MM-dd"),
            payment = payment.Payment,
            interest = payment.Interest,
            principal = payment.Principal,
            balanceAfter = payment.BalanceAfter
        }).ToList();
        return row;
    }
}
