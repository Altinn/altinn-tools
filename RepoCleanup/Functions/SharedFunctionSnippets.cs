using System;

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
            return CollectInput("Provide organisation name: ");
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
