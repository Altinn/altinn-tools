using RepoCleanup.Application.Commands;
using RepoCleanup.Services;
using System.Linq;
using System.Threading.Tasks;

namespace RepoCleanup.Application.CommandHandlers
{
    public class AddTeamToRepoCommandHandler
    {
        private readonly GiteaService _giteaService;

        public AddTeamToRepoCommandHandler(GiteaService giteaService)
        {
            _giteaService = giteaService;
        }

        public async Task<int> Handle(AddTeamToRepoCommand command)
        {
            var teamsAdded = 0;

            foreach(var org in command.Orgs)
            {
                var teams = await _giteaService.GetTeam(org);
                if (teams.FirstOrDefault(t => t.Name == command.TeamName) == null)
                {
                    return teamsAdded;
                }

                var repoName = command.PrefixRepoNameWithOrg ? $"{org}-{command.RepoName}" : command.RepoName;
                var giteaResponse = await _giteaService.AddTeamToRepo(org, repoName, command.TeamName);
                if(giteaResponse.Success)
                {
                    teamsAdded++;
                }
            }
            
            return teamsAdded;
        }       
    }
}
