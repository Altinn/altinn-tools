using RepoCleanup.Application.Commands;
using RepoCleanup.Infrastructure.Clients.Gitea;
using RepoCleanup.Models;
using System.Threading.Tasks;

namespace RepoCleanup.Application.CommandHandlers
{
    public class CreateOrgCommandHandler
    {
        private readonly GiteaService _giteaService;
        public CreateOrgCommandHandler(GiteaService giteaService)
        {
            _giteaService = giteaService;
        }

        public async Task<bool> Handle(CreateOrgCommand command)
        {
            GiteaResponse response = await _giteaService.CreateOrg(
                new Organisation() 
                { 
                    Username = command.ShortName, 
                    Fullname = command.FullName, 
                    Website = command.Website, 
                    Visibility = "public", 
                    RepoAdminChangeTeamAccess = false 
                });

            return response.Success;
        }
    }
}
