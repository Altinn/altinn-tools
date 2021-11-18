using RepoCleanup.Application.Commands;
using RepoCleanup.Infrastructure.Clients.Gitea;
using System.Net;
using System.Threading.Tasks;

namespace RepoCleanup.Application.CommandHandlers
{
    public class CreateRepoForOrgsCommandHandler
    {
        private readonly GiteaService _giteaService;
        public CreateRepoForOrgsCommandHandler(GiteaService giteaService)
        {
            _giteaService = giteaService;
        }

        public async Task<int> Handle(CreateRepoForOrgsCommand command)
        {
            var reposCreatedCounter = 0;
            var authenticatedUser = await _giteaService.GetAuthenticatedUser();

            foreach (var org in command.Orgs)
            {
                var repoName = command.PrefixRepoNameWithOrg ? $"{org}-{command.RepoName}" : command.RepoName;

                var giteaResponse = await _giteaService.GetRepo(org, repoName);
                if(giteaResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    var createRepoOption = GetCreateRepoOption(repoName);

                    giteaResponse = await _giteaService.CreateRepo(createRepoOption);
                    
                    if(giteaResponse.Success)
                    {
                        await _giteaService.TransferRepoOwnership(authenticatedUser.Username, repoName, org);
                        reposCreatedCounter++;
                    }
                }                
            }

            return reposCreatedCounter;
        }

        private CreateRepoOption GetCreateRepoOption(string repoName)
        {
            return new CreateRepoOption()
            {
                Name = repoName,
                AutoInit = true,
                DefaultBranch = "master",
                Private = false,
                TrustModel = TrustModel.@default
            };

        }
    }
}
