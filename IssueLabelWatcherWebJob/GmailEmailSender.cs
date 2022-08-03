using System;
using System.IO;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Microsoft.Extensions.Configuration;
using MimeKit;
using MimeKit.Text;

namespace IssueLabelWatcherWebJob
{
    public class GmailEmailSender : IEmailSender
    {
        private readonly IIlwConfiguration _ilwConfiguration;
        private readonly IGoogleApiServiceFactory _googleApiServiceFactory;
        private readonly IGoogleErrorHandler _googleErrorHandler;

        public GmailEmailSender(IIlwConfiguration ilwConfiguration, IGoogleApiServiceFactory googleApiServiceFactory, IGoogleErrorHandler googleErrorHandler)
        {
            _ilwConfiguration = ilwConfiguration;
            _googleApiServiceFactory = googleApiServiceFactory;
            _googleErrorHandler = googleErrorHandler;
        }

        public void SendHtmlEmail(string subject, string htmlBody)
        {
            this.SendEmail(subject, new TextPart(TextFormat.Html) { Text = htmlBody });
        }

        public void SendPlainTextEmail(string subject, string plainTextBody)
        {
            this.SendEmail(subject, new TextPart(TextFormat.Plain) { Text = plainTextBody });
        }

        private void SendEmail(string subject, TextPart bodyTextPart)
        {
            using var message = new MimeMessage
            {
                Subject = subject,
            };

            message.From.Add(new MailboxAddress(null, _ilwConfiguration.GmailFrom));
            message.To.Add(new MailboxAddress(null, _ilwConfiguration.GmailTo));
            message.Body = bodyTextPart;

            using MemoryStream memoryStream = new();
            message.WriteTo(memoryStream);
            byte[] messageBytes = memoryStream.ToArray();

            var body = new Message
            {
                Raw = Convert.ToBase64String(messageBytes),
            };

            try
            {
                var request = _googleApiServiceFactory.GetGmailService().Users.Messages.Send(body, "me");
                request.Execute();
            }
            catch (InvalidOperationException ioe)
            {
                if (ioe.Message?.Contains("The access token has expired and could not be refreshed") == true)
                {
                    _googleErrorHandler.OnTokenExpired(ioe).Wait();
                }

                throw;
            }
        }
    }
}
