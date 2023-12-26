using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using Altinn.Platform.Storage.Interface.Models;

namespace CosmosToPostgreSQL
{
    public class Program
    {
        private static string _cosmosUrl;
        private static string _cosmosSecret;
        private static string _pgConnectionString;
        private static string _environment;

        const string _logFilename = nameof(Program) + ".log";
        const string _errorFilename = nameof(Program) + "-errors.log";

        private static Container _instanceEventContainer;
        private static Container _instanceContainer;
        private static Container _dataElementContainer;
        private static Container _applicationContainer;
        private static Container _textContainer;
        private static NpgsqlDataSource _dataSource;
        private static long _resumeTimeInstance = 0;
        private static long _resumeTimeDataElement = 0;
        private static long _resumeTimeInstanceEvent = 0;
        private static long _resumeTimeApplication = 0;
        private static long _resumeTimeText = 0;

        private static int _processedTotalInstance = 0;
        private static int _processedTotalDataelement = 0;
        private static int _processedTotalInstanceEvent = 0;
        private static int _processedTotalApplication = 0;
        private static int _processedTotalText = 0;

        private static int _errorsInstance = 0;
        private static bool _abortOnError = false;
        private static object _lockObject = new object();
        private static long _startTs = DateTimeOffset.Now.ToUnixTimeSeconds();

        private static SortedSet<string> _dataelementWhitelist = new();
        private static SortedSet<string> _textWhitelist = new();
        private static readonly Dictionary<string, long> _instancesUpdatedAfterStart = new();

        public static async Task Main()
        {
            var builder = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false);
            builder.AddUserSecrets(Assembly.GetExecutingAssembly(), true);
            IConfiguration config = builder.Build();

            _cosmosUrl = config["cosmosUrl"];
            _cosmosSecret = config["cosmosSecret"];
            _pgConnectionString = config["pgConnectionString"];
            _environment = config["environment"];

            try
            {
                await CosmosInitAsync();
                await PostgresInitAsync();
                LogInit();
                ReadWhitelists();

                await ProcessApplications();
                await ProcessTexts();
                await ProcessInstances();
                await ProcessDataelements();
                await ProcessInstanceEvents();

            }
            catch (Exception ex)
            {
                LogException(ex);
                throw;
            }
        }

        private static async Task ProcessTexts()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            LogStartTable("Text", _resumeTimeText);
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = -1, MaxItemCount = 100 };
            FeedIterator<CosmosTextResource> query = _textContainer.GetItemLinqQueryable<CosmosTextResource>(requestOptions: options)
                .Where(t => t.Ts >= _resumeTimeText - 1 && t.Ts < _startTs)
                .OrderBy(t => t.Ts).ToFeedIterator();

            long startTime = DateTime.Now.Ticks;
            long timestamp = 0;
            while (query.HasMoreResults)
            {
                long iterationStartTime = DateTime.Now.Ticks;
                int processedInIteration = 0;
                foreach (CosmosTextResource textResource in await query.ReadNextAsync())
                {
                    await InsertText(textResource);
                    timestamp = textResource.Ts;
                    _processedTotalText++;
                    processedInIteration++;
                }

                await UpdateState("textTs", timestamp);
                long now = DateTime.Now.Ticks;
                long totalElapsedMs = (now - startTime) / TimeSpan.TicksPerMillisecond;
                long iterationElapsedMs = (now - iterationStartTime) / TimeSpan.TicksPerMillisecond;
                if (iterationElapsedMs > 0 && totalElapsedMs > 0)
                {
                    Console.WriteLine($"{DateTime.Now} Text Rate current p/m: {GetRate(processedInIteration, iterationElapsedMs):N0}" +
                        $" Rate total p/m: {GetRate(_processedTotalText, totalElapsedMs):N0}," +
                        $" processed: {_processedTotalText:N0}");
                }
            }

            LogEnd("Text", _processedTotalText, stopwatch.ElapsedMilliseconds / 1000, timestamp);
        }
        private static async Task ProcessApplications()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            LogStartTable("Application", _resumeTimeApplication);
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = -1, MaxItemCount = 100 };
            FeedIterator<CosmosApplication> query = _applicationContainer.GetItemLinqQueryable<CosmosApplication>(requestOptions: options)
                .Where(i => i.Ts >= _resumeTimeApplication - 1 && i.Ts < _startTs)
                .OrderBy(e => e.Created).ToFeedIterator();

            long startTime = DateTime.Now.Ticks;
            long timestamp = 0;
            while (query.HasMoreResults)
            {
                long iterationStartTime = DateTime.Now.Ticks;
                int processedInIteration = 0;
                foreach (CosmosApplication application in await query.ReadNextAsync())
                {
                    await InsertApplication(application);
                    timestamp = application.Ts;
                    _processedTotalApplication++;
                    processedInIteration++;
                }

                await UpdateState("applicationTs", timestamp);
                _resumeTimeApplication = timestamp;
                long now = DateTime.Now.Ticks;
                long totalElapsedMs = (now - startTime) / TimeSpan.TicksPerMillisecond;
                long iterationElapsedMs = (now - iterationStartTime) / TimeSpan.TicksPerMillisecond;
                if (iterationElapsedMs > 0 && totalElapsedMs > 0)
                {
                    Console.WriteLine($"{DateTime.Now} Application Rate current p/m: {GetRate(processedInIteration, iterationElapsedMs):N0}" +
                        $" Rate total p/m: {GetRate(_processedTotalApplication, totalElapsedMs):N0}," +
                        $" processed: {_processedTotalApplication:N0}");
                }
            }

            LogEnd("Application", _processedTotalApplication, stopwatch.ElapsedMilliseconds / 1000, timestamp);
        }

        private static async Task ProcessInstances()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            LogStartTable("Instance", _resumeTimeInstance);
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = -1, MaxItemCount = 1000 };
            FeedIterator<CosmosInstance> query = _instanceContainer.GetItemLinqQueryable<CosmosInstance>(requestOptions: options)
                .Where(i => i.Ts >= _resumeTimeInstance - 1 && i.Ts < _startTs)
                .OrderBy(e => e.Created).ToFeedIterator();

            long startTime = DateTime.Now.Ticks;
            long timestamp = 0;
            while (query.HasMoreResults)
            {
                long iterationStartTime = DateTime.Now.Ticks;
                int processedInIteration = 0;
                List<Task> tasks = new();
                foreach (CosmosInstance instance in await query.ReadNextAsync())
                {
                    tasks.Add(InsertInstance(instance));
                    timestamp = instance.Ts;
                    _processedTotalInstance++;
                    processedInIteration++;
                }
                await Task.WhenAll(tasks);

                await UpdateState("instanceTs", timestamp);
                _resumeTimeInstance = timestamp;
                long now = DateTime.Now.Ticks;
                long totalElapsedMs = (now - startTime) / TimeSpan.TicksPerMillisecond;
                long iterationElapsedMs = (now - iterationStartTime) / TimeSpan.TicksPerMillisecond;
                if (iterationElapsedMs > 0 && totalElapsedMs > 0)
                {
                    Console.WriteLine($"{DateTime.Now} Instance Rate current p/m: {GetRate(processedInIteration, iterationElapsedMs):N0}" +
                        $" Rate total p/m: {GetRate(_processedTotalInstance, totalElapsedMs):N0}," +
                        $" processed: {_processedTotalInstance:N0}");
                }
            }

            LogEnd("Instance", _processedTotalInstance, stopwatch.ElapsedMilliseconds / 1000, timestamp);
        }

        private static async Task<CosmosInstance?> GetInstanceIfUpdatedAfterStart(string instanceId)
        {
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = -1, MaxItemCount = 1 };
            FeedIterator<CosmosInstance> query = _instanceContainer.GetItemLinqQueryable<CosmosInstance>(requestOptions: options)
                .Where(i => i.Id == instanceId && i.Ts > _startTs)
                .ToFeedIterator();

            if (query.HasMoreResults)
            {
                return (await query.ReadNextAsync())?.FirstOrDefault();
            }

            return null;
        }

        private static async Task ProcessDataelements()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            LogStartTable("Dataelement", _resumeTimeDataElement);
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = -1, MaxItemCount = 1000 };
            FeedIterator<CosmosDataElement> query = _dataElementContainer.GetItemLinqQueryable<CosmosDataElement>(requestOptions: options)
                .Where(d => d.Ts >= _resumeTimeDataElement - 1 && d.Ts < _startTs)
                .OrderBy(d => d.Created).ToFeedIterator();

            long startTime = DateTime.Now.Ticks;
            long timestamp = 0;
            while (query.HasMoreResults)
            {
                long iterationStartTime = DateTime.Now.Ticks;
                int processedInIteration = 0;
                List<Task> tasks = new();
                foreach (CosmosDataElement dataElement in await query.ReadNextAsync())
                {
                    if (dataElement.FileScanResult != null && dataElement.FileScanResult.ToString().StartsWith('{'))
                    {
                        string fileScanResultString = dataElement.FileScanResult.ToString();
                        if (fileScanResultString.IndexOf("clean", StringComparison.OrdinalIgnoreCase) != -1)
                            dataElement.FileScanResult = "Clean";
                        else if (fileScanResultString.IndexOf("Infected", StringComparison.OrdinalIgnoreCase) != -1)
                            dataElement.FileScanResult = "Infected";
                        else if (fileScanResultString.IndexOf("Pending", StringComparison.OrdinalIgnoreCase) != -1)
                            dataElement.FileScanResult = "Pending";
                        else if (fileScanResultString.IndexOf("NotApplicable", StringComparison.OrdinalIgnoreCase) != -1)
                            dataElement.FileScanResult = "NotApplicable";
                    }

                    tasks.Add(InsertDataElement(dataElement));
                    timestamp = dataElement.Ts;
                    _processedTotalDataelement++;
                    processedInIteration++;
                }
                await Task.WhenAll(tasks);

                await UpdateState("dataElementTs", timestamp);
                long now = DateTime.Now.Ticks;
                long totalElapsedMs = (now - startTime) / TimeSpan.TicksPerMillisecond;
                long iterationElapsedMs = (now - iterationStartTime) / TimeSpan.TicksPerMillisecond;
                if (iterationElapsedMs > 0 && totalElapsedMs > 0)
                {
                    Console.WriteLine($"{DateTime.Now} Element Rate current p/m: {GetRate(processedInIteration, iterationElapsedMs):N0}" +
                        $" Rate total p/m: {GetRate(_processedTotalDataelement, totalElapsedMs):N0}," +
                        $" processed: {_processedTotalDataelement:N0}");
                }
            }

            LogEnd("Dataelement", _processedTotalDataelement, stopwatch.ElapsedMilliseconds / 1000, timestamp);
        }

        private static async Task ProcessInstanceEvents()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            LogStartTable("InstanceEvents", _resumeTimeInstanceEvent);

            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = -1, MaxItemCount = 1000 };
            FeedIterator<CosmosInstanceEvent> query = _instanceEventContainer.GetItemLinqQueryable<CosmosInstanceEvent>(requestOptions: options)
                .Where(e => e.Ts >= _resumeTimeInstanceEvent - 1 && e.Ts < _startTs)
                .OrderBy(e => e.Ts).ToFeedIterator(); //Created is missing in index, should have used created

            long startTime = DateTime.Now.Ticks;
            long timestamp = 0;
            while (query.HasMoreResults)
            {
                long iterationStartTime = DateTime.Now.Ticks;
                int processedInIteration = 0;
                List<Task> tasks = new();
                foreach (CosmosInstanceEvent instanceEvent in await query.ReadNextAsync())
                {
                    tasks.Add(InsertInstanceEvent(instanceEvent));
                    timestamp = instanceEvent.Ts;
                    _processedTotalInstanceEvent++;
                    processedInIteration++;
                }
                await Task.WhenAll(tasks);

                await UpdateState("instanceEventTs", timestamp);
                long now = DateTime.Now.Ticks;
                long totalElapsedMs = (now - startTime) / TimeSpan.TicksPerMillisecond;
                long iterationElapsedMs = (now - iterationStartTime) / TimeSpan.TicksPerMillisecond;
                if (iterationElapsedMs > 0 && totalElapsedMs > 0)
                {
                    Console.WriteLine($"{DateTime.Now} IEvent Rate current p/m: {GetRate(processedInIteration, iterationElapsedMs):N0}" +
                        $" Rate total p/m: {GetRate(_processedTotalInstanceEvent, totalElapsedMs):N0}" +
                        $", processed: {_processedTotalInstanceEvent:N0}");
                }
            }

            LogEnd("InstanceEvent", _processedTotalInstanceEvent, stopwatch.ElapsedMilliseconds / 1000, timestamp);
        }

        private static async Task InsertText(CosmosTextResource textResource)
        {
            int applicationInternalId;
            string app = textResource.Id[(textResource.Org.Length + 1)..^(textResource.Language.Length + 1)];
            await using NpgsqlCommand pgcomReadApp = _dataSource.CreateCommand("select id from storage.applications where app = $1 and org = $2");
            pgcomReadApp.Parameters.AddWithValue(NpgsqlDbType.Text, app);
            pgcomReadApp.Parameters.AddWithValue(NpgsqlDbType.Text, textResource.Org);
            await using NpgsqlDataReader reader = await pgcomReadApp.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                applicationInternalId = reader.GetFieldValue<int>("id");
            }
            else
            {
                if (!_textWhitelist.Contains(textResource.Id))
                {
                    LogError("App not found for text with id " + textResource.Id);
                }

                return;

                //throw new ArgumentException("App not found for " + textResource.Id);
            }

            await using NpgsqlCommand pgcomRead = _dataSource.CreateCommand("insert into storage.texts (org, app, language, textResource, applicationInternalId) values ($1, $2, $3, jsonb_strip_nulls($4), $5)" +
                " ON CONFLICT ON CONSTRAINT textAlternateId DO UPDATE SET textResource = jsonb_strip_nulls($4)");
            pgcomRead.Parameters.AddWithValue(NpgsqlDbType.Text, textResource.Org);
            pgcomRead.Parameters.AddWithValue(NpgsqlDbType.Text, app);
            pgcomRead.Parameters.AddWithValue(NpgsqlDbType.Text, textResource.Language);
            pgcomRead.Parameters.AddWithValue(NpgsqlDbType.Jsonb, textResource);
            pgcomRead.Parameters.AddWithValue(NpgsqlDbType.Bigint, applicationInternalId);

            await pgcomRead.ExecuteNonQueryAsync();
        }

        private static async Task InsertApplication(CosmosApplication application)
        {
            string app = application.Id[(application.Org.Length + 1)..];
            application.Id = $"{application.Org}/{app}";
            await using NpgsqlCommand pgcom = _dataSource.CreateCommand("insert into storage.applications (app, org, application) values ($1, $2, jsonb_strip_nulls($3))" +
                " ON CONFLICT ON CONSTRAINT app_org DO UPDATE SET application = jsonb_strip_nulls($3)");
            pgcom.Parameters.AddWithValue(NpgsqlDbType.Text, app);
            pgcom.Parameters.AddWithValue(NpgsqlDbType.Text, application.Org);
            pgcom.Parameters.AddWithValue(NpgsqlDbType.Jsonb, application);

            await pgcom.ExecuteNonQueryAsync();
        }

        private static async Task<long> InsertInstance(CosmosInstance instance)
        {
            instance.Data = null;
            await using NpgsqlCommand pgcomInsert = _dataSource.CreateCommand("INSERT INTO storage.instances(partyId, alternateId, instance, created, lastChanged, org, appId, taskId)" +
                " VALUES ($1, $2, jsonb_strip_nulls($3), $4, $5, $6, $7, $8) ON CONFLICT(alternateId) DO UPDATE SET instance = jsonb_strip_nulls($3), lastChanged = $5, taskId = $8 " +
                "RETURNING id");
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Bigint, long.Parse(instance.InstanceOwner.PartyId));
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Uuid, new Guid(instance.Id));
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, instance);
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, instance.Created ?? DateTime.Now);
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, instance.LastChanged ?? DateTime.Now);
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Text, instance.Org);
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Text, instance.AppId);
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Text, instance?.Process?.CurrentTask?.ElementId ?? (object)DBNull.Value);

            return (long)await pgcomInsert.ExecuteScalarAsync();
        }

        private static async Task InsertDataElement(CosmosDataElement element)
        {
            long instanceId = await GetInstanceId(element.InstanceGuid, element);
            if (instanceId == 0)
                return;

            if (element.Filename != null && element.Filename.Contains("\0"))
                element.Filename = element.Filename.Replace("\0", null);

            await using NpgsqlCommand pgcomInsert = _dataSource.CreateCommand("INSERT INTO storage.dataelements(instanceInternalId, instanceGuid, alternateId, element)" +
                " VALUES ($1, $2, $3, jsonb_strip_nulls($4)) ON CONFLICT(id) DO UPDATE SET element = jsonb_strip_nulls($4)");
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Bigint, instanceId);
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Uuid, new Guid(element.InstanceGuid));
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Uuid, new Guid(element.Id));
            pgcomInsert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, element);

            try
            {
                await pgcomInsert.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex)
            {
                if (ex.MessageText.StartsWith("duplicate key value violates unique constraint"))
                {
                    // Element updated after first iteration of convertion program
                    await using NpgsqlCommand pgcomUpdate = _dataSource.CreateCommand("UPDATE storage.dataelements set element = jsonb_strip_nulls($2) WHERE alternateId = $1");
                    pgcomUpdate.Parameters.AddWithValue(NpgsqlDbType.Uuid, new Guid(element.Id));
                    pgcomUpdate.Parameters.AddWithValue(NpgsqlDbType.Jsonb, element);

                    await pgcomUpdate.ExecuteNonQueryAsync();
                }
                else
                {
                    throw;
                }
            }
        }

        private static async Task InsertInstanceEvent(CosmosInstanceEvent instanceEvent)
        {
            await using NpgsqlCommand pgcom = _dataSource.CreateCommand("INSERT INTO storage.instanceEvents(instance, alternateId, event)" +
                " VALUES ($1, $2, jsonb_strip_nulls($3))");
            pgcom.Parameters.AddWithValue(NpgsqlDbType.Uuid, new Guid(instanceEvent.InstanceId.Split('/').Last()));
            pgcom.Parameters.AddWithValue(NpgsqlDbType.Uuid, instanceEvent.Id);
            pgcom.Parameters.AddWithValue(NpgsqlDbType.Jsonb, instanceEvent);

            try
            {
                await pgcom.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex)
            {
                // Ignore duplicate key because instance event is "write once". This should only occur
                // if we resume processing at a sequence with several events created at the exact
                // same timetamp
                if (!ex.MessageText.StartsWith("duplicate key value violates unique constraint"))
                {
                    throw;
                }
            }
        }

        private static async Task<long> GetInstanceId(string id, DataElement element)
        {
            long internalId = 0;
            await using NpgsqlCommand pgcom = _dataSource.CreateCommand($"select id from storage.instances where alternateId = $1");
            pgcom.Parameters.AddWithValue(NpgsqlDbType.Uuid, new Guid(id));
            await using (NpgsqlDataReader reader = await pgcom.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    internalId = reader.GetFieldValue<long>("id");
                }
            }

            if (internalId == 0 && !_dataelementWhitelist.Contains(element.InstanceGuid))
            {
                CosmosInstance instance = await GetInstanceIfUpdatedAfterStart(element.InstanceGuid);
                if (instance != null)
                {
                    lock (_instancesUpdatedAfterStart)
                    {
                        if (_instancesUpdatedAfterStart.TryGetValue(element.InstanceGuid, out internalId))
                        {
                            return internalId;
                        }

                        internalId = InsertInstance(instance).Result;
                        _instancesUpdatedAfterStart[element.InstanceGuid] = internalId;
                        return internalId;
                    }
                }

                _errorsInstance++;
                var blob = element.BlobStoragePath.Split('/');
                string app = blob[0] + "/" + blob[1];
                string msg = $"Could not find internal instance id for guid;{id};data element id;{element.Id};" +
                    $"created;{element.Created?.ToString("yyyy-MM-dd HH:mm:ss")};last changed;{element.LastChanged?.ToString("yyyy-MM-dd HH:mm:ss")};blob;{element.BlobStoragePath};app;{app}";
                Console.WriteLine(msg);
                LogError(msg);
                ////throw new Exception(msg);
            }

            return internalId;
        }

        private static void ReadWhitelists()
        {
            foreach (string line in File.ReadAllLines($"WhitelistElements-{_environment}.csv"))
            {
                if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
                {
                    _dataelementWhitelist.Add(line.Split(';')[0]);
                }
            }

            foreach (string line in File.ReadAllLines($"WhitelistTexts-{_environment}.csv"))
            {
                if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
                {
                    _textWhitelist.Add(line.Split(';')[0]);
                }
            }
        }

        private static async Task CosmosInitAsync()
        {
            CosmosClientOptions options = new()
            {
                ConnectionMode = ConnectionMode.Direct,
                GatewayModeMaxConnectionLimit = 100,
            };
            CosmosClient cosmosClient = new(_cosmosUrl, _cosmosSecret, options);
            Database db = await cosmosClient.CreateDatabaseIfNotExistsAsync("Storage");

            _instanceEventContainer = await db.CreateContainerIfNotExistsAsync("instanceEvents", "/instanceId");
            _instanceContainer = await db.CreateContainerIfNotExistsAsync("instances", "/instanceOwner/partyId");
            _dataElementContainer = await db.CreateContainerIfNotExistsAsync("dataElements", "/instanceGuid");
            _applicationContainer = await db.CreateContainerIfNotExistsAsync("applications", "/org");
            _textContainer = await db.CreateContainerIfNotExistsAsync("texts", "/org");
        }

        private static async Task PostgresInitAsync()
        {
            _dataSource = NpgsqlDataSource.Create(_pgConnectionString);

            await using NpgsqlCommand pgcom = _dataSource.CreateCommand("SELECT COUNT(*) FROM storage.convertionStatus");
            await using NpgsqlDataReader countReader = await pgcom.ExecuteReaderAsync();
            if (await countReader.ReadAsync())
            {
                long count = countReader.GetFieldValue<long>(0);
                if (count > 1)
                {
                    throw new Exception("Fatal error - too many rows in convertionStatus");
                }
                else if (count == 0)
                {
                    await using NpgsqlCommand pgcomInsert = _dataSource.CreateCommand("INSERT INTO storage.convertionStatus (instanceTs, instanceEventTs, dataElementTs, applicationTs, textTs) VALUES (0, 0, 0, 0, 0)");
                    await pgcomInsert.ExecuteNonQueryAsync();
                }
                else
                {
                    await using NpgsqlCommand pgcomRead = _dataSource.CreateCommand("SELECT * FROM storage.convertionStatus");
                    await using NpgsqlDataReader reader = await pgcomRead.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        _resumeTimeInstance = reader.GetFieldValue<long>("instanceTs");
                        _resumeTimeDataElement = reader.GetFieldValue<long>("dataElementTs");
                        _resumeTimeInstanceEvent = reader.GetFieldValue<long>("instanceEventTs");
                        _resumeTimeApplication = reader.GetFieldValue<long>("applicationTs");
                        _resumeTimeText = reader.GetFieldValue<long>("textTs");
                    }
                }
            }
            else
            {
                throw new Exception("Fatal error - can't read from convertionStatus");
            }
        }

        private static async Task UpdateState(string stateRef, long timestamp)
        {
            await using NpgsqlCommand pgcom = _dataSource.CreateCommand($"UPDATE storage.convertionStatus SET {stateRef} = $1");
            pgcom.Parameters.AddWithValue(NpgsqlDbType.Bigint, timestamp);
            await pgcom.ExecuteNonQueryAsync();
        }

        private static void LogStartTable(string table, long ts)
        {
            File.AppendAllText(_logFilename, $"{DateTime.Now} Start processing {table} at {ts}\r\n");
        }

        private static void LogInit()
        {
            File.AppendAllText(_logFilename, $"{DateTime.Now} Start processing at _ts {_startTs}," +
                $" server {_pgConnectionString.Split(';')[0].Split('=')[1]}\r\n");
        }

        private static void LogError(string msg, Exception e = null)
        {
            Console.WriteLine($"{DateTime.Now} {msg} {e?.Message ?? null}");
            lock (_lockObject)
                File.AppendAllText(_errorFilename, $"{DateTime.Now} {msg} {e?.Message ?? null}\r\n");
        }

        private static void LogEnd(string table, int numberProcessed, long timeUsed, object timestamp)
        {
            File.AppendAllText(_logFilename, $"{DateTime.Now} Finished {table}. Processed {numberProcessed:N0} items in {timeUsed:N0} seconds," +
                $" {timeUsed / 60:N0} minutes, {timeUsed / 3600} hours. Last timestamp {timestamp}\r\n");
        }

        private static void LogException(Exception e)
        {
            File.AppendAllText(_logFilename, $"{DateTime.Now} {e.Message}\r\n{e.StackTrace}\r\n" +
                $"Processed, instance: {_processedTotalInstance:N0}, " +
                $"data element: {_processedTotalDataelement:N0}, " +
                $"instance event: {_processedTotalInstanceEvent:N0}\r\n");
        }

        private static long GetRate(int count, long duration)
        {
            return (long)Math.Round(1000.0 * count / duration);
        }
    }
}