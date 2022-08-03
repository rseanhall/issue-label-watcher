using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IssueLabelWatcherWebJob
{
    public interface IEmailSender
    {
        void SendHtmlEmail(string subject, string htmlBody);
        void SendPlainTextEmail(string subject, string plainTextBody);
    }

    public class EmailSender : IEmailSender
    {
        private readonly IIlwConfiguration _ilwConfiguration;
        private readonly IEmailSender _gmailEmailSender;
        private readonly IEmailSender _smtpEmailSender;

        public EmailSender(IIlwConfiguration ilwConfiguration, GmailEmailSender gmailEmailSender, SmtpEmailSender smtpEmailSender)
        {
            _ilwConfiguration = ilwConfiguration;
            _gmailEmailSender = gmailEmailSender;
            _smtpEmailSender = smtpEmailSender;
        }

        public void SendHtmlEmail(string subject, string htmlBody)
        {
            if (_ilwConfiguration.GmailEnabled)
            {
                _gmailEmailSender.SendHtmlEmail(subject, htmlBody);
            }
            else
            {
                _smtpEmailSender.SendHtmlEmail(subject, htmlBody);
            }
        }

        public void SendPlainTextEmail(string subject, string plainTextBody)
        {
            if (_ilwConfiguration.GmailEnabled)
            {
                _gmailEmailSender.SendPlainTextEmail(subject, plainTextBody);
            }
            else
            {
                _smtpEmailSender.SendPlainTextEmail(subject, plainTextBody);
            }
        }
    }
}
