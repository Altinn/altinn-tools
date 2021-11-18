using System.Text.Json.Serialization;

namespace RepoCleanup.Models
{
    public class TransferRepoOption
    {
        public TransferRepoOption(string newOwner)
        {
            NewOwner = newOwner;
        }

        [JsonPropertyName("new_owner")]
        public string NewOwner { get; set; }
    }
}
