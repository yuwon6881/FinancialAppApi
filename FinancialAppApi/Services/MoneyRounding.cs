namespace FinancialAppApi.Services;

public static class MoneyRounding
{
    public static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
