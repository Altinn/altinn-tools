using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using Buildalyzer;
using CsvHelper;
using LibGit2Sharp;
using Spectre.Console;

namespace Altinn.Analysis.Cli;

internal sealed class AppsAnalyzer
{
    private readonly AnalysisConfig _config;

    private DirectoryInfo? _directory;
    private int _parallelism;

    public AppsAnalyzer(AnalysisConfig config)
    {
        _config = config;
    }

    private bool VerifyConfig()
    {
        var table = new Table();
        table.Border(TableBorder.None);
        table.AddColumn(new TableColumn(""));
        table.AddColumn(new TableColumn(""));

        _parallelism = Math.Min(Constants.LimitMaxParallelism, _config.MaxParallelism);
        var directory = Path.GetFullPath(
            _config.Directory.Replace(
                "~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            )
        );
        table.AddRow(
            new Markup(nameof(FetchConfig.Directory)),
            new Markup($"= [bold]{directory}[/]")
        );
        table.AddRow(new Markup("Parallelism"), new Markup($"= [bold]{_parallelism}[/]"));
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (!Debugger.IsAttached)
        {
            var proceed = AnsiConsole.Prompt(new ConfirmationPrompt($"Continue?"));
            return proceed;
        }

        return true;
    }

    public async Task Analyze(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[blue]Analyzing Altinn apps[/]... Config:");
        if (!VerifyConfig())
            return;

        var directory = Path.GetFullPath(
            _config.Directory.Replace(
                "~",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            )
        );
        _directory = new DirectoryInfo(directory);
        if (!_directory.Exists)
        {
            AnsiConsole.MarkupLine(
                $"Directory for downloaded apps [red]doesn't exist[/]: [bold]{directory}[/]"
            );
            return;
        }

        var repos = GetRepos(cancellationToken);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"Analysing [blue]{repos.Count}[/] apps").LeftJustified());

        AppAnalysisResult[]? results = null;
        await AnsiConsole
            .Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new ElapsedTimeColumn(),
                new SpinnerColumn()
            )
            .HideCompleted(true)
            .AutoClear(true)
            .StartAsync(async ctx =>
            {
                results = await Task.Run(
                    () => AnalyzeRepos(ctx, repos, cancellationToken),
                    cancellationToken
                );
            });

        Debug.Assert(results is not null);
        AnsiConsole.MarkupLine($"[green]Successfully[/] analyzed [blue]{results.Length}[/] apps");

        // Ample opportunity for SIMD below...
        var timedOut = results.Count(r => r.TimedOut is true);
        var invalidProjects = results.Count(r => r.ValidProject is false);
        var failedBuilds = results.Count(r => r.Builds is false);
        var noAppLib = results.Count(r => r.HasAppLib is false);
        var oldAppLib = results.Count(r => r.HasLatestAppLib is false);
        var aOkay = results.Count(r => r.OK);

        AnsiConsole.MarkupLine("[green]Overview[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new BarChart()
                .Width(60)
                .Label("[green bold underline]Analysis[/]")
                .LeftAlignLabel()
                .AddItem("Timed out", timedOut, Color.Red)
                .AddItem("Invalid project", invalidProjects, Color.Red)
                .AddItem("Failed builds", failedBuilds, Color.Red)
                .AddItem("Missing applib", noAppLib, Color.Red)
                .AddItem("Old applib", oldAppLib, Color.Yellow)
                .AddItem("On applib v8", aOkay, Color.Green)
        );

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Symbol references totals[/]");
        var symbolReferences = results
            .SelectMany(r =>
                r.SymbolReferenceCountsBySymbol.Select(kvp =>
                    (Symbol: kvp.Key, References: kvp.Value.AsReadOnly(), App: r.AppRepository)
                )
            )
            .GroupBy(r => r.Symbol)
            .Select(grp => new
            {
                Symbol = grp.Key,
                Count = grp.Sum(x => x.References.Count),
                Apps = string.Join(
                    ", ",
                    grp.Where(x => x.References.Count > 0)
                        .Select(x => $"{x.App.Org}/{x.App.Name}")
                        .Order()
                ),
            })
            .OrderByDescending(x => x.Count)
            .ToArray();

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("Symbol"));
        table.AddColumn(new TableColumn("Reference count"));
        table.AddColumn(new TableColumn("Apps"));

        foreach (var item in symbolReferences)
        {
            table.AddRow(item.Symbol, item.Count.ToString(CultureInfo.InvariantCulture), item.Apps);
        }

        AnsiConsole.Write(table);

        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Writing CSV report[/]...");

            // Write CSV report to analysis directory
            var filename = Path.Combine(_directory.FullName, "apps.csv");
            await using var writer = new StreamWriter(filename);
            await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

            var records = results
                .Select(r => new AppCsvRow
                {
                    Org = r.AppRepository.Org,
                    Name = r.AppRepository.Name,
                    TimedOut = r.TimedOut,
                    Builds = r.Builds,
                    AppCoreVersion = r.AppCoreVersion,
                    WarningCount = r.WarningCount,
                })
                .ToArray();
            await csv.WriteRecordsAsync(records, cancellationToken);

            AnsiConsole.MarkupLine(
                $"[green]Successfully[/] wrote CSV report to: [bold]{filename}[/]"
            );
        }
    }

    private List<AppRepository> GetRepos(CancellationToken cancellationToken)
    {
        Debug.Assert(_directory is not null);

        var result = new List<AppRepository>(64);
        var orgsCounter = 0;
        foreach (var orgDir in Directory.EnumerateDirectories(_directory.FullName))
        {
            var org = orgDir.Substring(orgDir.LastIndexOf(Path.DirectorySeparatorChar) + 1);
            cancellationToken.ThrowIfCancellationRequested();

            var reposCounter = 0;
            foreach (var repoDir in Directory.EnumerateDirectories(orgDir))
            {
                var name = repoDir.Substring(repoDir.LastIndexOf(Path.DirectorySeparatorChar) + 1);
                var repoDirInfo = new DirectoryInfo(
                    Path.Combine(repoDir, Constants.MainBranchFolder)
                );
                if (!repoDirInfo.Exists)
                {
                    AnsiConsole.MarkupLine(
                        $"Couldn't find main branch folder for repo: '{repoDirInfo.Name}'"
                    );
                    continue;
                }
                result.Add(new AppRepository(repoDirInfo, org, name));
                reposCounter++;

                if (reposCounter == Constants.LimitRepos)
                    break;
            }

            orgsCounter++;

            if (orgsCounter == Constants.LimitOrgs)
                break;
        }

        return result;
    }

    private async Task<AppAnalysisResult[]> AnalyzeRepos(
        ProgressContext ctx,
        List<AppRepository> repos,
        CancellationToken cancellationToken
    )
    {
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = _parallelism,
        };

        var results = new ConcurrentBag<AppAnalysisResult>();

        await Parallel.ForEachAsync(
            repos,
            options,
            async (repo, cancellationToken) =>
            {
                var (dir, org, name) = repo;
                var task = ctx.AddTask($"[green]{org}/{name}[/]", autoStart: false, maxValue: 1);
                task.IsIndeterminate = true;
                task.StartTask();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(1));
                cancellationToken = cts.Token;

                // Doc for selecting refs:
                // https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md
                string[] findRefs =
                [
                    "M:Altinn.App.Core.Internal.Auth.IAuthorizationClient.GetUserRoles(System.Int32,System.Int32)",
                    // "T:Altinn.App.Core.Features.IProcessTaskEnd",
                ];
                try
                {
                    var result = await AppAnalyzer.Run(repo, findRefs, cancellationToken);
                    results.Add(result);
                }
                catch (OperationCanceledException)
                {
                    results.Add(new(repo, timedOut: true));
                }

                task.Increment(1.0);
                task.StopTask();
            }
        );

        return results.ToArray();
    }

    private sealed record AppCsvRow
    {
        public required string Org { get; init; }
        public required string Name { get; init; }
        public required bool? TimedOut { get; init; }
        public required bool? Builds { get; init; }
        public required string? AppCoreVersion { get; init; }
        public required uint? WarningCount { get; init; }
    }
}
