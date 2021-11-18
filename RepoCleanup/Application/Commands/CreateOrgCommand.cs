using System;

namespace RepoCleanup.Application.Commands
{
    public class CreateOrgCommand
    {
        public CreateOrgCommand(string shortName, string fullName, string website)
        {
            if (string.IsNullOrEmpty(shortName))
            {
                throw new ArgumentException($"'{nameof(shortName)}' cannot be null or empty.", nameof(shortName));
            }

            if (string.IsNullOrEmpty(fullName))
            {
                throw new ArgumentException($"'{nameof(fullName)}' cannot be null or empty.", nameof(fullName));
            }

            if (string.IsNullOrEmpty(website))
            {
                throw new ArgumentException($"'{nameof(website)}' cannot be null or empty.", nameof(website));
            }
            ShortName = shortName;
            FullName = fullName;
            Website = website;
        }

        public string ShortName { get; }
        public string FullName { get; }
        public string Website { get; }
    }
}
