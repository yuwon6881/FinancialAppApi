using System.ComponentModel.DataAnnotations;

namespace FinancialAppApi.Models;

public class FinancialSetting
{
    [Key]
    public int Id { get; set; }

    [Required]
    public decimal MonthlyIncome { get; set; }

    [Required]
    public decimal TargetStabilityFund { get; set; }

    [Required]
    public string SelectedMonth { get; set; } = "Jun";

    [Required]
    public int SelectedYear { get; set; } = 2026;

    [Required]
    public decimal EssentialsAlloc { get; set; } = 0.50m;

    [Required]
    public decimal GrowthAlloc { get; set; } = 0.25m;

    [Required]
    public decimal StabilityAlloc { get; set; } = 0.15m;

    [Required]
    public decimal RewardsAlloc { get; set; } = 0.10m;

    [Required]
    public int CycleDay { get; set; } = 28; // Day of the month representing the cycle start (e.g. 28th)
}
