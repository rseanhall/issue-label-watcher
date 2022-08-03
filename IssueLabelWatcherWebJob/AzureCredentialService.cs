using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;

namespace IssueLabelWatcherWebJob
{
    public interface IAzureCredentialService
    {
        TokenCredential GetAppTokenCredential();
    }

    public sealed class AzureCredentialService : IAzureCredentialService
    {
        private readonly TokenCredential _appTokenCredential;

        public AzureCredentialService(IIlwConfiguration ilwConfiguration)
        {
            X509Certificate2? cert = null;

            if (!string.IsNullOrEmpty(ilwConfiguration.GmailAzureTenantId) && !string.IsNullOrEmpty(ilwConfiguration.GmailAzureApplicationId))
            {
                // New-SelfSignedCertificate -Type SSLServerAuthentication -Subject "{ilwConfiguration.GmailAzureApplicationId}" -CertStoreLocation "cert:\CurrentUser\My" -NotAfter "12-31-2299"
                using var store = new X509Store(StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                var certs = store.Certificates.Find(X509FindType.FindBySubjectName, $"{ilwConfiguration.GmailAzureApplicationId}", false)
                                              .Find(X509FindType.FindByTimeValid, DateTime.Now, false);
                cert = certs.OfType<X509Certificate2>().SingleOrDefault();
            }

            if (cert == null)
            {
                _appTokenCredential = new DefaultAzureCredential();
            }
            else
            {
                _appTokenCredential = new ClientCertificateCredential(ilwConfiguration.GmailAzureTenantId, ilwConfiguration.GmailAzureApplicationId, cert);
            }
        }

        public TokenCredential GetAppTokenCredential()
        {
            return _appTokenCredential;
        }
    }
}
