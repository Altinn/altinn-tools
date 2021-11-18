using RepoCleanup.Application.CommandHandlers;
using RepoCleanup.Application.Commands;
using RepoCleanup.Infrastructure.Clients.Gitea;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RepoCleanup.Functions
{
    public static class SetupNewServiceOwnerFunction
    {
        public static async Task Run()
        {
            SharedFunctionSnippets.WriteHeader("Setup a new service owner in Gitea with all teams and default repository.");

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
            
            // Create default datamodels repository
            var createRepoForOrgsCommandHandler = new CreateRepoForOrgsCommandHandler(giteaService);
            var numberOfReposCreated = await createRepoForOrgsCommandHandler.Handle(new CreateRepoForOrgsCommand(new List<string>() { org.Username }, "datamodels", true));
            if (numberOfReposCreated != 1)
            {
                Console.WriteLine($"Could not create default datamodels repository");
                return;
            }
            else
            {
                Console.WriteLine($"Created default datamodels repository for {org.Fullname}.");
            }

            Console.WriteLine("Done setting up new serivce owner in Gitea!");
        }
    }
}
