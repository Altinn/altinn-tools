using RepoCleanup.Infrastructure.Clients.Gitea;
using RepoCleanup.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RepoCleanup.Functions
{
    public static class CreateTeamForOrgsFunction
    {
        public static async Task Run()
        {
            List<string> successfulOrgs = new List<string>();
            List<string> failedOrgs = new List<string>();
            StringBuilder logBuilder = new StringBuilder();
            string logName = @$"CreateTeamForOrgs-Log.txt";

            Console.Clear();
            Console.WriteLine("\r\n----------------------------------------------------------------");
            Console.WriteLine("------------ Create new team for organisation(s) ---------------");
            Console.WriteLine("----------------------------------------------------------------");

            List<string> orgsToUpdate = await CollectOrgInfo();
            CreateTeamOption teamOption = CollectTeamInfo();

            Console.WriteLine("Updating teams. Please wait.");
            foreach (string org in orgsToUpdate)
            {
                GiteaResponse response = await new GiteaService().CreateTeam(org, teamOption);

                if (response.Success)
                {
                    successfulOrgs.Add(org);
                    logBuilder.AppendLine($"{DateTime.Now} - Information - Team {teamOption.Name} was successfully added to org: {org}.");
                }
                else
                {
                    failedOrgs.Add(org);
                    logBuilder.AppendLine($"{DateTime.Now} - Error - Team {teamOption.Name} was not added to org {org}.");
                    logBuilder.AppendLine($"{DateTime.Now} - Error -  {response.StatusCode}:{JsonSerializer.Serialize(response.ResponseMessage, Globals.SerializerOptions)}.");
                }
            }

            using (StreamWriter file = new StreamWriter(logName, true))
            {
                file.WriteLine(logBuilder.ToString());
            }

            logBuilder.Clear();

            if (failedOrgs.Count > 0)
            {
                Console.WriteLine($"Update failed for {failedOrgs.Count}/{orgsToUpdate.Count} organisations. See log {logName}.");
            }
            else
            {
                Console.WriteLine("All orgs successfully updated.");
            }
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

        private static CreateTeamOption CollectTeamInfo()
        {
            string name = SetTeamName();

            Console.Write("\r\nSet team description: ");
            string description = Console.ReadLine();

            Console.Write("\r\nCan create org repositories? (Y/N):");
            bool canCreateOrgRepo = false;

            switch (Console.ReadLine().ToUpper())
            {
                case "Y":
                    canCreateOrgRepo = true;
                    break;
                case "N":
                    canCreateOrgRepo = false;
                    break;
                default:
                    break;
            }

            Console.WriteLine("\r\nSet permission level for the team.");
            Console.WriteLine("1) Read");
            Console.WriteLine("2) Write");
            Console.WriteLine("3) Admin");
            Console.Write("\r\nSelect an option: ");

            Permission permission = Permission.read;

            switch (Console.ReadLine())
            {
                case "1":
                    permission = Permission.read;
                    break;
                case "2":
                    permission = Permission.write;
                    break;
                case "3":
                    permission = Permission.admin;
                    break;
                default:
                    break;
            }

            CreateTeamOption teamOption = TeamOption.GetCreateTeamOption(name, description, canCreateOrgRepo, permission);

            Console.WriteLine("\r\n----------------------------------------------------------------");
            Console.WriteLine("------------------ Review team confiruation --------------------");
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine(JsonSerializer.Serialize(teamOption, Globals.SerializerOptions));
            Console.Write("\r\nAre you happy with the configuration? (Y)es / (N)o.Start over: ");

            switch (Console.ReadLine().ToUpper())
            {
                case "Y":
                    return teamOption;
                default:
                    Console.Clear();
                    Console.WriteLine("Starting over...");
                    return CollectTeamInfo();
            }
        }

        private static string SetTeamName()
        {
            bool isValidName = false;
            string name = string.Empty;

            while (!isValidName)
            {
                Console.Write("\r\nSet team name: ");
                name = Console.ReadLine();

                isValidName = Regex.IsMatch(name, "^[a-zA-Z0-9\\-._]*$");
                if (!isValidName)
                {
                    Console.WriteLine("Invalid name. Letters a-z and characters:'-', '_', '.' are permitted.");
                }
            }

            return name;
        }
    }
}
