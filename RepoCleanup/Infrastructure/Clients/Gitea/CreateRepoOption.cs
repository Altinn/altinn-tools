using System.Text.Json.Serialization;

namespace RepoCleanup.Infrastructure.Clients.Gitea
{
    public class CreateRepoOption
    {
        [JsonPropertyName("auto_init")]
        public bool AutoInit { get; set; }

        [JsonPropertyName("default_branch")]
        public string DefaultBranch { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("gitignores")]
        public string GitIgnores { get; set; }

        [JsonPropertyName("issue_labels")]
        public string IssueLabels { get; set; }

        [JsonPropertyName("license")]
        public string License { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("private")]
        public bool Private { get; set; }

        [JsonPropertyName("readme")]
        public string ReadMe { get; set; }

        [JsonPropertyName("template")]
        public bool Template { get; set; }

        [JsonPropertyName("trust_model")]
        public TrustModel TrustModel { get; set; }
    }
}
