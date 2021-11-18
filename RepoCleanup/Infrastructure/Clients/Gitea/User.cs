using System.Text.Json.Serialization;

namespace RepoCleanup.Infrastructure.Clients.Gitea
{
    public class User
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

    }
}
