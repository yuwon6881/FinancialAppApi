namespace FinancialAppApi.Services;

public interface IEmailSender
{
    Task SendAsync(string toAddress, string subject, string body);
}
