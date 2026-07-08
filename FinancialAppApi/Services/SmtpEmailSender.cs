using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FinancialAppApi.Services;

// Reads Email:Smtp:{Host,Port,User,Password,From,UseSsl} from configuration. When Host is not
// configured (e.g. local dev without real credentials on hand) this logs the message instead of
// sending it, so email-verification/2FA flows stay testable without an SMTP account. Real
// deployments must set these values (appsettings, environment variables, or a secret store) for
// mail to actually deliver.
public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendAsync(string toAddress, string subject, string body)
    {
        var host = _configuration["Email:Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogWarning(
                "Email:Smtp:Host is not configured -- logging instead of sending. To: {To} Subject: {Subject} Body: {Body}",
                toAddress, subject, body);
            return;
        }

        var port = _configuration.GetValue("Email:Smtp:Port", 587);
        var user = _configuration["Email:Smtp:User"] ?? string.Empty;
        var password = _configuration["Email:Smtp:Password"] ?? string.Empty;
        var from = _configuration["Email:Smtp:From"] ?? user;
        var useSsl = _configuration.GetValue("Email:Smtp:UseSsl", true);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        await client.ConnectAsync(host, port, useSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);
        if (!string.IsNullOrEmpty(user))
        {
            await client.AuthenticateAsync(user, password);
        }
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
