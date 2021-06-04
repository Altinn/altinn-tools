using System;
using System.Collections.Generic;

using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using RepoCleanup.Functions;
using RepoCleanup.Models;

namespace RepoCleanup
{
    public class Program
    {
        static async Task Main(string[] args)
        {
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
            Console.WriteLine("3) Create org with all teams");
            Console.WriteLine("4) Import organizations from json file");
            Console.WriteLine("5) Create repository for organisation(s)");
            Console.WriteLine("6) Add existing team to repository for organisation(s)");
            Console.WriteLine("9) Exit");
            Console.Write("\r\nSelect an option: ");

            switch (Console.ReadLine())
            {
                case "1":
                    await RemoveCodelistRepo.Run();
                    return;
                case "2":
                    await CreateTeamForOrgs.Run();
                    return;
                case "3":
                    await CreateOrgWithTeams.Run();
                    return;
                case "4":
                    await CreateOrgWithTeams.RunFromFile();
                    return;
                case "5":
                    await CreateRepoFunction.Run();
                    return;
                case "6":
                    await AddTeamToRepoFunction.Run();
                    return;
                case "9":
                default:
                    return;
            }
        }

        private static void SetUpClient()
        {
            string url = string.Empty;
            Enums.Environment env = SelectEnvironment();

            switch (env)
            {
                case Enums.Environment.Development:
                    url = "https://dev.altinn.studio/repos/api/v1/";
                    break;
                case Enums.Environment.Staging:
                    url = "https://staging.altinn.studio/repos/api/v1/";
                    break;
                case Enums.Environment.Production:
                    url = "https://altinn.studio/repos/api/v1/";
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
        }

        private static Enums.Environment SelectEnvironment()
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Choose an environment:");
            Console.WriteLine("1) Development");
            Console.WriteLine("2) Staging");
            Console.WriteLine("3) Production");
            Console.WriteLine();
            Console.Write("Select an option: ");

            switch (Console.ReadLine())
            {
                case "1":
                    return Enums.Environment.Development;
                case "2":
                    return Enums.Environment.Staging;
                case "3":
                    return Enums.Environment.Production;
                default:
                    return Enums.Environment.Development;
            }
        }

        private static string GetAccessToken(Enums.Environment env)
        {
            string url = string.Empty;

            Console.WriteLine();
            Console.WriteLine("The application requires an API key with admin access.");
            switch (env)
            {
                case Enums.Environment.Development:
                    url = "https://dev.altinn.studio/repos/user/settings/applications";
                    break;
                case Enums.Environment.Staging:
                    url = "https://staging.altinn.studio/repos/user/settings/applications";
                    break;
                case Enums.Environment.Production:
                    url = "https://altinn.studio/repos/user/settings/applications";
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
