using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager;
using Azure.ResourceManager.OperationalInsights;
using CsvHelper;
using CsvHelper.Configuration.Attributes;
using Spectre.Console;
using SQLite;

if (!Directory.Exists("data"))
    Directory.CreateDirectory("data");
if (!Directory.Exists("data/export"))
    Directory.CreateDirectory("data/export");

var db = new SQLiteAsyncConnection(Path.Combine("data", "data.db"));

await db.CreateTableAsync<ErrorRecord>();

var client = new ArmClient(new AzureCliCredential());

var cancellationToken = CancellationToken.None;

var subscriptions = await client.GetSubscriptions().GetAllAsync().ToArrayAsync();

var env = await File.ReadAllLinesAsync(".env");
foreach (var line in env)
{
    var parts = line.Split('=', 2);
    if (parts.Length != 2)
        throw new Exception($"Invalid .env line: {line}");

    var key = parts[0];
    var value = parts[1];
    if (Environment.GetEnvironmentVariable(key) is null)
        Environment.SetEnvironmentVariable(key, value);
}

var slackAccessToken = Environment.GetEnvironmentVariable("SlackAccessToken");
var slackSigningSecret = Environment.GetEnvironmentVariable("SlackSigningSecret");
var slackChannel = Environment.GetEnvironmentVariable("SlackChannel");
if (string.IsNullOrWhiteSpace(slackAccessToken) || string.IsNullOrWhiteSpace(slackSigningSecret) || string.IsNullOrWhiteSpace(slackChannel))
    throw new Exception("Missing config. Check the '.env' file");

const int SearchFromDays = 90;
TimeSpan PollInterval = TimeSpan.FromMinutes(5);

var instanceIdInUrlRegex = new Regex(@"(\d+)\/([0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12})", RegexOptions.Compiled);

long totalErrors = 0;
long dbSavedErrors = 0;

var tasks = subscriptions.Select(async subscription => 
{
    // if (!subscription.Data.DisplayName.Contains("skd", StringComparison.OrdinalIgnoreCase))
    //     return;

    if (!subscription.Data.DisplayName.StartsWith("altinn", StringComparison.OrdinalIgnoreCase))
        return;
    if (!subscription.Data.DisplayName.EndsWith("prod", StringComparison.OrdinalIgnoreCase))
        return;

    var split = subscription.Data.DisplayName.Split('-');
    if (split.Length != 3)
        return;

    var serviceOwner = split[1];
    if (serviceOwner.Any(char.IsLower))
        return;
    
    serviceOwner = serviceOwner.ToLowerInvariant();
    var rgs = await subscription.GetResourceGroups().GetAllAsync(cancellationToken: cancellationToken).ToArrayAsync();
    var rg = rgs.SingleOrDefault(rg => rg.Data.Name == $"monitor-{serviceOwner}-prod-rg");
    if (rg is null)
        return;

    var workspaces = await rg.GetOperationalInsightsWorkspaces().GetAllAsync(cancellationToken: cancellationToken).ToArrayAsync();
    var workspace = workspaces.SingleOrDefault(ws => ws.Data.Name == $"application-{serviceOwner}-prod-law");
    if (workspace is null)
        return;

    var lastError = await db.Table<ErrorRecord>()
        .Where(e => e.ServiceOwner == serviceOwner)
        .OrderByDescending(e => e.TimeGenerated)
        .FirstOrDefaultAsync();
    
    var searchFrom = lastError is not null ? lastError.TimeGenerated : DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(SearchFromDays)).AddSeconds(-1);

    AnsiConsole.MarkupLine( $"([bold]{serviceOwner}[/]) - initializing search from [blue]{searchFrom.UtcDateTime:o}[/]");

    var client = new LogsQueryClient(new AzureCliCredential());

    var timer = new PeriodicTimer(PollInterval);
    do 
    {
        var searchTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        var timeRange = searchTimestamp - searchFrom + TimeSpan.FromMinutes(5);
        AnsiConsole.MarkupLine( $"([bold]{serviceOwner}[/]) - searching from [blue]{searchFrom.UtcDateTime:o}[/] to [blue]{searchTimestamp.UtcDateTime:o}[/] (range {timeRange})");

        var query = $"""
            AppDependencies
            | where TimeGenerated > todatetime('{searchFrom.UtcDateTime:o}')
            | where Success == false
            | where Target startswith "platform.altinn.no"
            | where (Name startswith "POST /storage" and Name endswith "/events" and OperationName startswith "PUT Process/NextElement");

            AppDependencies
            | where TimeGenerated > todatetime('{searchFrom.UtcDateTime:o}')
            | where Success == false
            | where Target startswith "platform.altinn.no"
            | where (Name startswith "POST /events" and OperationName startswith "PUT Process/NextElement")
            | join kind=inner AppRequests on OperationId;
        """;
        Response<LogsQueryResult> results = await client.QueryResourceAsync(
            workspace.Id,
            query,
            new QueryTimeRange(timeRange),
            cancellationToken: cancellationToken
        );

        var tables = results.Value.AllTables;
        if (tables.Sum(t => t.Rows.Count) == 0)
        {
            searchFrom = searchTimestamp.AddMinutes(-1);
            continue;
        }

        var total = string.Join(", ", tables.Select((t, i) => $"error {i + 1}={(t.Rows.Count > 0 ? $"[red]{t.Rows.Count}[/]" : $"[green]{t.Rows.Count}[/]")}"));
        AnsiConsole.MarkupLine( $"([bold]{serviceOwner}[/]) - {total}");
        
        int tableCount = 0;
        for (int i = 0; i < tables.Count; i++)
        {
            var table = tables[i];
            if (table.Rows.Count == 0)
                continue;

            tableCount++;

            var records = new ErrorRecord[table.Rows.Count];
            for (int j = 0; j < table.Rows.Count; j++)
            {
                var r = table.Rows[j];
                var name = r.GetString(nameof(ErrorRecord.Name));

                string? rootRequestUrl = null;
                int? instanceOwnerPartyId = null;
                Guid? instanceId = null;
                if (table.Columns.Any(c => c.Name == "Url"))
                {
                    // Url column is present in requests table but not the dependency table
                    // so in this case the root trace/request has been joined in
                    rootRequestUrl = r.GetString("Url");
                    var match = instanceIdInUrlRegex.Match(rootRequestUrl);
                    instanceOwnerPartyId = int.Parse(match.Groups[1].Value);
                    instanceId = Guid.Parse(match.Groups[2].Value);
                }
                else 
                {
                    // When we don't have the root request joined in,
                    // lets see if we can extract the instance id from the name
                    var match = instanceIdInUrlRegex.Match(name);
                    instanceOwnerPartyId = int.Parse(match.Groups[1].Value);
                    instanceId = Guid.Parse(match.Groups[2].Value);
                }

                var timeGenerated = r.GetDateTimeOffset(nameof(ErrorRecord.TimeGenerated)) ?? default;

                var record = new ErrorRecord
                {
                    ServiceOwner = serviceOwner,
                    TimeGenerated = timeGenerated,
                    TimeIngested = searchTimestamp,
                    InstanceOwnerPartyId = instanceOwnerPartyId,
                    InstanceId = instanceId,
                    Id = r.GetString(nameof(ErrorRecord.Id)),
                    Target = r.GetString(nameof(ErrorRecord.Target)),
                    DependencyType = r.GetString(nameof(ErrorRecord.DependencyType)),
                    Name = name,
                    Data = r.GetString(nameof(ErrorRecord.Data)),
                    Success = r.GetBoolean(nameof(ErrorRecord.Success)),
                    ResultCode = r.GetString(nameof(ErrorRecord.ResultCode)),
                    DurationMs = r.GetDouble(nameof(ErrorRecord.DurationMs)),
                    PerformanceBucket = r.GetString(nameof(ErrorRecord.PerformanceBucket)),
                    Properties = r.GetString(nameof(ErrorRecord.Properties)),
                    OperationName = r.GetString(nameof(ErrorRecord.OperationName)),
                    OperationId = r.GetString(nameof(ErrorRecord.OperationId)),
                    ParentId = r.GetString(nameof(ErrorRecord.ParentId)),
                    AppVersion = r.GetString(nameof(ErrorRecord.AppVersion)),
                    AppRoleName = r.GetString(nameof(ErrorRecord.AppRoleName)),
                    AlertedInSlack = false,
                    ErrorNumber = i + 1,
                };

                if (timeGenerated > searchFrom)
                {
                    lastError = record;
                    searchFrom = timeGenerated;
                }

                records[j] = record;
            }

            Interlocked.Add(ref totalErrors, records.Length);

            var savedCount = await db.InsertAllAsync(records);

            Interlocked.Add(ref dbSavedErrors, savedCount);

            // {
            //     var filename = $"data/export/{serviceOwner}-error-{i + 1}.json";
            //     await using var writer = File.OpenWrite(filename);
            //     await JsonSerializer.SerializeAsync(writer, records, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
            // }

            // {
            //     var filename = $"data/export/{serviceOwner}-error-{i + 1}.csv";
            //     await using var writer = new StreamWriter(filename);
            //     await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
            //     await csv.WriteRecordsAsync(records, cancellationToken);
            // }
        }
        
        AnsiConsole.MarkupLine($"([bold]{serviceOwner}[/]) - processed [blue]{tableCount}[/] queries, sleeping for [blue]{PollInterval}[/]...");
    }
    while (await timer.WaitForNextTickAsync(cancellationToken));
}).ToList();

tasks.Add(Task.Run(async () => 
{
    var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

    var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.Authorization = new("Bearer", slackAccessToken);

    do 
    {
        var alertFrom = new DateTimeOffset(2025, 2, 6, 0, 0, 0, TimeSpan.Zero);
        var errors = await db.Table<ErrorRecord>()
            .Where(e => e.AlertedInSlack == false && e.TimeGenerated >= alertFrom)
            .OrderBy(e => e.TimeGenerated)
            .ToArrayAsync();

        foreach (var error in errors)
        {
            var text = $"""
            *ALERT* `{error.TimeGenerated.UtcDateTime:O}`:
            - App: *{error.ServiceOwner}*/*{error.AppRoleName}*/*{error.AppVersion}*
            - Feil: *{error.Name}* (status *{error.ResultCode}*, *{error.DurationMs}ms*)
            - Instansen: *{error.InstanceOwnerPartyId}*/*{error.InstanceId}*
            - Operation ID: *{error.OperationId}*
            """;
            var url = "https://slack.com/api/chat.postMessage";
            using var response = await httpClient.PostAsJsonAsync(url, new 
            {
                channel = slackChannel,
                text = text,
                mrkdwn = true,
            });
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new Exception($"Failed to send message to slack: {response.StatusCode}: {body}");
            }

            await using (var writer = File.AppendText("data/export/slack.txt"))
            {
                await writer.WriteLineAsync($"{text}\n");
            }

            error.AlertedInSlack = true;
            await db.UpdateAsync(error);
        }

        if (errors.Length > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Alerted in slack:[/] {errors.Length}");
        }
    }
    while (await timer.WaitForNextTickAsync(cancellationToken));
}));


tasks.Add(Task.Run(async () => 
{
    var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
    do 
    {
        var filename = $"data/export/all-errors.csv";
        var fileInfo = new FileInfo(filename);

        var lastRecord = await db.Table<ErrorRecord>()
            .OrderByDescending(e => e.TimeIngested)
            .FirstOrDefaultAsync();

        if (!fileInfo.Exists || lastRecord.TimeIngested > fileInfo.LastWriteTimeUtc)
        {
            if (fileInfo.Exists)
                fileInfo.Delete();

            await using var writer = new StreamWriter(filename);
            await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

            var dbRecords = await db.Table<ErrorRecord>()
                .OrderBy(r => r.TimeGenerated)
                .ToArrayAsync();

            var records = dbRecords.Select(r => new ErrorRecordForCsv
            {
                TimeGenerated = r.TimeGenerated,
                ServiceOwner = r.ServiceOwner,
                AppRoleName = r.AppRoleName,
                AppVersion = r.AppVersion,
                InstanceOwnerPartyId = r.InstanceOwnerPartyId,
                InstanceId = r.InstanceId,

                Id = r.Id,
                OperationId = r.OperationId,
                ParentId = r.ParentId,
                OperationName = r.OperationName,
                Name = r.Name,
                Success = r.Success,
                ResultCode = r.ResultCode,
                DurationMs = r.DurationMs,
                PerformanceBucket = r.PerformanceBucket,
                Target = r.Target,
                DependencyType = r.DependencyType,
                Data = r.Data,
                Properties = r.Properties,
                AlertedInSlack = r.AlertedInSlack,
                ErrorNumber = r.ErrorNumber,
            }).ToArray();
            await csv.WriteRecordsAsync(records);

            AnsiConsole.MarkupLine($"[bold]Synced CSV:[/] {records.Length} records to [blue]{filename}[/]");
        }
    }
    while (await timer.WaitForNextTickAsync(cancellationToken));
}));

tasks.Add(Task.Run(async () => 
{
    var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
    do 
    {
        AnsiConsole.MarkupLine($"[bold]Total errors:[/] {totalErrors}, [bold]DB saved:[/] {dbSavedErrors}");
    }
    while (await timer.WaitForNextTickAsync(cancellationToken));
}));

await Task.WhenAll(tasks);


public sealed class ErrorRecord
{
	[PrimaryKey, AutoIncrement]
	public int PK { get; set; }

	[Indexed]
    public string? ServiceOwner { get; init; }
    
	[Indexed]
    public DateTimeOffset TimeGenerated { get; init; }

    [Indexed]
    public DateTimeOffset TimeIngested { get; init; }

	[Indexed]
    public int? InstanceOwnerPartyId { get; init; }
	[Indexed]
    public Guid? InstanceId { get; init; }
	[Indexed]
    public string? Id { get; init; } 
    public string? Target { get; init; } 
    public string? DependencyType { get; init; } 
    public string? Name { get; init; } 
    public string? Data { get; init; } 
	[Indexed]
    public bool? Success { get; init; } 
	[Indexed]
    public string? ResultCode { get; init; }
    public double? DurationMs { get; init; }
    public string? PerformanceBucket { get; init; }
    public string? Properties { get; init; }
    public string? OperationName { get; init; }
	[Indexed]
    public string? OperationId { get; init; }
    public string? ParentId { get; init; }
	[Indexed]
    public string? AppVersion { get; init; }
	[Indexed]
    public string? AppRoleName { get; init; }

    [Indexed]
    public bool AlertedInSlack { get; set; }

    [Indexed]
    public int ErrorNumber { get; set; }
}


public sealed class ErrorRecordForCsv
{
    [Format("dd.MM.yyyy HH:mm:ss")]
    public DateTimeOffset TimeGenerated { get; init; }

    public string? ServiceOwner { get; init; }
    public string? AppRoleName { get; init; }
    public string? AppVersion { get; init; }
    public int? InstanceOwnerPartyId { get; init; }
    public Guid? InstanceId { get; init; }
    
    public string? Id { get; init; } 
    public string? OperationId { get; init; }
    public string? ParentId { get; init; }
    public string? OperationName { get; init; }
    public string? Name { get; init; } 
    public bool? Success { get; init; } 
    public string? ResultCode { get; init; }
    public double? DurationMs { get; init; }
    public string? PerformanceBucket { get; init; }
    public string? Target { get; init; } 
    public string? DependencyType { get; init; } 
    public string? Data { get; init; }     
    public string? Properties { get; init; }
    public bool AlertedInSlack { get; set; }
    public int ErrorNumber { get; set; }
}



