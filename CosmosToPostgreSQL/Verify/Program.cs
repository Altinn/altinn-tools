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

namespace Verify
{
    internal class Program
    {
        const string _logFilename = nameof(Program) + ".log";
        const string _errorFilename = nameof(Program) + "-errors.log";

        private static readonly DateTime _cutoffDateTime = DateTime.Parse("2023-12-31 00:49");
        private static readonly long _cutoffEpoch = new DateTimeOffset(_cutoffDateTime).ToUniversalTime().ToUnixTimeSeconds();
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

            _cosmosUrl = config["cosmosUrl"];
            _cosmosSecret = config["cosmosSecret"];
            _pgConnectionString = config["pgConnectionString"];
            _environment = config["environment"];


            await CosmosInitAsync();
            await PostgresInitAsync();
            ReadWhitelists();
            await ProcessInstancesAll();
            await ProcessDataelementsAll();
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

        private static async Task ProcessInstances(char partition)
        {
            QueryRequestOptions options = new() { MaxBufferedItemCount = 0, MaxConcurrency = 16, MaxItemCount = 50000 };
            FeedIterator<CosmosInstance> query = _instanceContainer.GetItemLinqQueryable<CosmosInstance>(requestOptions: options)
                .Where(i => i.Id.StartsWith(partition) && (_cutoffDateTime > DateTime.Now || i.Ts < _cutoffEpoch))
                .OrderBy(i => i.Id).ToFeedIterator();

            while (query.HasMoreResults)
            {
                var cosmosInstances = await query.ReadNextAsync();
                //// Console.WriteLine(cosmosInstances.First().Id + " - " + cosmosInstances.Last().Id);
                Task<SortedList<Guid, string>> instanceTask = ReadInstances(cosmosInstances.First().Id, cosmosInstances.Last().Id);
                SortedList<Guid, string> cInstances = new SortedList<Guid, string>();

                foreach (CosmosInstance instance in cosmosInstances)
                {
                    // Todo: verify that data should always be null. Found actual data here: 00a4b483-d7e6-4f19-8a11-4405a9d74a0d
                    instance.Data = null;
                    instance.DataValues = instance.DataValues?.OrderBy(i => i.Key).ToDictionary();
                    instance.PresentationTexts = instance.PresentationTexts?.OrderBy(i => i.Key).ToDictionary();
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
                    // Todo: verify that data should always be null. Found actual data here: 00a4b483-d7e6-4f19-8a11-4405a9d74a0d
                    //element.Data = null;
                    //element.DataValues = element.DataValues?.OrderBy(i => i.Key).ToDictionary();
                    //element.PresentationTexts = element.PresentationTexts?.OrderBy(i => i.Key).ToDictionary();
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
                //Guid id = reader.GetFieldValue<Guid>("alternateId");
                //CosmosInstance jsoni = reader.GetFieldValue<CosmosInstance>("instance");
                //string json = reader.GetFieldValue<string>("instance");
                CosmosInstance instance = reader.GetFieldValue<CosmosInstance>("instance");
                instance.DataValues = instance.DataValues?.OrderBy(i => i.Key).ToDictionary();
                instance.PresentationTexts = instance.PresentationTexts?.OrderBy(i => i.Key).ToDictionary();
                instances.Add(reader.GetFieldValue<Guid>("alternateId"), JsonSerializer.Serialize(instance, _jsonOptions));
            }

            return instances;
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
                //Guid id = reader.GetFieldValue<Guid>("alternateId");
                //CosmosInstance jsoni = reader.GetFieldValue<CosmosInstance>("instance");
                //string json = reader.GetFieldValue<string>("instance");
                CosmosDataElement element = reader.GetFieldValue<CosmosDataElement>("element");
                //element.DataValues = element.DataValues?.OrderBy(i => i.Key).ToDictionary();
                //element.PresentationTexts = element.PresentationTexts?.OrderBy(i => i.Key).ToDictionary();
                instances.Add(reader.GetFieldValue<Guid>("alternateId"), JsonSerializer.Serialize(element, _jsonOptions));
            }

            return instances;
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
        }

        private static void ReadWhitelists()
        {
            foreach (string line in File.ReadAllLines(@$"..\..\..\..\Common\bin\Debug\net8.0\WhitelistElements-{_environment}.csv"))
            {
                if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
                {
                    _dataelementWhitelist.Add(line.Split(';')[0]);
                }
            }

            foreach (string line in File.ReadAllLines(@$"..\..\..\..\Common\bin\Debug\net8.0\WhitelistTexts-{_environment}.csv"))
            {
                if (!line.StartsWith('#') && !string.IsNullOrWhiteSpace(line))
                {
                    _textWhitelist.Add(line.Split(';')[0]);
                }
            }
        }
    }
}
