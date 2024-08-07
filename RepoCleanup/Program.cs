﻿using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RepoCleanup.Functions;

namespace RepoCleanup
{
    public class Program
    {
        protected Program()
        {
        }

        static async Task Main(string[] args)
        {
            var logger = Globals.CreateLogger<Program>();
            logger.LogWarning("this time with config.");
            Console.Clear();
            Console.WriteLine("Altinn Studio Repository cleanup");
            SetUpClient();
            await SelectFunction();
        }

        private static async Task SelectFunction()
        {
            Console.WriteLine("\r\nChoose a function:");
            Console.WriteLine("1) Remove codelists repositories");
            Console.WriteLine("2) Create team for organisation(s)");
            Console.WriteLine("3) Create org with all teams (OBSOLETE. USE 9)");
            Console.WriteLine("4) Import organizations from json file");
            Console.WriteLine("5) Create repository for organisation(s)");
            Console.WriteLine("6) Add existing team to repository for organisation(s)");
            Console.WriteLine("7) Migrate Altinn II XSD Schema for organisation(s)");
            Console.WriteLine("8) Delete repository for organisation(s)");
            Console.WriteLine("9) Setup a new service owner in Gitea with all teams and default repository");
            Console.WriteLine("0) Exit");
            Console.Write("\r\nSelect an option: ");

            switch (Console.ReadLine())
            {
                case "1":
                    await RemoveCodelistRepoFunction.Run();
                    return;
                case "2":
                    await CreateTeamForOrgsFunction.Run();
                    return;
                case "3":
                    await CreateOrgWithTeamsFunction.Run();
                    return;
                case "4":
                    await CreateOrgWithTeamsFunction.RunFromFile();
                    return;
                case "5":
                    await CreateRepoFunction.Run();
                    return;
                case "6":
                    await AddTeamToRepoFunction.Run();
                    return;
                case "7":
                    await MigrateXsdSchemasFunction.Run();
                    return;
                case "8":
                    await DeleteDatamodelsRepoFunction.Run();
                    return;
                case "9":
                    await SetupNewServiceOwnerFunction.Run();
                    return;
                case "0":
                default:
                    return;
            }
        }

        private static void SetUpClient()
        {
            string url = string.Empty;
            Application.Models.Environment env = SelectEnvironment();

            switch (env)
            {
                case Application.Models.Environment.Development:
                    url = "https://dev.altinn.studio/repos/api/v1/";
                    break;
                case Application.Models.Environment.Staging:
                    url = "https://staging.altinn.studio/repos/api/v1/";
                    break;
                case Application.Models.Environment.Production:
                    url = "https://altinn.studio/repos/api/v1/";
                    break;
                case Application.Models.Environment.Local:
                    url = "http://studio.localhost/repos/api/v1/";
                    break;
                default:
                    SelectEnvironment();
                    break;
            }

            string token = GetAccessToken(env);
            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri(url);
            client.DefaultRequestHeaders.Add("Authorization", $"token {token}");
            Globals.Client = client;
            Globals.GiteaToken = token;
            Globals.RepositoryBaseUrl = GetRepositoryBaseUrl(env);
        }

        private static string GetRepositoryBaseUrl(Application.Models.Environment env)
        {
            switch (env)
            {
                case Application.Models.Environment.Development:
                    return "https://dev.altinn.studio/repos/";
                case Application.Models.Environment.Staging:
                    return "https://staging.altinn.studio/repos/";
                case Application.Models.Environment.Production:
                    return "https://altinn.studio/repos/";
                case Application.Models.Environment.Local:
                    return "http://studio.localhost/repos/";
            }
            
            return string.Empty;
        }

        private static Application.Models.Environment SelectEnvironment()
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Choose an environment:");
            Console.WriteLine("1) Development");
            Console.WriteLine("2) Staging");
            Console.WriteLine("3) Production");
            Console.WriteLine("4) Local (altinn3.no)");
            Console.WriteLine();
            Console.Write("Select an option: ");

            switch (Console.ReadLine())
            {
                case "1":
                    return Application.Models.Environment.Development;
                case "2":
                    return Application.Models.Environment.Staging;
                case "3":
                    return Application.Models.Environment.Production;
                case "4":
                    return Application.Models.Environment.Local;
                default:
                    return Application.Models.Environment.Development;
            }
        }

        private static string GetAccessToken(Application.Models.Environment env)
        {
            string url = string.Empty;

            Console.WriteLine();
            Console.WriteLine("The application requires an API key with admin access.");
            switch (env)
            {
                case Application.Models.Environment.Development:
                    url = "https://dev.altinn.studio/repos/user/settings/applications";
                    break;
                case Application.Models.Environment.Staging:
                    url = "https://staging.altinn.studio/repos/user/settings/applications";
                    break;
                case Application.Models.Environment.Production:
                    url = "https://altinn.studio/repos/user/settings/applications";
                    break;
                case Application.Models.Environment.Local:
                    url = "http://studio.localhost/repos/user/settings/applications";
                    break;
            }

            Console.WriteLine($"Tokens can be generated on this page: {url}");
            Console.WriteLine();
            Console.Write("Provide token: ");

            string token = Console.ReadLine().Trim();
            if (token.Length != 40)
            {
                Console.Write("Invalid token.");
                return GetAccessToken(env);
            }

            return token;
        }
    }
}
