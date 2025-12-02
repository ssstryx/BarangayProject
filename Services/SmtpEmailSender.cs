using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace BarangayProject.Services
{
    public class SmtpEmailSender : IEmailSender
    {
        private readonly EmailSettings _settings;
        private readonly ILogger<SmtpEmailSender> _logger;

        public SmtpEmailSender(IOptions<EmailSettings> options, ILogger<SmtpEmailSender> logger)
        {
            _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SendEmailAsync(string toEmail, string subject, string htmlMessage)
        {
            if (string.IsNullOrWhiteSpace(_settings.Host))
                throw new InvalidOperationException("SMTP host is not configured.");

            var from = string.IsNullOrWhiteSpace(_settings.FromEmail) ? _settings.Username : _settings.FromEmail;
            var fromName = string.IsNullOrWhiteSpace(_settings.FromName) ? "NoReply" : _settings.FromName;

            var mail = new MailMessage()
            {
                From = new MailAddress(from, fromName),
                Subject = subject ?? "",
                Body = htmlMessage ?? "",
                IsBodyHtml = true
            };

            mail.To.Add(new MailAddress(toEmail));

            using var client = new SmtpClient(_settings.Host, _settings.Port)
            {
                EnableSsl = _settings.EnableSsl
            };

            if (!string.IsNullOrWhiteSpace(_settings.Username))
            {
                client.Credentials = new NetworkCredential(_settings.Username, _settings.Password);
            }

            try
            {
                // async send
                await client.SendMailAsync(mail);
                _logger.LogInformation("Email sent to {to}", toEmail);
            }
            catch (SmtpException sx)
            {
                _logger.LogError(sx, "SMTP error while sending email to {to}", toEmail);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while sending email to {to}", toEmail);
                throw;
            }
        }
    }
}
