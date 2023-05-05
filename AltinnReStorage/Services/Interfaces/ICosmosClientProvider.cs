using System.Threading.Tasks;

using Microsoft.Azure.Cosmos;

namespace AltinnReStorage.Services
{
    /// <summary>
    /// Service that provides and administers Azure CosmosDB document clients.
    /// </summary>
    public interface ICosmosClientProvider
    {
        /// <summary>
        /// Retrieves a document client for the given context.
        /// </summary>
        /// <param name="environment">The environment the database exists in.</param>
        /// <returns>The document client.</returns>
        Task<CosmosClient> GetCosmosClient(string environment);

        /// <summary>
        /// Invalidates the document client if it exists.
        /// </summary>
        /// <param name="environment">The environment.</param>
        void InvalidateCosmosClient(string environment);

        /// <summary>
        /// Deletes all cached document clients and the database primary keys.
        /// </summary>
        void RemoveCosmosClients();
    }
}
