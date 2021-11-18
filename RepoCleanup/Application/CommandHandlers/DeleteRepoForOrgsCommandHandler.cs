using RepoCleanup.Application.Commands;
using RepoCleanup.Infrastructure.Clients.Gitea;
using RepoCleanup.Models;
using System.Net;
using System.Threading.Tasks;

namespace RepoCleanup.Application.CommandHandlers
{
    public class DeleteRepoForOrgsCommandHandler
    {
        private readonly GiteaService _giteaService;
        public DeleteRepoForOrgsCommandHandler(GiteaService giteaService)
        {
            _giteaService = giteaService;
        }

        public async Task<int> Handle(DeleteRepoForOrgsCommand command)
        {
            var reposDeletedCounter = 0;            

            foreach (var org in command.Orgs)
            {
                var repoName = command.PrefixRepoNameWithOrg ? $"{org}-{command.RepoName}" : command.RepoName;

                var giteaResponse = await _giteaService.GetRepo(org, repoName);
                if(giteaResponse.StatusCode == HttpStatusCode.OK)
                {
                    giteaResponse = await _giteaService.DeleteRepository(org, repoName);
                    
                    if(giteaResponse.Success)
                    {                        
                        reposDeletedCounter++;
                    }
                }                
            }

            return reposDeletedCounter;
        }
    }
}
