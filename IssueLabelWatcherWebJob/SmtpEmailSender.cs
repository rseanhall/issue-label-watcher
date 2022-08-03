using System.Net;
using System.Net.Mail;

namespace IssueLabelWatcherWebJob
{
    public class SmtpEmailSender : IEmailSender
    {
        private readonly IIlwConfiguration _configuration;
        private readonly NetworkCredential _credentials;

        public SmtpEmailSender(IIlwConfiguration configuration)
        {
            _configuration = configuration;
            _credentials = new NetworkCredential(_configuration.SmtpUsername, _configuration.SmtpPassword);
        }

        public void SendHtmlEmail(string subject, string htmlBody)
        {
            this.SendEmail(subject, htmlBody, true);
        }

        public void SendPlainTextEmail(string subject, string plainTextBody)
        {
            this.SendEmail(subject, plainTextBody, false);
        }

        private void SendEmail(string subject, string body, bool isHtml)
        {
            var message = new MailMessage(_configuration.SmtpFrom, _configuration.SmtpTo, subject, body)
            {
                IsBodyHtml = isHtml,
            };

            var smtpClient = new SmtpClient(_configuration.SmtpServer)
            {
                EnableSsl = true,
                Credentials = _credentials,
            };
            if (_configuration.SmtpPort.HasValue)
            {
                smtpClient.Port = _configuration.SmtpPort.Value;
            }

            smtpClient.Send(message);
        }
    }
}
