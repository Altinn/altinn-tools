using RepoCleanup.Application.Commands;
using RepoCleanup.Infrastructure.Clients.Gitea;
using RepoCleanup.Utils;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RepoCleanup.Application.CommandHandlers
{
    public class CreateDefaultTeamsCommandHandler
    {
        private readonly GiteaService _giteaService;

        public CreateDefaultTeamsCommandHandler(GiteaService giteaService)
        {
            _giteaService = giteaService;
        }

        public async Task<bool> Handle(CreateDefaultTeamsCommand command)
        {
            bool createdTeams = true;
            List<CreateTeamOption> teams = new List<CreateTeamOption>();
            teams.Add(TeamOption.GetCreateTeamOption("Deploy-Production", "Members can deploy to production", false, Permission.read));
            teams.Add(TeamOption.GetCreateTeamOption("Deploy-TT02", "Members can deploy to TT02", false, Permission.read));
            teams.Add(TeamOption.GetCreateTeamOption("Devs", "All application developers", true, Permission.write));

            foreach (CreateTeamOption team in teams)
            {
                GiteaResponse response = await _giteaService.CreateTeam(command.OrgShortName, team);

                if (!response.Success)
                {
                    createdTeams = false;                    
                }
            }

            return createdTeams;
        }
    }
}
