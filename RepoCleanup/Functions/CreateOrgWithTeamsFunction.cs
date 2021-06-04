using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RepoCleanup.Models;
using RepoCleanup.Services;
using RepoCleanup.Utils;

namespace RepoCleanup.Functions
{
    public static class CreateOrgWithTeamsFunction
    {
        public static async Task Run()
        {
            WriteHeader();

            Organisation org = CollectOrgInfo();

            await CreateOrgAndTeams(org);
        }        

        public async static Task RunFromFile()
        {
            WriteHeader();

            var defaultPathToOrgsJsonFile = @"data/orgs.json";
            var (pathToOrgsJsonFile, fileExits) = CollectPathToOrgsFile(defaultPathToOrgsJsonFile);

            if (!fileExits)
            {
                return;
            }

            List<Organisation> organisations = ParseOrganisationsFromFile(pathToOrgsJsonFile);

            await CreateAllOrgsAndTeams(organisations);
        }

        private static void WriteHeader()
        {
            Console.Clear();
            Console.WriteLine("\r\n----------------------------------------------------------------");
            Console.WriteLine("------------ Create new organisation(s) with teams ----------------");
            Console.WriteLine("----------------------------------------------------------------");
        }

        private static Tuple<string, bool> CollectPathToOrgsFile(string defaultPathToOrgsJsonFile)
        {
            Console.Write("Path to JSON file with array of organisations. Leave empty to use default data\\orgs.json: ");
            var pathToOrgsJsonFile = Console.ReadLine();
            pathToOrgsJsonFile = string.IsNullOrEmpty(pathToOrgsJsonFile) ? defaultPathToOrgsJsonFile : pathToOrgsJsonFile;

            if (!System.IO.File.Exists(pathToOrgsJsonFile))
            {
                Console.WriteLine("Can't find the specified file!");
                return new Tuple<string, bool>("", false);
            }

            return new Tuple<string, bool>(pathToOrgsJsonFile, true);
        }

        private static List<Organisation> ParseOrganisationsFromFile(string pathToOrgsJsonFile)
        {
            string json = System.IO.File.ReadAllText(pathToOrgsJsonFile);
            var organisations = JsonSerializer.Deserialize<List<Organisation>>(json);

            return organisations;
        }

        private static async Task CreateAllOrgsAndTeams(List<Organisation> organisations)
        {
            Console.WriteLine($"Found {organisations.Count} organisations in file.");
            Console.Write($"Continue to create all organisations (existing orgs won't change)? (Y)es / (N)o: ");
            var confirm = Console.ReadLine().ToUpper();

            if (confirm == "N")
            {
                Console.WriteLine("Aborting, 0 organisations created.");
                return;
            }

            int orgsCreated = 0;
            foreach (var org in organisations)
            {
                var created = await CreateOrgAndTeams(org);
                if (created)
                {
                    orgsCreated++;
                }
            }

            Console.WriteLine($"Created {orgsCreated} organisations out of {organisations.Count} specified.");
        }

        private static Organisation CollectOrgInfo()
        {
            bool isValid = false;
            string username = string.Empty;

            while (!isValid)
            {
                Console.Write("\r\nSet username (shortname) for org: ");
                username = Console.ReadLine().ToLower();

                isValid = Regex.IsMatch(username, "^[a-z]+[a-z0-9\\-]+[a-z0-9]$");
                if (!isValid)
                {
                    Console.WriteLine("Invalid name. Letters a-z and character '-' are permitted. Username must start with a letter and end with a letter or number.");
                }
            }

            string fullname = string.Empty;

            Console.Write("\r\nSet fullname for org: ");
            fullname = Console.ReadLine();

            isValid = false;
            string website = string.Empty;

            while (!isValid)
            {
                Console.Write("\r\nSet website for org: ");
                website = Console.ReadLine();

                if (string.IsNullOrEmpty(website))
                {
                    isValid = true;
                }
                else
                {
                    isValid = Regex.IsMatch(website, "^[a-zA-Z0-9\\-._/:]*$");
                }
                if (!isValid)
                {
                    Console.WriteLine("Invalid website adress. Letters a-z and characters:'-', '_', '.', '/', ':' are permitted.");
                }
            }

            Console.WriteLine();

            Organisation org = new Organisation();
            org.Username = username;
            org.Fullname = fullname;
            org.Website = website;
            org.Visibility = "public";
            org.RepoAdminChangeTeamAccess = false;
            return org;
        }

        private static async Task<bool> CreateOrgAndTeams(Organisation org)
        {
            bool createdOrg = false;
            Console.WriteLine($"Creating org {org.Fullname} ({org.Username}). Please wait.");
            GiteaResponse response = await GiteaService.CreateOrg(org);

            if (response.Success)
            {
                Console.WriteLine($"{DateTime.Now} - Information - Org {org.Username} was successfully added to Gitea.");
                createdOrg = true;
            }
            else
            {
                Console.WriteLine($"{DateTime.Now} - Error - Org {org.Username} was not added to Gitea.");
                Console.WriteLine($"{DateTime.Now} - Error -  {response.StatusCode}:{JsonSerializer.Serialize(response.ResponseMessage, Globals.SerializerOptions)}.");
            }

            if (createdOrg)
            {
                Console.WriteLine($"Org {org.Fullname} ({org.Username}) successfully created.");
                await CreateTeams(org);
            }
            else
            {
                Console.WriteLine($"Create org for {org.Fullname} ({org.Username}) failed. See details above.");
            }

            return createdOrg;
        }

        private static async Task CreateTeams(Organisation org)
        {
            bool createdTeams = true;
            List<CreateTeamOption> teams = new List<CreateTeamOption>();
            teams.Add(TeamOption.GetCreateTeamOption("Deploy-Production", "Members can deploy to production", false, Permission.read));
            teams.Add(TeamOption.GetCreateTeamOption("Deploy-TT02", "Members can deploy to TT02", false, Permission.read));
            teams.Add(TeamOption.GetCreateTeamOption("Devs", "All application developers", true, Permission.write));

            Console.WriteLine("Creating teams for org. Please wait.");
            foreach (CreateTeamOption team in teams)
            {
                GiteaResponse response = await GiteaService.CreateTeam(org.Username, team);

                if (response.Success)
                {
                    Console.WriteLine($"{DateTime.Now} - Information - Team {team.Name} was successfully added to org: {org.Username}.");
                }
                else
                {
                    createdTeams = false;
                    Console.WriteLine($"{DateTime.Now} - Error - Team {team.Name} was not added to org {org.Username}.");
                    Console.WriteLine($"{DateTime.Now} - Error -  {response.StatusCode}:{JsonSerializer.Serialize(response.ResponseMessage, Globals.SerializerOptions)}.");
                }
            }

            if (createdTeams)
            {
                Console.WriteLine($"All teams for {org.Username} successfully created.");
            }
            else
            {
                Console.WriteLine($"Create teams for {org.Username} failed.");
            }
        }
    }
}