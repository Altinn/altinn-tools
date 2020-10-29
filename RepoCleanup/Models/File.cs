﻿using System.Text.Json.Serialization;

namespace RepoCleanup.Models
{
    public class File
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; }


        [JsonPropertyName("type")]
        public string Type { get; set; }
    }
}
