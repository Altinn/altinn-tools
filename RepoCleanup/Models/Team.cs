using System.Text.Json.Serialization;

namespace RepoCleanup.Models
{
    public class Team
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("organization")]
        public Organisation Organisation { get; set; }
    }
}
