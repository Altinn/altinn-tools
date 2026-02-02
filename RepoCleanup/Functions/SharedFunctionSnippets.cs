using RepoCleanup.Infrastructure.Clients.Gitea;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RepoCleanup.Functions
{
    public static class SharedFunctionSnippets
    {
        private const int HEADER_WIDTH = 80;

        public static void WriteHeader(string header)
        {
            Console.Clear();
            Console.WriteLine();
            Console.WriteLine("-------------------------------------------------------------------------------");
            Console.WriteLine(CenterText(header, HEADER_WIDTH, '-'));
            Console.WriteLine("-------------------------------------------------------------------------------");
            Console.WriteLine();
        }

        public static bool ShouldRepoNameBePrefixedWithOrg()
        {
            return YesNo("Should repository name be prefixed with {org}-?");
        }

        public static string CollectRepoName()
        {
            return CollectInput("Provide repository name: ");
        }

        public static string CollectTeamName()
        {
            return CollectInput("Provide team name (must exist): ");
        }

        public static string CollectInput(string inputLabel)
        {
            Console.Write(inputLabel);
            var inputValue = Console.ReadLine();

            return inputValue;
        }

        public static async Task<List<string>> CollectExistingOrgsInfo()
        {
            List<string> orgs = new List<string>();

            bool updateAllOrgs = ShouldThisApplyToAllOrgs();

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

        public static bool ShouldThisApplyToAllOrgs()
        {
            return YesNo("Should this apply to all organisations?");
        }

        public static Organisation CollectNewOrgInfo()
        {
            var shortName = CollectShortNameForOrg();
            var fullname = CollectFullNameForOrg();
            var website = CollectWebSiteForOrg();

            var org = new Organisation
            {
                Username = shortName,
                Fullname = fullname,
                Website = website,
                Visibility = "public",
                RepoAdminChangeTeamAccess = false
            };

            return org;
        }

        private static string CollectWebSiteForOrg()
        {
            var isValid = false;
            var website = string.Empty;

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

            return website;
        }

        private static string CollectFullNameForOrg()
        {
            return CollectInput("\r\nSet fullname for org: ");
        }

        private static string CollectShortNameForOrg()
        {
            var isValid = false;
            var shortName = string.Empty;
            while (!isValid)
            {
                Console.Write("\r\nSet shortname for org: ");
                shortName = Console.ReadLine().ToLower();

                isValid = IsValidOrgShortName(shortName);
                if (!isValid)
                {
                    Console.WriteLine("Invalid short name. Letters a-z and numbers are permitted. Name must start with a letter.");
                }
            }

            return shortName;
        }

        private static bool IsValidOrgShortName(string shortName)
        {
            return Regex.IsMatch(shortName, "^[a-z][a-z0-9]+$");
        }

        public static void ConfirmWithExit(string confirmMessage, string exitMessage)
        {
            var proceed = YesNo(confirmMessage);
            
            if (!proceed)
            {
                Console.WriteLine(exitMessage);
                Environment.Exit(0);
            }
        }

        private static bool YesNo(string question)
        {
            Console.Write($"{question} (Y)es / (N)o : ");
            string yesNo = Console.ReadLine().ToUpper();

            return yesNo == "Y";
        }

        private static string CenterText(string text, int length, char padChar)
        {
            int pad = (length - text.Length) / 2;

            int leftPad = pad - 1;
            int rightPad = (pad % 2 == 0) ? pad - 1 : pad;

            var left = "".PadLeft(leftPad, padChar);
            var right = "".PadRight(rightPad, padChar);

            return $"{left} {text} {right}";
        }
    }
}
