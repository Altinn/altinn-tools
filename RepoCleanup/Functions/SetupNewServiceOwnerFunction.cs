using RepoCleanup.Application.CommandHandlers;
using RepoCleanup.Application.Commands;
using RepoCleanup.Infrastructure.Clients.Gitea;
using System;
using System.Threading.Tasks;

namespace RepoCleanup.Functions
{
    public static class SetupNewServiceOwnerFunction
    {
        public static async Task Run()
        {
            SharedFunctionSnippets.WriteHeader("Setup a new service owner in Gitea with all teams and default repositories.");

            var giteaService = new GiteaService();

            // Create new org
            Organisation org = SharedFunctionSnippets.CollectNewOrgInfo();
            var createOrgCommandHandler = new CreateOrgCommandHandler(giteaService);
            bool orgCreated = await createOrgCommandHandler.Handle(new CreateOrgCommand(org.Username, org.Fullname, org.Website));

            if (!orgCreated)
            {
                Console.WriteLine($"Could not create org {org.Fullname}");
                return;
            }
            else
            {
                Console.WriteLine($"Created org {org.Fullname}");
            }

            // Create default teams
            var createDefaultTeamsCommandHandler = new CreateDefaultTeamsCommandHandler(giteaService);
            var teamsCreated = await createDefaultTeamsCommandHandler.Handle(new CreateDefaultTeamsCommand(org.Username));
            if (!teamsCreated)
            {
                Console.WriteLine($"Could not create all default teams for {org.Fullname}");
                return;
            }
            else
            {
                Console.WriteLine($"Created all default teams for {org.Fullname}");
            }

            // Create default repositories
            var isDatamodelRepoCreated = await CreateRepoWithPrefix(giteaService, org, "datamodels");
            var isContentRepoCreated = await CreateRepoWithPrefix(giteaService, org, "content");
            var isResourceRepoCreated = await CreateRepoWithPrefix(giteaService, org, "resources");

            if (isDatamodelRepoCreated && isContentRepoCreated && isResourceRepoCreated)
            {
                Console.WriteLine($"Added all default repositories for {org.Fullname}");
            }

            // Ensure that datamodels and resources teams have write access to the datamodels and resources repos respectively
            var addTeamToRepoCommandHandler = new AddTeamToRepoCommandHandler(giteaService);
            int datamodelsTeamAddedToDatamodelsRepo = await addTeamToRepoCommandHandler.Handle(new AddTeamToRepoCommand([org.Username], "datamodels", true, "Datamodels"));
            int resourcesTeamAddedToResourcesRepo = await addTeamToRepoCommandHandler.Handle(new AddTeamToRepoCommand([org.Username], "resources", true, "Resources"));

            if (datamodelsTeamAddedToDatamodelsRepo == 0)
            {
                Console.WriteLine($"Could not add Datamodels team to datamodels repo for {org.Fullname}");
            }
            else
            {
                Console.WriteLine($"Added Datamodels team to datamodels repo for {org.Fullname}");
            }

            if (resourcesTeamAddedToResourcesRepo == 0)
            {
                Console.WriteLine($"Could not add Resources team to resources repo for {org.Fullname}");
            }
            else
            {
                Console.WriteLine($"Added Resources team to resources repo for {org.Fullname}");
            }

            Console.WriteLine("Done setting up new service owner in Gitea!");
        }

        private static async Task<bool> CreateRepoWithPrefix(GiteaService giteaService, Organisation org, string repoName)
        {
            var createRepoForOrgsCommandHandler = new CreateRepoForOrgsCommandHandler(giteaService);
            var numberOfReposCreated = await createRepoForOrgsCommandHandler.Handle(new CreateRepoForOrgsCommand([org.Username],  repoName, true));
            if (numberOfReposCreated != 1)
            {
                Console.WriteLine($"Could not create default {repoName} repository for {org.Fullname}");
                return false;
            }
            else
            {
                Console.WriteLine($"Created default {repoName} repository for {org.Fullname}");
                return true;
            }
        }
    }
}
