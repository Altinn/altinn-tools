namespace RepoCleanup.Application.Commands
{
    public class CreateDefaultTeamsCommand
    {
        public CreateDefaultTeamsCommand(string orgShortName)
        {
            if (string.IsNullOrEmpty(orgShortName))
            {
                throw new System.ArgumentException($"'{nameof(orgShortName)}' cannot be null or empty.", nameof(orgShortName));
            }

            OrgShortName = orgShortName;
        }

        public string OrgShortName { get; }
    }
}
