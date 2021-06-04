using RepoCleanup.Application.Commands;
using RepoCleanup.Models;
using RepoCleanup.Services;
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

                var createResponse = await _giteaService.GetRepo(org, repoName);
                if(createResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    var createRepoOption = GetCreateRepoOption(repoName);

                    createResponse = await _giteaService.CreateRepo(createRepoOption);
                    
                    if(createResponse.Success)
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
