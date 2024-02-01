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
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections;
using Common;
using System.Xml.Linq;

namespace Verify
{
    internal static class Program
    {
        private static DateTime _cutoffDateTime;
        private static long _cutoffEpoch;
        private static readonly object _lockObject = new object();
        private static readonly char[] _partitions = new char[] { '0','1','2','3','4','5','6','7','8','9','0','a','b','c','d','e','f' };
        private static readonly JsonSerializerOptions _jsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        private static long _count = 0;
        private static Container _instanceEventContainer;
        private static Container _instanceContainer;
        private static Container _dataElementContainer;
        private static Container _applicationContainer;
        private static Container _textContainer;
        private static NpgsqlDataSource _dataSource;

        private static string _cosmosUrl;
        private static string _cosmosSecret;
        private static string _pgConnectionString;
        private static string _environment;

        private static StreamWriter _missingSw;
        private static StreamWriter _diffSw;

        private static SortedSet<string> _dataelementWhitelist = new();
        private static SortedSet<string> _textWhitelist = new();

        public static async Task Main()
        {
            var builder = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false);
            builder.AddUserSecrets(Assembly.GetExecutingAssembly(), true);
            IConfiguration config = builder.Build();

            _environment = config["environment"];
            _cosmosUrl = config[$"{_environment}:cosmosUrl"];
            _cosmosSecret = config[$"{_environment}:cosmosSecret"];
            _pgConnectionString = config[$"{_environment}:pgConnectionString"];
            _cutoffDateTime = DateTime.Parse(config[$"{_environment}:cutoffDateTime"] ?? "2030-01-01");
            _cutoffEpoch = new DateTimeOffset(_cutoffDateTime).ToUniversalTime().ToUnixTimeSeconds();

            await CosmosInitAsync();
            PostgresInit();
            ReadWhitelists();
            await ProcessApplicationsAll();
            await ProcessTextsAll();
            await ProcessInstancesAll();
            await ProcessDataelementsAll();
            await ProcessInstanceEventsAll();
        }

        private static async Task ProcessApplicationsAll()
        {
            _missingSw = new StreamWriter($"ApplicationsMissing-{_environment}.log", false);
            _diffSw = new StreamWriter($"ApplicationsDiff-{_environment}.log", false);
            _missingSw.WriteLine(_cutoffEpoch);
            _diffSw.WriteLine(_cutoffEpoch);
            Stopwatch sw = Stopwatch.StartNew();
            await ProcessApplications();
            sw.Stop();
            Console.WriteLine($"Time used for applications: {sw.ElapsedMilliseconds / 1000:N0}");
            _missingSw.Close();
            _diffSw.Close();
        }

        private static async Task ProcessTextsAll()
        {
            _missingSw = new StreamWriter($"TextsMissing-{_environment}.log", false);
            _diffSw = new StreamWriter($"TextsDiff-{_environment}.log", false);
            _missingSw.WriteLine(_cutoffEpoch);
            _diffSw.WriteLine(_cutoffEpoch);
            Stopwatch sw = Stopwatch.StartNew();
            await ProcessTexts();
            sw.Stop();
            Console.WriteLine($"Time used for texts: {sw.ElapsedMilliseconds / 1000:N0}");
            _missingSw.Close();
            _diffSw.Close();
        }

        private static async Task ProcessInstancesAll ()
        {
            _missingSw = new StreamWriter($"InstanceMissing-{_environment}.log", false);
            _diffSw = new StreamWriter($"InstanceDiff-{_environment}.log", false);
            _missingSw.WriteLine(_cutoffEpoch);
            _diffSw.WriteLine(_cutoffEpoch);
            List<Task> tasks = new();

            Stopwatch sw = Stopwatch.StartNew();
            foreach (char partition in _partitions)
            {
                tasks.Add(ProcessInstances(partition));
            }
            await Task.WhenAll(tasks);
            sw.Stop();
            Console.WriteLine($"Time used for instances: {sw.ElapsedMilliseconds / 1000:N0}");
            _missingSw.Close();
            _diffSw.Close();
        }

        private static async Task ProcessInstanceEventsAll()
        {
            _missingSw = new StreamWriter($"InstanceEventsMissing-{_environment}.log", false);
            _diffSw = new StreamWriter($"InstanceEventsDiff-{_environment}.log", false);
            _missingSw.WriteLine(_cutoffEpoch);
            _diffSw.WriteLine(_cutoffEpoch);
            List<Task> tasks = new();

            Stopwatch sw = Stopwatch.StartNew();
            foreach (char partition in _partitions)
            {
                tasks.Add(ProcessInstanceEvents(partition));
            }
            await Task.WhenAll(tasks);
            sw.Stop();
            Console.WriteLine($"Time used for instance events: {sw.ElapsedMilliseconds / 1000:N0}");
            _missingSw.Close();
            _diffSw.Close();
        }

        private static async Task ProcessDataelementsAll()
        {
            _missingSw = new StreamWriter($"DataelementMissing-{_environment}.log", false);
            _diffSw = new StreamWriter($"DataelementDiff-{_environment}.log", false);
            _missingSw.WriteLine(_cutoffEpoch);
            _diffSw.WriteLine(_cutoffEpoch);
            List<Task> tasks = new();

            Stopwatch sw = Stopwatch.StartNew();
            foreach (char partition in _partitions)
            {
                tasks.Add(ProcessDataelements(partition));
            }
            await Task.WhenAll(tasks);
            sw.Stop();
            Console.WriteLine($"Time used for data elements: {sw.ElapsedMilliseconds / 1000:N0}");
            _missingSw.Close();
            _diffSw.Close();
        }

        private static async Task ProcessApplications()
        {
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = 16, MaxItemCount = 50000 };
            FeedIterator<CosmosApplication> query = _applicationContainer.GetItemLinqQueryable<CosmosApplication>(requestOptions: options)
                .Where(i => (_cutoffDateTime > DateTime.Now || i.Ts < _cutoffEpoch))
                .OrderBy(i => i.Id).ToFeedIterator();

            while (query.HasMoreResults)
            {
                var cosmosApplications = await query.ReadNextAsync();
                Task<SortedList<string, string>> applicationTask = ReadApplications();
                SortedList<string, string> cApplications = new SortedList<string, string>();

                foreach (CosmosApplication application in cosmosApplications)
                {
                    application.Title = application.Title?.OrderBy(i => i.Key).ToDictionary();
                    cApplications.Add(application.Id, JsonSerializer.Serialize(application, _jsonOptions));
                }
                var pgApplications = await applicationTask;
                foreach (var kvp in cApplications)
                {
                    pgApplications.TryGetValue(kvp.Key, out string? pgInstance);
                    if (pgInstance == null)
                    {
                        lock (_missingSw)
                            _missingSw.WriteLine($"Missing {kvp.Key}, {kvp.Value}");
                    }
                    else if (kvp.Value != pgInstance)
                    {
                        lock (_diffSw)
                            _diffSw.WriteLine($"Diff in content\r\n{kvp.Value}\r\n{pgInstance}");
                    }
                }

                lock (_lockObject)
                    _count += cApplications.Count;
                Console.WriteLine($"{cApplications.Count:N0}, {_count:N0}");
            }
        }

        private static async Task ProcessTexts()
        {
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = 16, MaxItemCount = 50000 };
            FeedIterator<CosmosTextResource> query = _textContainer.GetItemLinqQueryable<CosmosTextResource>(requestOptions: options)
                .Where(i => (_cutoffDateTime > DateTime.Now || i.Ts < _cutoffEpoch))
                .OrderBy(i => i.Id).ToFeedIterator();

            while (query.HasMoreResults)
            {
                var cosmosTexts = await query.ReadNextAsync();
                Task<SortedList<string, string>> textTask = ReadTexts();
                SortedList<string, string> cTexts = new SortedList<string, string>();

                foreach (CosmosTextResource text in cosmosTexts)
                {
                    cTexts.Add($"{text.Id}", JsonSerializer.Serialize(text, _jsonOptions));
                }
                var pgTexts = await textTask;
                foreach (var kvp in cTexts)
                {
                    pgTexts.TryGetValue(kvp.Key, out string? pgInstance);
                    if (pgInstance == null)
                    {
                        if (!_textWhitelist.Contains(kvp.Key))
                            lock (_missingSw)
                                _missingSw.WriteLine($"Missing {kvp.Key}, {kvp.Value}");
                    }
                    else if (kvp.Value != pgInstance)
                    {
                        lock (_diffSw)
                            _diffSw.WriteLine($"Diff in content\r\n{kvp.Value}\r\n{pgInstance}");
                    }
                }

                lock (_lockObject)
                    _count += cTexts.Count;
                Console.WriteLine($"{cTexts.Count:N0}, {_count:N0}");
            }
        }

        private static async Task ProcessInstances(char partition)
        {
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = 16, MaxItemCount = 50000 };
            FeedIterator<CosmosInstance> query = _instanceContainer.GetItemLinqQueryable<CosmosInstance>(requestOptions: options)
                .Where(i => i.Id.StartsWith(partition) && (_cutoffDateTime > DateTime.Now || i.Ts < _cutoffEpoch))
                .OrderBy(i => i.Id).ToFeedIterator();

            while (query.HasMoreResults)
            {
                var cosmosInstances = await query.ReadNextAsync();
                Task<SortedList<Guid, string>> instanceTask = ReadInstances(cosmosInstances.First().Id, cosmosInstances.Last().Id);
                SortedList<Guid, string> cInstances = new SortedList<Guid, string>();

                foreach (CosmosInstance instance in cosmosInstances)
                {
                    instance.Data = null;
                    instance.DataValues = instance.DataValues?.OrderBy(i => i.Key).ToDictionary();
                    instance.PresentationTexts = instance.PresentationTexts?.OrderBy(i => i.Key).ToDictionary();
                    if (instance.DataValues != null && instance.DataValues.TryGetValue("ReceiversReference", out string? value) && value == null)
                    {
                        instance.DataValues.Remove("ReceiversReference");
                    }
                    cInstances.Add(Guid.Parse(instance.Id), JsonSerializer.Serialize(instance, _jsonOptions));
                }
                var pgInstances = await instanceTask;
                foreach (var kvp in cInstances)
                {
                    pgInstances.TryGetValue(kvp.Key, out string? pgInstance);
                    if (pgInstance == null)
                    {
                        lock (_missingSw)
                            _missingSw.WriteLine($"Missing {kvp.Key}, {kvp.Value}");
                    }
                    else if (kvp.Value != pgInstance)
                    {
                        lock (_diffSw)
                            _diffSw.WriteLine($"Diff in content\r\n{kvp.Value}\r\n{pgInstance}");
                    }
                }

                lock (_lockObject)
                    _count += cInstances.Count;
                Console.WriteLine($"{cInstances.Count:N0}, {_count:N0}");
            }
        }

        private static async Task ProcessInstanceEvents(char partition)
        {
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = 16, MaxItemCount = 50_000 };
            FeedIterator<CosmosInstanceEvent> query = _instanceEventContainer.GetItemLinqQueryable<CosmosInstanceEvent>(requestOptions: options)
                .Where(i => i.Id.ToString().StartsWith(partition) && (_cutoffDateTime > DateTime.Now || i.Ts < _cutoffEpoch))
                .OrderBy(i => i.Id).ToFeedIterator();

            while (query.HasMoreResults)
            {
                var cosmosInstanceEvents = await query.ReadNextAsync();
                Task<SortedList<Guid, string>> instanceEventTask = ReadInstanceEvents((Guid)cosmosInstanceEvents.First().Id, (Guid)cosmosInstanceEvents.Last().Id);
                SortedList<Guid, string> cInstanceEvents = new SortedList<Guid, string>();

                foreach (CosmosInstanceEvent instanceEvent in cosmosInstanceEvents)
                {
                    cInstanceEvents.Add((Guid)instanceEvent.Id, JsonSerializer.Serialize(instanceEvent, _jsonOptions));
                }
                var pgInstanceEvents = await instanceEventTask;
                foreach (var kvp in cInstanceEvents)
                {
                    pgInstanceEvents.TryGetValue(kvp.Key, out string? pgInstance);
                    if (pgInstance == null)
                    {
                        lock (_missingSw)
                            _missingSw.WriteLine($"Missing {kvp.Key}, {kvp.Value}");
                    }
                    else if (kvp.Value != pgInstance)
                    {
                        lock (_diffSw)
                            _diffSw.WriteLine($"Diff in content\r\n{kvp.Value}\r\n{pgInstance}");
                    }
                }

                lock (_lockObject)
                    _count += cInstanceEvents.Count;
                Console.WriteLine($"{cInstanceEvents.Count:N0}, {_count:N0}");
            }
        }

        private static async Task ProcessDataelements(char partition)
        {
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = 16, MaxItemCount = 50000 };
            FeedIterator<CosmosDataElement> query = _dataElementContainer.GetItemLinqQueryable<CosmosDataElement>(requestOptions: options)
                .Where(i => i.Id.StartsWith(partition) && (_cutoffDateTime > DateTime.Now || i.Ts < _cutoffEpoch))
                .OrderBy(i => i.Id).ToFeedIterator();

            while (query.HasMoreResults)
            {
                var cosmosElements = await query.ReadNextAsync();
                Task<SortedList<Guid, string>> instanceTask = ReadDataelements(cosmosElements.First().Id, cosmosElements.Last().Id);
                SortedList<Guid, string> cElements = new SortedList<Guid, string>();

                foreach (CosmosDataElement element in cosmosElements)
                {
                    if (element.Filename != null && element.Filename.Contains('\0'))
                        element.Filename = element.Filename.Replace("\0", null);
                    cElements.Add(Guid.Parse(element.Id), JsonSerializer.Serialize(element, _jsonOptions));
                }
                var pgElements = await instanceTask;
                foreach (var kvp in cElements)
                {
                    pgElements.TryGetValue(kvp.Key, out string? pgElement);
                    if (pgElement == null)
                    {
                        var element = JsonSerializer.Deserialize<CosmosDataElement>(kvp.Value);
                        bool instanceFound = (await GetInstance(element.InstanceGuid)) != null;
                        if (!_dataelementWhitelist.Contains(element.InstanceGuid))
                            lock (_missingSw)
                                _missingSw.WriteLine($"Missing, {(instanceFound ? "instance found" : "no instance")}: {kvp.Key}, {kvp.Value}");
                    }
                    else if (kvp.Value != pgElement)
                    {
                        lock (_diffSw)
                            _diffSw.WriteLine($"Diff in content\r\n{kvp.Value}\r\n{pgElement}");
                    }
                }

                lock (_lockObject)
                    _count += cElements.Count;
                Console.WriteLine($"{cElements.Count:N0}, {_count:N0}");
            }
        }

        private static async Task<CosmosInstance?> GetInstance(string id)
        {
            CosmosInstance? instance = null;
            QueryRequestOptions instanceOptions = new() { MaxBufferedItemCount = 0, MaxConcurrency = -1, MaxItemCount = 1 };
            FeedIterator<CosmosInstance> instanceQuery = _instanceContainer.GetItemLinqQueryable<CosmosInstance>(requestOptions: instanceOptions)
                .Where(i => i.Id == id)
                .ToFeedIterator();

            while (instanceQuery.HasMoreResults)
            {
                instance = (await instanceQuery.ReadNextAsync())?.FirstOrDefault();
                if (instance != null)
                {
                    return instance;
                }
            }

            return instance;
        }

        private static async Task<SortedList<Guid, string>> ReadInstances (string start, string end)
        {
            SortedList<Guid, string> instances = new SortedList<Guid, string>();
            await using NpgsqlCommand pgcomReadApp = _dataSource.CreateCommand("select alternateId, instance from storage.instances where alternateId between $1 and $2");
            pgcomReadApp.Parameters.AddWithValue(NpgsqlDbType.Uuid, Guid.Parse(start));
            pgcomReadApp.Parameters.AddWithValue(NpgsqlDbType.Uuid, Guid.Parse(end));
            await using NpgsqlDataReader reader = await pgcomReadApp.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                CosmosInstance instance = reader.GetFieldValue<CosmosInstance>("instance");
                instance.DataValues = instance.DataValues?.OrderBy(i => i.Key).ToDictionary();
                instance.PresentationTexts = instance.PresentationTexts?.OrderBy(i => i.Key).ToDictionary();
                instances.Add(reader.GetFieldValue<Guid>("alternateId"), JsonSerializer.Serialize(instance, _jsonOptions));
            }

            return instances;
        }

        private static async Task<SortedList<Guid, string>> ReadInstanceEvents(Guid start, Guid end)
        {
            SortedList<Guid, string> instanceEvents = new SortedList<Guid, string>();
            await using NpgsqlCommand pgcomReadApp = _dataSource.CreateCommand("select alternateId, event from storage.instanceevents where alternateId between $1 and $2");
            pgcomReadApp.Parameters.AddWithValue(NpgsqlDbType.Uuid, start);
            pgcomReadApp.Parameters.AddWithValue(NpgsqlDbType.Uuid, end);
            await using NpgsqlDataReader reader = await pgcomReadApp.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                CosmosInstanceEvent instanceEvent = reader.GetFieldValue<CosmosInstanceEvent>("event");
                instanceEvents.Add(reader.GetFieldValue<Guid>("alternateId"), JsonSerializer.Serialize(instanceEvent, _jsonOptions));
            }

            return instanceEvents;
        }

        private static async Task<SortedList<Guid, string>> ReadDataelements(string start, string end)
        {
            SortedList<Guid, string> instances = new SortedList<Guid, string>();
            await using NpgsqlCommand pgcomReadApp = _dataSource.CreateCommand("select alternateId, element from storage.dataelements where alternateId between $1 and $2");
            pgcomReadApp.Parameters.AddWithValue(NpgsqlDbType.Uuid, Guid.Parse(start));
            pgcomReadApp.Parameters.AddWithValue(NpgsqlDbType.Uuid, Guid.Parse(end));
            await using NpgsqlDataReader reader = await pgcomReadApp.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                CosmosDataElement element = reader.GetFieldValue<CosmosDataElement>("element");
                instances.Add(reader.GetFieldValue<Guid>("alternateId"), JsonSerializer.Serialize(element, _jsonOptions));
            }

            return instances;
        }

        private static async Task<SortedList<string, string>> ReadApplications()
        {
            SortedList<string, string> applications = new SortedList<string, string>();
            await using NpgsqlCommand pgcomReadApp = _dataSource.CreateCommand("select app, org, application from storage.applications");
            await using NpgsqlDataReader reader = await pgcomReadApp.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                CosmosApplication application = reader.GetFieldValue<CosmosApplication>("application");
                application.Id = application.Id.Replace('/', '-');
                application.Title = application.Title?.OrderBy(i => i.Key).ToDictionary();
                applications.Add(reader.GetFieldValue<string>("org").Replace('/', '-') + "-" + reader.GetFieldValue<string>("app"), JsonSerializer.Serialize(application, _jsonOptions));
            }

            return applications;
        }

        private static async Task<SortedList<string, string>> ReadTexts()
        {
            SortedList<string, string> texts = new SortedList<string, string>();
            await using NpgsqlCommand pgcomReadApp = _dataSource.CreateCommand("select app, org, language, textresource from storage.texts");
            await using NpgsqlDataReader reader = await pgcomReadApp.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                CosmosTextResource text = reader.GetFieldValue<CosmosTextResource>("textresource");
                texts.Add(reader.GetFieldValue<string>("org") + "-" + reader.GetFieldValue<string>("app") + "-" + reader.GetFieldValue<string>("language"), JsonSerializer.Serialize(text, _jsonOptions));
            }

            return texts;
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

        private static void PostgresInit()
        {
            _dataSource = NpgsqlDataSource.Create(_pgConnectionString);
        }

        private static void ReadWhitelists()
        {
            string fileName = @$"..\..\..\..\Common\bin\Debug\net8.0\WhitelistElements-{_environment}.csv";
            if (File.Exists(fileName))
            {
                foreach (string line in File.ReadAllLines(fileName))
                {
                    if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
                    {
                        _dataelementWhitelist.Add(line.Split(';')[0].Trim());
                    }
                }
            }

            fileName = @$"..\..\..\..\Common\bin\Debug\net8.0\WhitelistTexts-{_environment}.csv";
            if (File.Exists(fileName))
            {
                foreach (string line in File.ReadAllLines(fileName))
                {
                    if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
                    {
                        _textWhitelist.Add(line.Split(';')[0].Trim());
                    }
                }
            }
        }
    }
}
