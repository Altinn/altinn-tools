using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Altinn.Platform.Storage.Interface.Models;

using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace AltinnReStorage.Services
{
    /// <inheritdoc/>
    public class CosmosService : ICosmosService
    {
        private readonly ICosmosClientProvider _clientProvider;
        private readonly string _databaseId = "Storage";
        private readonly string _dataCollectionId = "dataElements";

        /// <summary>
        /// Initializes a new instance of the <see cref="CosmosService"/> class.
        /// </summary>
        /// <param name="clientProvider">The document client provider.</param>
        public CosmosService(ICosmosClientProvider clientProvider)
        {
            _clientProvider = clientProvider;
        }

        /// <summary>
        /// Gets metadata for the given data element.
        /// </summary>
        /// <remarks>
        /// Include instanceGuid to avoid a cross partition query.
        /// </remarks>
        public async Task<DataElement> GetDataElement(string dataGuid, string instanceGuid = "")
        {
            DataElement dataElement = null;

            CosmosClient client = await _clientProvider.GetCosmosClient(Program.Environment);

            if (client == null)
            {
                throw new Exception("Unable to create document client. Please check your login credentials.");
            }

            Container container = client.GetContainer(_databaseId, _dataCollectionId);

            QueryRequestOptions options = new()
            {
                MaxBufferedItemCount = 0,
                MaxConcurrency = -1,
                MaxItemCount = 1000
            };

            if (!string.IsNullOrEmpty(instanceGuid))
            {
                options.PartitionKey = new PartitionKey(instanceGuid);
            }

            FeedIterator<DataElement> query = container.GetItemLinqQueryable<DataElement>(requestOptions: options)
                    .ToFeedIterator();

            while (query.HasMoreResults)
            {
                FeedResponse<DataElement> response = await query.ReadNextAsync();
                dataElement = response.First();
            }

            return dataElement;
        }

        /// <inheritdoc/>
        public async Task<List<string>> ListDataElements(string instanceGuid)
        {
            CosmosClient client = await _clientProvider.GetCosmosClient(Program.Environment);

            if (client == null)
            {
                throw new Exception("Unable to create document client. Please check your login credentials.");
            }

            List<string> dataElementIds = new();
            Container container = client.GetContainer(_databaseId, _dataCollectionId);
            QueryRequestOptions options = new()
            {
                MaxBufferedItemCount = 0,
                MaxConcurrency = -1,
                PartitionKey = new(instanceGuid),
                MaxItemCount = 1000
            };

            FeedIterator<DataElement> query = container.GetItemLinqQueryable<DataElement>(requestOptions: options)
                    .ToFeedIterator();

            while (query.HasMoreResults)
            {
                FeedResponse<DataElement> response = await query.ReadNextAsync();
                dataElementIds.AddRange(response.Select(d => d.Id));
            }

            return dataElementIds;
        }

        /// <inheritdoc/>
        public async Task<bool> SaveDataElement(DataElement dataElement)
        {
            CosmosClient client = await _clientProvider.GetCosmosClient(Program.Environment);

            if (client == null)
            {
                throw new Exception("Unable to create document client. Please check your login credentials.");
            }

            Container container = client.GetContainer(_databaseId, _dataCollectionId);

            ItemResponse<DataElement> createdDataElement = await container.CreateItemAsync(dataElement, new PartitionKey(dataElement.InstanceGuid));

            HttpStatusCode res = createdDataElement.StatusCode;

            return res == HttpStatusCode.Created;
        }

        /// <inheritdoc/>
        public async Task<bool> ReplaceDataElement(DataElement dataElement)
        {
            CosmosClient client = await _clientProvider.GetCosmosClient(Program.Environment);
            if (client == null)
            {
                throw new Exception("Unable to create document client. Please check your login credentials.");
            }

            Container container = client.GetContainer(_databaseId, _dataCollectionId);

            ItemResponse<DataElement> res = await container.UpsertItemAsync(dataElement, new PartitionKey(dataElement.Id));
            return res.StatusCode == HttpStatusCode.OK;
        }
    }
}
