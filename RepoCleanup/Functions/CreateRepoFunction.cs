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
    public class CreateRepoFunction
    {
        public async static Task Run()
        {
            WriteHeader();

            var orgs = await CollectOrgInfo();
            var prefixRepoNameWithOrg = CollectPrefixRepoNameWithOrg();
            var repoName = CollectRepoName();

            Console.Write($"You are about to create a new repository for {orgs.Count} organisation(s). Proceed? (Y)es / (N)o: ");
            var proceed = Console.ReadLine().ToUpper();
            if(proceed == "N")
            {
                Console.WriteLine("Aborting, no repositories created.");
                return;
            }

            var command = new CreateRepoForOrgsCommand(orgs, repoName, prefixRepoNameWithOrg);
            var commandHander = new CreateRepoForOrgsCommandHandler(new GiteaService());
            var result = await commandHander.Handle(command);

            Console.WriteLine($"Created {result} repositories.");
        }

        private static void WriteHeader()
        {
            Console.Clear();
            Console.WriteLine();
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine("--------- Create new repository for organisation(s) ------------");
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine();
        }

        private static async Task<List<string>> CollectOrgInfo()
        {
            List<string> orgs = new List<string>();

            bool updateAllOrgs = CheckIfAllOrgs();

            if (updateAllOrgs)
            {
                List<Organisation> organisations = await GiteaService.GetOrganisations();
                orgs.AddRange(organisations.Select(o => o.Username));
            }
            else
            {
                Console.Write("\r\nProvide organisation name: ");

                string name = Console.ReadLine();
                orgs.Add(name);
            }

            return orgs;
        }

        private static bool CollectPrefixRepoNameWithOrg()
        {
            Console.Write("Should repository name be prefixed with {org}-? (Y)es / (N)o: ");
            var prefixWithOrg = Console.ReadLine().ToUpper();

            if (prefixWithOrg == "Y")
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        private static string CollectRepoName()
        {
            Console.Write("Provide repository name: ");
            var repositoryName = Console.ReadLine();

            return repositoryName;
        }

        private static bool CheckIfAllOrgs()
        {
            Console.Write("\r\nShould the team be created for all organisations? (Y)es / (N)o: ");
            bool updateAllOrgs = false;

            switch (Console.ReadLine().ToUpper())
            {
                case "Y":
                    updateAllOrgs = true;
                    break;
                case "N":
                    updateAllOrgs = false;
                    break;
                default:
                    return CheckIfAllOrgs();
            }
            return updateAllOrgs;
        }
    }
}
