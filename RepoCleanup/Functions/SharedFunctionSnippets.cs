using System;
using System.Text.RegularExpressions;

namespace RepoCleanup.Functions
{
    public static class SharedFunctionSnippets
    {
        private const int HEADER_WIDTH = 96;

        public static void WriteHeader(string header)
        {
            Console.Clear();
            Console.WriteLine();
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine(CenterText(header, HEADER_WIDTH, char.Parse("-")));
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine();
        }

        public static bool ShouldThisApplyToAllOrgs()
        {
            return YesNo("Should this apply to all organisations ?");
        }

        public static string CollectOrgName()
        {
            return CollectInput("Provide organisation name (short name): ");
        }

        public static string CollectOrgNameValidated()
        {
            bool isValid = false;
            string username = string.Empty;

            while (!isValid)
            {
                Console.Write("Provide user name for organisation (short name): ");
                username = Console.ReadLine().ToLower();

                isValid = Regex.IsMatch(username, "^[a-z]+[a-z0-9\\-]+[a-z0-9]$");
                if (!isValid)
                {
                    Console.WriteLine("Invalid name. Letters a-z and character '-' are permitted. Name must start with a letter and end with a letter or number.");
                }
            }

            return username;
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

        public static string CollectWebsiteValidated()
        {
            bool isValid = false;
            string website = string.Empty;

            while (!isValid)
            {
                Console.Write("Provide website adress for org: ");
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

        public static string CollectPathValidated(string inputLabel, string defaultPathToOrgsJsonFile)
        {
            bool isValid = false;
            string pathToOrgsJsonFile = string.Empty;

            while (!isValid)
            {
                Console.Write(inputLabel);
                pathToOrgsJsonFile = Console.ReadLine();
                pathToOrgsJsonFile = string.IsNullOrEmpty(pathToOrgsJsonFile) ? defaultPathToOrgsJsonFile : pathToOrgsJsonFile;

                if (System.IO.File.Exists(pathToOrgsJsonFile))
                {
                    Console.WriteLine("Can't find the specified file!");
                    isValid = false;
                }
                else
                {
                    isValid = true;
                }
            }

            return pathToOrgsJsonFile;
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
