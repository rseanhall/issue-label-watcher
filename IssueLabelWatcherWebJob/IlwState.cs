using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace IssueLabelWatcherWebJob
{
    public interface IIlwState
    {
        Dictionary<string, HashSet<string>> IssuesByRepo { get; }
    }

    public class IlwState : IIlwState
    {
        public Dictionary<string, HashSet<string>> IssuesByRepo { get; private set; }

        public void Load(string json)
        {
            this.IssuesByRepo = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            if (json == null)
            {
                return;
            }

            var state = JsonConvert.DeserializeObject<IlwStateObject>(json);
            foreach (var repo in state.Repos)
            {
                this.IssuesByRepo.Add(repo.FullName, new HashSet<string>(repo.IssueNumbers));
            }
        }

        public string Save()
        {
            var state = new IlwStateObject
            {
                Repos = this.IssuesByRepo.Select(x => new IlwStateObject.Repo
                {
                    FullName = x.Key,
                    IssueNumbers = x.Value.ToArray(),
                }).ToArray(),
            };

            return JsonConvert.SerializeObject(state);
        }
    }

    public class IlwStateObject
    {
        public Repo[] Repos { get; set; }

        public class Repo
        {
            public string FullName { get; set; }
            public string[] IssueNumbers { get; set; }
        }
    }
}
