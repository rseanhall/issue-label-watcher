using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IssueLabelWatcherWebJob
{
    public interface IIlwIssuesState
    {
        DateTime? LastRunTime { get; set; }
        Dictionary<string, HashSet<string>> IssuesByRepo { get; }
    }

    public class IlwIssuesState : IIlwIssuesState
    {
        public DateTime? LastRunTime { get; set; }
        public Dictionary<string, HashSet<string>> IssuesByRepo { get; set; }

        public IlwIssuesState(Dictionary<string, HashSet<string>> issuesByRepo)
        {
            this.IssuesByRepo = issuesByRepo;
        }
    }

    public interface IIlwGoogleState
    {
        bool ExpiredAuthEmailSent { get; set; }
    }

    public class IlwGoogleState : IIlwGoogleState
    {
        public bool ExpiredAuthEmailSent { get; set; }
    }

    public interface IIlwStateService
    {
        Task<IIlwGoogleState> LoadGoogle();
        Task<IIlwIssuesState> LoadIssues();
        Task SaveGoogle(IIlwGoogleState state);
        Task SaveIssues(IIlwIssuesState state);
    }

    public class IlwStateService : IIlwStateService
    {
        private CloudBlobContainer? _container;
        private CloudBlockBlob? _googleBlob;
        private CloudBlockBlob? _issuesBlob;

        private readonly IIlwConfiguration _configuration;

        public IlwStateService(IIlwConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<IIlwGoogleState> LoadGoogle()
        {
            var state = new IlwGoogleState();

            var json = await this.ReadGoogleBlob();
            if (json != null)
            {
                var stateObject = JsonConvert.DeserializeObject<IlwGoogleStateObject>(json)!;
                state.ExpiredAuthEmailSent = stateObject.ExpiredAuthEmailSent;
            }

            return state;
        }

        public async Task<IIlwIssuesState> LoadIssues()
        {
            var state = new IlwIssuesState
            (
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            );

            var json = await this.ReadIssuesBlob();
            if (json != null)
            {
                var stateObject = JsonConvert.DeserializeObject<IlwIssuesStateObject>(json)!;
                state.LastRunTime = stateObject.LastRunTime;
                foreach (var repo in stateObject.Repos)
                {
                    if (repo.FullName != null)
                    {
                        state.IssuesByRepo.Add(repo.FullName, new HashSet<string>(repo.IssueNumbers ?? Array.Empty<string>()));
                    }
                }
            }

            return state;
        }

        public Task SaveGoogle(IIlwGoogleState state)
        {
            var stateObject = new IlwGoogleStateObject
            {
                ExpiredAuthEmailSent = state.ExpiredAuthEmailSent,
            };

            var json = JsonConvert.SerializeObject(stateObject);
            return this.WriteGoogleBlob(json);
        }

        public Task SaveIssues(IIlwIssuesState state)
        {
            var stateObject = new IlwIssuesStateObject
            {
                LastRunTime = state.LastRunTime,
                Repos = state.IssuesByRepo.Select(x => new IlwIssuesStateObject.Repo
                {
                    FullName = x.Key,
                    IssueNumbers = x.Value.ToArray(),
                }).ToArray(),
            };

            var json = JsonConvert.SerializeObject(stateObject);
            return this.WriteIssuesBlob(json);
        }

        private async Task<CloudBlobContainer> GetContainer()
        {
            if (_container == null)
            {
                var storageAccount = CloudStorageAccount.Parse(_configuration.StorageAccountConnectionString);
                var blobClient = storageAccount.CreateCloudBlobClient();
                var container = blobClient.GetContainerReference("issue-label-watcher");
                await container.CreateIfNotExistsAsync();
                _container = container;
            }

            return _container;
        }

        private async Task<CloudBlockBlob> GetBlob(string blobName)
        {
            var container = await this.GetContainer();
            return container.GetBlockBlobReference(blobName);
        }

        private async Task<CloudBlockBlob> GetGoogleBlob()
        {
            if (_googleBlob == null)
            {
                _googleBlob = await this.GetBlob("googleState");
            }

            return _googleBlob;
        }

        private async Task<CloudBlockBlob> GetIssuesBlob()
        {
            if (_issuesBlob == null)
            {
                _issuesBlob = await this.GetBlob("state");
            }

            return _issuesBlob;
        }

        private async Task<string?> ReadBlob(CloudBlockBlob blob)
        {
            if (!await blob.ExistsAsync())
            {
                return null;
            }

            return await blob.DownloadTextAsync();
        }

        private async Task<string?> ReadGoogleBlob()
        {
            var blob = await this.GetGoogleBlob();
            return await this.ReadBlob(blob);
        }

        private async Task<string?> ReadIssuesBlob()
        {
            var blob = await this.GetIssuesBlob();
            return await this.ReadBlob(blob);
        }

        private async Task WriteBlob(CloudBlockBlob blob, string content)
        {
            await blob.UploadTextAsync(content);
        }

        private async Task WriteGoogleBlob(string content)
        {
            var blob = await this.GetGoogleBlob();
            await this.WriteBlob(blob, content);
        }

        private async Task WriteIssuesBlob(string content)
        {
            var blob = await this.GetIssuesBlob();
            await this.WriteBlob(blob, content);
        }
    }

    public class IlwGoogleStateObject
    {
        public bool ExpiredAuthEmailSent { get; set; }
    }

    public class IlwIssuesStateObject
    {
        public DateTime? LastRunTime { get; set; }
        public Repo[] Repos { get; set; }

        public IlwIssuesStateObject()
        {
            this.Repos = new Repo[0];
        }

        public class Repo
        {
            public string? FullName { get; set; }
            public string[]? IssueNumbers { get; set; }
        }
    }
}
