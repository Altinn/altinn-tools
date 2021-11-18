using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RepoCleanup.Infrastructure.Clients.Gitea
{
    public enum Permission
    {
        read,
        write,
        admin
    }

    public class CreateTeamOption
    {
        [JsonPropertyName("can_create_org_repo")]
        public bool CanCreateOrgRepo { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("includes_all_repositories")]
        public bool IncludesAllRepositories { get; set; } = true;

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("permission")]
        public Permission Permission { get; set; }

        [JsonPropertyName("units")]
        public List<string> Units { get; set; } = new List<string> { "repo.code", "repo.issues", "repo.ext_issues", "repo.wiki", "repo.pulls", "repo.releases", "repo.ext_wiki" };
    }
}
