using RepoCleanup.Application.Commands;
using RepoCleanup.Services;
using System.Threading.Tasks;

namespace RepoCleanup.Application.CommandHandlers
{
    public class AddTeamToRepoCommandHandler
    {
        private readonly GiteaService _gitteaService;

        public AddTeamToRepoCommandHandler(GiteaService giteaService)
        {
            _gitteaService = giteaService;
        }

        public async Task<int> Handle(AddTeamToRepoCommand command)
        {
            var teamsAdded = 0;
            foreach(var org in command.Orgs)
            {
                var repoName = command.PrefixRepoNameWithOrg ? $"{org}-{command.RepoName}" : command.RepoName;
                var giteaResponse = await _gitteaService.AddTeamToRepo(org, repoName, command.TeamName);
                if(giteaResponse.Success)
                {
                    teamsAdded++;
                }
            }
            
            return teamsAdded;
        }
    }
}
