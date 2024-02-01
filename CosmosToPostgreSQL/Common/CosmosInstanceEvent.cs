using Altinn.Platform.Storage.Interface.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Common
{
    public class CosmosInstanceEvent : InstanceEvent
    {
        [JsonProperty(PropertyName = "_ts")]
        [JsonPropertyName("_ts")]
        public long Ts { get; set; }
    }
}
