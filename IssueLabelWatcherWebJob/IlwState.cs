using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IssueLabelWatcherWebJob
{
    public interface IIlwState
    {
        DateTime? LastRunTime { get; set; }
        Dictionary<string, HashSet<string>> IssuesByRepo { get; }
    }

    public class IlwState : IIlwState
    {
        public DateTime? LastRunTime { get; set; }
        public Dictionary<string, HashSet<string>> IssuesByRepo { get; set; }

        public IlwState(Dictionary<string, HashSet<string>> issuesByRepo)
        {
            this.IssuesByRepo = issuesByRepo;
        }
    }

    public interface IIlwStateService
    {
        Task<IIlwState> Load();
        Task Save(IIlwState state);
    }

    public class IlwStateService : IIlwStateService
    {
        private CloudBlockBlob? _blob;
        private readonly IIlwConfiguration _configuration;

        public IlwStateService(IIlwConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<IIlwState> Load()
        {
            var state = new IlwState
            (
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            );

            var json = await this.ReadBlob();
            if (json != null)
            {
                var stateObject = JsonConvert.DeserializeObject<IlwStateObject>(json)!;
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

        public Task Save(IIlwState state)
        {
            var stateObject = new IlwStateObject
            {
                LastRunTime = state.LastRunTime,
                Repos = state.IssuesByRepo.Select(x => new IlwStateObject.Repo
                {
                    FullName = x.Key,
                    IssueNumbers = x.Value.ToArray(),
                }).ToArray(),
            };

            var json = JsonConvert.SerializeObject(stateObject);
            return this.WriteBlob(json);
        }

        private async Task<CloudBlockBlob> GetBlob()
        {
            if (_blob == null)
            {
                var storageAccount = CloudStorageAccount.Parse(_configuration.StorageAccountConnectionString);
                var blobClient = storageAccount.CreateCloudBlobClient();
                var container = blobClient.GetContainerReference("issue-label-watcher");
                await container.CreateIfNotExistsAsync();
                _blob = container.GetBlockBlobReference("state");
            }

            return _blob;
        }

        private async Task<string?> ReadBlob()
        {
            var blob = await this.GetBlob();
            if (!await blob.ExistsAsync())
            {
                return null;
            }

            return await blob.DownloadTextAsync();
        }

        private async Task WriteBlob(string content)
        {
            var blob = await this.GetBlob();
            await blob.UploadTextAsync(content);
        }
    }

    public class IlwStateObject
    {
        public DateTime? LastRunTime { get; set; }
        public Repo[] Repos { get; set; }

        public IlwStateObject()
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
