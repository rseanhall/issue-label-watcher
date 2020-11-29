using System.Net;
using System.Net.Mail;

namespace IssueLabelWatcherWebJob
{
    public interface IEmailSender
    {
        void SendHtmlEmail(string subject, string htmlBody);
    }

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
            var message = new MailMessage(_configuration.SmtpFrom, _configuration.SmtpTo, subject, htmlBody)
            {
                IsBodyHtml = true,
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
