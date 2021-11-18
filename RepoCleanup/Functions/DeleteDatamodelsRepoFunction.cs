using RepoCleanup.Application.CommandHandlers;
using RepoCleanup.Application.Commands;
using RepoCleanup.Infrastructure.Clients.Gitea;
using System;
using System.Threading.Tasks;

namespace RepoCleanup.Functions
{
    public static class DeleteDatamodelsRepoFunction
    {
        public static async Task Run()
        {
            SharedFunctionSnippets.WriteHeader("Delete datamodels repo for all oranisations");

            var orgs = await SharedFunctionSnippets.CollectExistingOrgsInfo();
            var prefixRepoNameWithOrg = SharedFunctionSnippets.ShouldRepoNameBePrefixedWithOrg();
            var repoName = SharedFunctionSnippets.CollectRepoName();

            SharedFunctionSnippets.ConfirmWithExit($"You are about to delete repository {repoName} for {orgs.Count} organisation(s). Proceed?", "Aborting, no repositories deleted.");

            var command = new DeleteRepoForOrgsCommand(orgs, repoName, prefixRepoNameWithOrg);
            var commandHander = new DeleteRepoForOrgsCommandHandler(new GiteaService());
            var result = await commandHander.Handle(command);

            Console.WriteLine($"Deleted {result} repositories.");
        }
    }
}
