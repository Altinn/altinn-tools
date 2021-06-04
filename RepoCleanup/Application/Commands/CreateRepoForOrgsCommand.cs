using RepoCleanup.Models;
using System.Collections.Generic;

namespace RepoCleanup.Application.Commands
{
    public class CreateRepoForOrgsCommand
    {
        public CreateRepoForOrgsCommand(List<string> orgs, string repoName, bool prefixRepoNameWithOrg)
        {
            Orgs = orgs;
            RepoName = repoName;
            PrefixRepoNameWithOrg = prefixRepoNameWithOrg;
        }

        public List<string> Orgs { get; private set; }
        public string RepoName { get; private set; }
        public bool PrefixRepoNameWithOrg { get; private set; }
    }
}
