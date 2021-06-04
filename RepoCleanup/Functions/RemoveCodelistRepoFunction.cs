using RepoCleanup.Models;
using RepoCleanup.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RepoCleanup.Functions
{
    public static class RemoveCodelistRepoFunction
    {
        public static async Task Run()
        {
            CheckForDryRun();
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"Start time: {DateTime.Now}");
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Getting organisations...");
            List<Organisation> orgs = await GiteaService.GetOrganisations();

            Console.WriteLine("Getting users...");
            List<User> users = await GiteaService.GetUsers();

            Console.Write("Getting repositories...");
            List<Repository> repos = new List<Repository>();
            foreach (Organisation org in orgs)
            {
                Console.Write(".");
                repos.AddRange(await GetRepositories(null, org.Username));
            }

            foreach (User user in users)
            {
                Console.Write(".");
                repos.AddRange(await GetRepositories(user.Username, null));
            }

            Console.WriteLine();
            Console.WriteLine($"Total number of repositories: {repos.Count}");

            Console.WriteLine($"Filtering repositories...");
            List<Repository> filtered = await FilterRepos(repos);

            Console.WriteLine();
            Console.WriteLine($"Number of repositories to delete: {filtered.Count}");

            Console.WriteLine($"Deleting repositories...");
            if (Globals.IsDryRun)
            {
                using (System.IO.StreamWriter file = new System.IO.StreamWriter("ReposToDelete.txt", false))
                {
                    foreach (Repository repository in filtered)
                    {
                        Console.Write(".");
                        file.WriteLine($"Repo: {repository.Owner.Username}/{repository.Name} \t\t\t\t Last updated: {repository.Updated}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Repositories for deletion can be found in ReposToDelete.txt");
            }
            else
            {
                using (System.IO.StreamWriter writer = System.IO.File.AppendText("DeletedRepos.txt"))
                {
                    foreach (Repository repository in filtered)
                    {
                        Console.Write(".");
                        await DeleteRepository(repository, writer);
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Deleted repositories are can be found in DeletedRepos.txt");
            }

            Console.WriteLine($"Altinn Studio Repository cleanup completed { DateTime.Now}");

            Globals.Client.Dispose();
        }

        private static async Task<List<Repository>> GetRepositories(string username, string orgUsername)
        {
            string requestPath;
            if (!string.IsNullOrEmpty(orgUsername))
            {
                requestPath = $"orgs/{orgUsername}/repos";
            }
            else
            {
                requestPath = $"users/{username}/repos";
            }

            List<Repository> repos = new List<Repository>();
            int index = 1;

            while (true)
            {
                HttpResponseMessage res = await Globals.Client.GetAsync($"{requestPath}?page={index}");

                if (!res.IsSuccessStatusCode)
                {
                    break;
                }

                string jsonString = await res.Content.ReadAsStringAsync();
                List<Repository> retrievedRepos = JsonSerializer.Deserialize<List<Repository>>(jsonString);

                if (retrievedRepos.Count == 0)
                {
                    break;
                }

                repos.AddRange(retrievedRepos);
                ++index;
            }

            return repos;
        }


        private static async Task<List<Repository>> FilterRepos(List<Repository> repos)
        {
            List<Repository> filteredList = new List<Repository>();

            filteredList.AddRange(repos.Where(r => r.Created < new DateTime(2020, 1, 1) && r.Updated < new DateTime(2020, 1, 1)));

            filteredList.AddRange(repos.Where(r => r.Name.Equals("codelists")));

            filteredList.AddRange(repos.Where(r => r.Empty));

            filteredList = filteredList.Distinct().ToList();

            repos.RemoveAll(r => filteredList.Contains(r));
            foreach (Repository repo in repos)
            {
                Console.Write(".");
                List<File> files = await GiteaService.GetRepoContent(repo);
                if (files.Exists(f => f.Name.Equals("AltinnService.csproj")))
                {
                    filteredList.Add(repo);
                }
            }

            return filteredList;
        }

        private static async Task DeleteRepository(Repository repo, System.IO.StreamWriter writer)
        {
            HttpResponseMessage res = await Globals.Client.DeleteAsync($"repos/{repo.Owner.Username}/{repo.Name}");
            if (!res.IsSuccessStatusCode)
            {
                Console.WriteLine();
                Console.WriteLine($"Delete {repo.Owner.Username}/{repo.Name} incomplete. Failed with status code {res.StatusCode}: {await res.Content.ReadAsStringAsync()}.");
            }
            else
            {
                writer.WriteLine($"Repo: {repo.Owner.Username}/{repo.Name}");
            }
        }

        private static void CheckForDryRun()
        {
            Console.WriteLine("Press 'y' if you would like to delete the repositories. If not press any other key.");

            ConsoleKeyInfo cki = Console.ReadKey();

            if (cki.Key.ToString() == "Y")
            {
                Globals.IsDryRun = false;
            }
            else
            {
                Globals.IsDryRun = true;
            }
        }
    }
}
