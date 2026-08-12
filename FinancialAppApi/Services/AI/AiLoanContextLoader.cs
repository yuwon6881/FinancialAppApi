using FinancialAppApi.Models;

namespace FinancialAppApi.Services;

public partial class AiAssistantService
{
    private sealed record AiLoanContext(object Payload, string? SelectedLoanId);

    private async Task<AiLoanContext> LoadLoanContextAsync(
        AiQueryPlan queryPlan,
        string? selectedLoanId,
        bool sensitiveMode,
        CancellationToken cancellationToken)
    {
        if (!queryPlan.NeedsLoans) return new AiLoanContext(Array.Empty<object>(), selectedLoanId);

        IReadOnlyList<Loans.LoanView> views;
        if (!string.IsNullOrWhiteSpace(selectedLoanId))
        {
            var selected = await _loanService.GetLoanAsync(selectedLoanId, cancellationToken);
            views = selected == null ? [] : [selected];
        }
        else
        {
            views = await _loanService.GetLoansAsync(cancellationToken);
        }

        var payload = views.Take(20).Select(view =>
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

            if (sensitiveMode) return row;

            row["openingPrincipal"] = loan.OpeningPrincipal;
            row["annualRatePercent"] = loan.AnnualRatePercent;
            if (!forecastAvailable) return row;

            row["amountStillOwed"] = view.Replay.OutstandingBalance;
            row["scheduledPayment"] = view.Replay.ScheduledPayment;
            row["totalScheduledInterest"] = view.Replay.TotalScheduledInterest;
            row["interestPaid"] = view.Replay.TotalInterestPaid;
            row["payoffDate"] = view.Replay.PayoffDate?.ToString("yyyy-MM-dd");
            row["recordedHistory"] = view.Replay.Payments.TakeLast(20).Select(payment => new
            {
                occurrenceDate = payment.OccurrenceDate.ToString("yyyy-MM-dd"),
                payment = payment.Payment,
                interest = payment.Interest,
                principal = payment.Principal,
                balanceAfter = payment.BalanceAfter
            }).ToList();
            row["plannedSchedule"] = view.Replay.FutureSchedule.Take(12).Select(payment => new
            {
                occurrenceDate = payment.OccurrenceDate.ToString("yyyy-MM-dd"),
                payment = payment.Payment,
                interest = payment.Interest,
                principal = payment.Principal,
                balanceAfter = payment.BalanceAfter
            }).ToList();
            return row;
        }).ToList();

        return new AiLoanContext(payload, payload.Count == 1 ? views[0].Loan.Id : selectedLoanId);
    }
}
