using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

using AltinnReStorage.Configuration;

using Azure.Core;

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

using Newtonsoft.Json.Linq;

namespace AltinnReStorage.Services
{
    /// <inheritdoc/>
    public class CosmosClientProvider : ICosmosClientProvider
    {
        private readonly IAccessTokenService _accessTokenService;
        private readonly Dictionary<string, CosmosAccountConfig> _accountConfig;
        private readonly Dictionary<string, CosmosClient> _clients = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="CosmosClientProvider"/> class.
        /// </summary>
        /// <param name="accessTokenService">The access token service.</param>
        /// <param name="cosmosConfig">THe cosmos DB configuration.</param>
        public CosmosClientProvider(IAccessTokenService accessTokenService, IOptions<AzureCosmosConfiguration> cosmosConfig)
        {
            _accessTokenService = accessTokenService;
            _accountConfig = cosmosConfig.Value.AccountConfig;
        }

        /// <inheritdoc/>
        public async Task<CosmosClient> GetCosmosClient(string environment)
        {
            if (_clients.TryGetValue(environment, out CosmosClient cosmosClient))
            {
                return cosmosClient;
            }

            _accountConfig.TryGetValue(environment, out CosmosAccountConfig config);

            string token = await _accessTokenService.GetToken(new TokenRequestContext(new[] { "https://management.azure.com/user_impersonation" }));

            using HttpClient client = new();

            string url = $"https://management.azure.com/subscriptions/{config.SubscriptionId}/resourceGroups/{config.ResourceGroup}/providers/Microsoft.DocumentDB/databaseAccounts/{config.AccountName}/listKeys?api-version=2020-04-01";
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage res = await client.PostAsync(url, null);

            if (res.StatusCode == System.Net.HttpStatusCode.OK)
            {
                JObject responseObject = JObject.Parse(await res.Content.ReadAsStringAsync());
                config.PrimaryKey = responseObject["primaryMasterKey"].ToString();
                _accountConfig[environment].PrimaryKey = config.PrimaryKey;

                cosmosClient = new CosmosClient(
                    $"https://altinn-{environment}-cosmos-db.documents.azure.com:443/",
                    config.PrimaryKey,
                    new CosmosClientOptions { ConnectionMode = ConnectionMode.Direct });

                _clients.Add(environment, cosmosClient);
            }
            else
            {
                cosmosClient = null;
            }

            client.Dispose();

            return cosmosClient;
        }

        /// <inheritdoc/>
        public void InvalidateCosmosClient(string environment)
        {
            _clients.Remove(environment);
        }

        /// <inheritdoc/>
        public void RemoveCosmosClients()
        {
            foreach (var entry in _accountConfig)
            {
                entry.Value.PrimaryKey = string.Empty;
            }

            _clients.Clear();
        }
    }
}
