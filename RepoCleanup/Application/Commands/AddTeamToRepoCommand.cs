using System.Collections.Generic;

namespace RepoCleanup.Application.Commands
{
    public class AddTeamToRepoCommand
    {
        public AddTeamToRepoCommand(List<string> orgs, string repoName, bool prefixRepoNameWithOrg, string teamName)
        {
            Orgs = orgs;
            RepoName = repoName;
            PrefixRepoNameWithOrg = prefixRepoNameWithOrg;
            TeamName = teamName;            
        }

        public List<string> Orgs { get; private set; }
        public string RepoName { get; private set; }
        public bool PrefixRepoNameWithOrg { get; private set; }
        public string TeamName { get; private set; }
    }
}
