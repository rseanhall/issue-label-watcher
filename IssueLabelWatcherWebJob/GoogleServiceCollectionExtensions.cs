using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IssueLabelWatcherWebJob
{
    public static class GoogleServiceCollectionExtensions
    {
        public static IServiceCollection AddGoogleServices(this IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<IBlockConditionallyCodeReceiver, BlockConditionallyCodeReceiver>();
            serviceCollection.AddSingleton<IGoogleApiServiceFactory, GoogleApiServiceFactory>();
            serviceCollection.AddSingleton<IGoogleCredentialService, GoogleCredentialService>();
            serviceCollection.AddSingleton<IGoogleErrorHandler, GoogleErrorHandler>();
            serviceCollection.AddSingleton<GmailEmailSender>();

            return serviceCollection;
        }
    }

    public interface IGoogleApiServiceFactory
    {
        GmailService GetGmailService();
    }

    public class GoogleApiServiceFactory : IGoogleApiServiceFactory
    {
        private readonly GmailService _gmailService;

        public GoogleApiServiceFactory(IGoogleCredentialService googleCredentialService)
        {
            var credential = googleCredentialService.GetUserCredential();

            _gmailService = new GmailService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = Program.ApplicationUserAgent,
            });
        }

        public GmailService GetGmailService()
        {
            return _gmailService;
        }
    }

    public interface IGoogleCredentialService
    {
        UserCredential GetUserCredential();
        Task Initialize(CancellationToken cancellationToken);
    }

    public class GoogleCredentialService : IGoogleCredentialService
    {
        private readonly IBlockConditionallyCodeReceiver _codeReceiver;
        private readonly IGoogleErrorHandler _googleErrorHandler;
        private readonly IIlwConfiguration _ilwConfiguration;
        private UserCredential? _userCredential;

        public GoogleCredentialService(IIlwConfiguration ilwConfiguration, IBlockConditionallyCodeReceiver codeReceiver, IGoogleErrorHandler googleErrorHandler)
        {
            _ilwConfiguration = ilwConfiguration;
            _codeReceiver = codeReceiver;
            _googleErrorHandler = googleErrorHandler;
        }

        public UserCredential GetUserCredential()
        {
            if (_userCredential == null)
            {
                throw new InvalidOperationException();
            }

            return _userCredential;
        }

        public async Task Initialize(CancellationToken cancellationToken)
        {
            // https://console.cloud.google.com/
            // https://developers.google.com/gmail/api/quickstart/dotnet
            // https://github.com/googleapis/google-api-dotnet-client/tree/main/Src/Support/Google.Apis.Auth/OAuth2
            // https://stackoverflow.com/a/65936387
            if (!_ilwConfiguration.GmailEnabled)
            {
                return;
            }

            IDataStore dataStore;
            if (!string.IsNullOrEmpty(_ilwConfiguration.GmailAzureKeyVaultUrl))
            {
                var azureCredentialService = new AzureCredentialService(_ilwConfiguration);
                dataStore = new AzureKeyVaultGoogleAuthDataStore(_ilwConfiguration.GmailConfigurationPrefix, _ilwConfiguration.GmailAzureKeyVaultUrl, azureCredentialService);
            }
            else
            {
                dataStore = new FileDataStore(_ilwConfiguration.GmailConfigurationPrefix);
            }

            var secrets = new ClientSecrets()
            {
                ClientId = _ilwConfiguration.GmailClientId!,
                ClientSecret = _ilwConfiguration.GmailClientSecret!,
            };

            if (!_ilwConfiguration.GmailAllowAuthenticationRequest)
            {
                _codeReceiver.StartBlockingRequests(_googleErrorHandler);
            }

            var userCredential = await this.GenerateUserCredential(secrets, dataStore, cancellationToken);

            bool needsReauthorization;

            try
            {
                needsReauthorization = !(await userCredential.RefreshTokenAsync(cancellationToken));
                if (needsReauthorization)
                {
                    await userCredential.Flow.DeleteTokenAsync(userCredential.UserId, cancellationToken);
                }
            }
            catch (TokenResponseException)
            {
                needsReauthorization = true;
            }

            if (needsReauthorization)
            {
                await this.GenerateUserCredential(secrets, dataStore, cancellationToken);
            }

            _codeReceiver.StartBlockingRequests(_googleErrorHandler);

            await _googleErrorHandler.OnTokenValidated();
        }

        private async Task<UserCredential> GenerateUserCredential(ClientSecrets secrets, IDataStore dataStore, CancellationToken cancellationToken)
        {
            _userCredential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                new[] { GmailService.ScopeConstants.GmailSend },
                _ilwConfiguration.GmailFrom!,
                cancellationToken,
                dataStore,
                _codeReceiver
                );
            return _userCredential;
        }
    }

    public class AzureKeyVaultGoogleAuthDataStore : IDataStore
    {
        private readonly string? _prefix;
        private readonly SecretClient _secretClient;

        public AzureKeyVaultGoogleAuthDataStore(string? prefix, string vaultUri, IAzureCredentialService azureCredentialService)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                _prefix = EscapeKey(string.Format("{0}-", prefix));
            }
            _secretClient = new SecretClient(new Uri(vaultUri), azureCredentialService.GetAppTokenCredential());
        }

        public async Task StoreAsync<T>(string key, T value)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            var name = GenerateStoredKey(_prefix, key, typeof(T));

            if (value is TokenResponse tokenResponse)
            {
                var previousValue = await this.GetAsync<TokenResponse>(key);
                if (previousValue?.RefreshToken == tokenResponse.RefreshToken && previousValue?.Scope == tokenResponse.Scope)
                {
                    // As a long running service, there's no point in storing this unless the refresh token changed.
                    return;
                }
            }

            var serializedValue = JsonSerializer.Serialize(value);
            await this.SetSecretAsync(name, serializedValue);
        }

        public async Task DeleteAsync<T>(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            var name = GenerateStoredKey(_prefix, key, typeof(T));
            await this.DeleteSecretAsync(name);
        }

        public async Task<T?> GetAsync<T>(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            var name = GenerateStoredKey(_prefix, key, typeof(T));
            try
            {
                var result = await _secretClient.GetSecretAsync(name);

                if (string.IsNullOrEmpty(result?.Value?.Value))
                {
                    return default(T);
                }
                return JsonSerializer.Deserialize<T>(result.Value.Value);
            }
            catch (RequestFailedException rfe)
            {
                if (rfe.Status == 404 ||
                    rfe.Status == 403 && rfe.Message.Contains("\"code\":\"SecretDisabled\""))
                {
                    return default(T);
                }
                throw;
            }
        }

        public async Task ClearAsync()
        {
            var secretProperties = _secretClient.GetPropertiesOfSecrets()
                                                .Where(sp => string.IsNullOrEmpty(_prefix) || sp.Name.StartsWith(_prefix))
                                                .ToList();
            foreach (var secretProperty in secretProperties)
            {
                await this.DeleteSecretAsync(secretProperty.Name);
            }
        }

        private async Task DeleteSecretAsync(string name)
        {
            await this.SetSecretAsync(name, "");
        }

        private async Task SetSecretAsync(string name, string serializedValue)
        {
            await _secretClient.SetSecretAsync(name, serializedValue);
        }

        private static string GenerateStoredKey(string? prefix, string key, Type t)
        {
            return EscapeKey(string.Format("{0}{1}-{2}", prefix, key, t.FullName));
        }

        private static string EscapeKey(string key)
        {
            return Regex.Replace(key, "[^a-zA-Z0-9\\-]", "-");
        }
    }

    public interface IBlockConditionallyCodeReceiver : ICodeReceiver
    {
        void StartBlockingRequests(IGoogleErrorHandler googleErrorHandler);
    }

    public class BlockConditionallyCodeReceiver : IBlockConditionallyCodeReceiver
    {
        private readonly LocalServerCodeReceiver _innerCodeReceiver;

        public string RedirectUri => _innerCodeReceiver.RedirectUri;

        public BlockConditionallyCodeReceiver()
        {
            _innerCodeReceiver = new LocalServerCodeReceiver();
        }

        private IGoogleErrorHandler? GoogleErrorHandler { get; set; }

        public void StartBlockingRequests(IGoogleErrorHandler googleErrorHandler)
        {
            this.GoogleErrorHandler = googleErrorHandler;
        }

        public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url, CancellationToken taskCancellationToken)
        {
            if (this.GoogleErrorHandler != null)
            {
                var exception = new GoogleAuthExpiredException();
                await this.GoogleErrorHandler.OnTokenExpired(exception);
                throw exception;
            }

            return await _innerCodeReceiver.ReceiveCodeAsync(url, taskCancellationToken);
        }
    }

    public class GoogleAuthExpiredException : Exception
    {
        public GoogleAuthExpiredException() : base("Blocked attempt to authenticate user after startup") { }
    }

    public interface IGoogleErrorHandler
    {
        Task OnTokenExpired(Exception exception);
        Task OnTokenValidated();
    }

    public class GoogleErrorHandler : IGoogleErrorHandler
    {
        private readonly IEmailSender _emailSender;
        private readonly IIlwStateService _ilwStateService;
        private readonly ILogger _logger;

        public GoogleErrorHandler(SmtpEmailSender emailSender, IIlwStateService ilwStateService, ILogger<GoogleErrorHandler> logger)
        {
            _emailSender = emailSender;
            _ilwStateService = ilwStateService;
            _logger = logger;
        }

        public async Task OnTokenExpired(Exception exception)
        {
            var googleState = await _ilwStateService.LoadGoogle();

            if (!googleState.ExpiredAuthEmailSent)
            {
                googleState.ExpiredAuthEmailSent = true;

                try
                {
                    _emailSender.SendPlainTextEmail("IssueLabelWatcher Google Auth Expired", exception.ToString());

                    await _ilwStateService.SaveGoogle(googleState);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error sending google auth expired email");
                }
            }

            _logger.LogError(exception, "Google auth expired and couldn't be refreshed");
        }

        public async Task OnTokenValidated()
        {
            var googleState = await _ilwStateService.LoadGoogle();

            if (googleState.ExpiredAuthEmailSent)
            {
                googleState.ExpiredAuthEmailSent = false;

                try
                {
                    await _ilwStateService.SaveGoogle(googleState);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error saving google state");
                }
            }

            _logger.LogInformation("Google auth validated");
        }
    }
}
