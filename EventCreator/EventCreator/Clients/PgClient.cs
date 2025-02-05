using Altinn.Platform.Storage.Interface.Models;
using Npgsql;
using NpgsqlTypes;
using System.Data;

namespace EventCreator.Clients;

public class PgClient
{
    private readonly string _readSqlNoElements = "select * from storage.readinstancenoelements ($1)";

    private readonly NpgsqlDataSource _dataSource;

    public PgClient(string _pgConnectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_pgConnectionString);
        dataSourceBuilder.EnableDynamicJson();
        _dataSource = dataSourceBuilder.Build();
    }

    /// <inheritdoc/>
    public async Task<Instance?> GetOne(Guid instanceGuid)
    {
        Instance? instance = null;
        List<DataElement> instanceData = [];
        long instanceInternalId = 0;

        await using NpgsqlCommand pgcom = _dataSource.CreateCommand(_readSqlNoElements);

        pgcom.Parameters.AddWithValue(NpgsqlDbType.Uuid, instanceGuid);

        await using (NpgsqlDataReader reader = await pgcom.ExecuteReaderAsync())
        {
            bool instanceCreated = false;
            while (await reader.ReadAsync())
            {
                if (!instanceCreated)
                {
                    instanceCreated = true;
                    instance = await reader.GetFieldValueAsync<Instance>("instance");
                    instanceInternalId = await reader.GetFieldValueAsync<long>("id");
                }
            }
        }

        return instance;
    }
}
