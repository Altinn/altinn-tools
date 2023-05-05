using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AltinnReStorage.Configuration;

using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using Azure.Storage;
using Azure.Storage.Blobs;

using Microsoft.Extensions.Options;

namespace AltinnReStorage.Services
{
    /// <inheritdoc/>
    public class BlobContainerClientProvider : IBlobContainerClientProvider
    {
        private readonly IAccessTokenService _accessTokenService;
        private readonly Dictionary<string, StorageAccountConfig> _accountConfig;
        private readonly Dictionary<string, BlobContainerClient> _clients = new Dictionary<string, BlobContainerClient>();

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobContainerClientProvider"/> class.
        /// </summary>
        /// <param name="accessTokenService">The access token service.</param>
        /// <param name="storageConfig">The storage configuration.</param>
        public BlobContainerClientProvider(IAccessTokenService accessTokenService, IOptions<AzureStorageConfiguration> storageConfig)
        {
            _accountConfig = storageConfig.Value.AccountConfig;
            _accessTokenService = accessTokenService;
        }

        /// <inheritdoc/>
        public async Task<BlobContainerClient> GetBlobClient(string org, string environment)
        {
            string key = $"{org}-{environment}";

            if (_clients.TryGetValue(key, out BlobContainerClient client))
            {
                return client;
            }

            try
            {
                _accountConfig.TryGetValue(key, out StorageAccountConfig config);

                if (string.IsNullOrEmpty(config.AccountKey))
                {
                    ArmClient armClient = new(_accessTokenService.GetCredential());
                    SubscriptionResource subscription = armClient.GetSubscriptionResource(new Azure.Core.ResourceIdentifier(config.SubscriptionId));
                    ResourceGroupResource resourceGroup = subscription.GetResourceGroup(config.ResourceGroup);
                    StorageAccountResource storageAccount = await resourceGroup.GetStorageAccountAsync(config.AccountName);
                    await foreach (StorageAccountKey accountKey in storageAccount.GetKeysAsync())
                    {
                        if (accountKey.KeyName == "key1")
                        {
                            config.AccountKey = accountKey.Value;
                            _accountConfig[key].AccountKey = config.AccountKey;
                            break;
                        }
                    }
                }

                string blobEndpoint = $"https://{config.AccountName}.blob.core.windows.net/";

                BlobServiceClient commonBlobClient = new BlobServiceClient(new Uri(blobEndpoint), new StorageSharedKeyCredential(config.AccountName, config.AccountKey));
                client = commonBlobClient.GetBlobContainerClient(config.Container);

                _clients.TryAdd(key, client);
            }
            catch (Exception e)
            {
                throw new Exception("An error occured when setting up blob storage client. Please check your credentials and try again.", e);
            }

            return client;
        }

        /// <inheritdoc/>
        public void InvalidateBlobClient(string org, string environment)
        {
            string key = $"{org}-{environment}";
            _clients.Remove(key);
        }

        /// <inheritdoc/>
        public void RemoveBlobClients()
        {
            foreach (var entry in _accountConfig)
            {
                entry.Value.AccountKey = string.Empty;
            }

            _clients.Clear();
        }
    }
}
