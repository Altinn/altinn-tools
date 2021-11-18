using System;
using System.Text.Json.Serialization;

namespace RepoCleanup.Infrastructure.Clients.Gitea
{
    public class Repository
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("owner")]
        public User Owner { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime Created { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime Updated { get; set; }

        [JsonPropertyName("empty")]
        public bool Empty { get; set; }
    }
}
