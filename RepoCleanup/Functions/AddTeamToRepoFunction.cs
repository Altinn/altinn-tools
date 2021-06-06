using RepoCleanup.Application.CommandHandlers;
using RepoCleanup.Application.Commands;
using RepoCleanup.Models;
using RepoCleanup.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RepoCleanup.Functions
{
    public static class AddTeamToRepoFunction
    {
        public async static Task Run()
        {
            SharedFunctionSnippets.WriteHeader("Add existing team to repository");
            
            var orgs = await CollectOrgInfo();
            var prefixReponame = SharedFunctionSnippets.ShouldRepoNameBePrefixedWithOrg();
            var repoName = SharedFunctionSnippets.CollectRepoName();
            var teamName = SharedFunctionSnippets.CollectTeamName();

            SharedFunctionSnippets.ConfirmWithExit($"You are about to add team {teamName} in repo {repoName} for {orgs.Count} organisation(s). Proceed?", "Aborting, no teams added.");

            var command = new AddTeamToRepoCommand(orgs, repoName, prefixReponame, teamName);
            var commandHander = new AddTeamToRepoCommandHandler(new GiteaService());
            var result = await commandHander.Handle(command);

            Console.WriteLine($"Added {result} teams.");
        }

        private static async Task<List<string>> CollectOrgInfo()
        {
            List<string> orgs = new List<string>();

            bool updateAllOrgs = SharedFunctionSnippets.ShouldThisApplyToAllOrgs();

            if (updateAllOrgs)
            {
                List<Organisation> organisations = await GiteaService.GetOrganisations();
                orgs.AddRange(organisations.Select(o => o.Username));
            }
            else
            {
                var org = SharedFunctionSnippets.CollectOrgName();                
                orgs.Add(org);
            }

            return orgs;
        }
    }   
}
