using System.Reflection;
using Altinn.Apps.Monitoring.Tests.Parallelism;
using Xunit.Internal;
using Xunit.Sdk;
using Xunit.v3;

[assembly: Xunit.TestFramework(typeof(ParallelTestFramework))]

namespace Altinn.Apps.Monitoring.Tests.Parallelism;

internal sealed class ParallelTestFramework : XunitTestFramework
{
    public ParallelTestFramework()
        : base() { }

    protected override ITestFrameworkExecutor CreateExecutor(Assembly assembly) =>
        new ParallelTestFrameworkExecutor(
            new XunitTestAssembly(Guard.ArgumentNotNull(assembly), null, assembly.GetName().Version)
        );

    public static ParallelOptions CreateParallelOptions(ContextBase context) =>
        new()
        {
            TaskScheduler = TaskScheduler.Current,
            CancellationToken = context.CancellationTokenSource.Token,
            MaxDegreeOfParallelism = Environment.ProcessorCount, // Heuristic: assume some degree of IO-boundness
        };
}

internal sealed class ParallelTestFrameworkExecutor : XunitTestFrameworkExecutor
{
    public ParallelTestFrameworkExecutor(XunitTestAssembly assembly)
        : base(assembly) { }

    public override async ValueTask RunTestCases(
        IReadOnlyCollection<IXunitTestCase> testCases,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions,
        CancellationToken cancellationToken
    )
    {
        // SetEnvironment(EnvironmentVariables.AssertEquivalentMaxDepth, executionOptions.AssertEquivalentMaxDepth());
        // SetEnvironment(EnvironmentVariables.PrintMaxEnumerableLength, executionOptions.PrintMaxEnumerableLength());
        // SetEnvironment(EnvironmentVariables.PrintMaxObjectDepth, executionOptions.PrintMaxObjectDepth());
        // SetEnvironment(EnvironmentVariables.PrintMaxObjectMemberCount, executionOptions.PrintMaxObjectMemberCount());
        // SetEnvironment(EnvironmentVariables.PrintMaxStringLength, executionOptions.PrintMaxStringLength());

        await ParallelTestAssemblyRunner.Instance.Run(
            TestAssembly,
            testCases,
            executionMessageSink,
            executionOptions,
            cancellationToken
        );
    }
}

internal sealed class ParallelTestAssemblyRunner : XunitTestAssemblyRunner
{
    public static new ParallelTestAssemblyRunner Instance { get; } = new();

    protected override async ValueTask<RunSummary> RunTestCollection(
        XunitTestAssemblyRunnerContext ctxt,
        IXunitTestCollection testCollection,
        IReadOnlyCollection<IXunitTestCase> testCases
    )
    {
        Guard.ArgumentNotNull(ctxt);
        Guard.ArgumentNotNull(testCollection);
        Guard.ArgumentNotNull(testCases);

        var testCaseOrderer = ctxt.AssemblyTestCaseOrderer ?? DefaultTestCaseOrderer.Instance;

        return await ParallelTestCollectionRunner.Instance.Run(
            testCollection,
            testCases,
            ctxt.ExplicitOption,
            ctxt.MessageBus,
            testCaseOrderer,
            ctxt.Aggregator.Clone(),
            ctxt.CancellationTokenSource,
            ctxt.AssemblyFixtureMappings
        );
    }
}

internal sealed class ParallelTestCollectionRunner : XunitTestCollectionRunner
{
    public static new ParallelTestCollectionRunner Instance { get; } = new();

    protected override ValueTask<RunSummary> RunTestClass(
        XunitTestCollectionRunnerContext ctxt,
        IXunitTestClass? testClass,
        IReadOnlyCollection<IXunitTestCase> testCases
    )
    {
        Guard.ArgumentNotNull(ctxt);
        Guard.ArgumentNotNull(testCases);

        if (testClass is null)
            return new(
                XunitRunnerHelper.FailTestCases(
                    ctxt.MessageBus,
                    ctxt.CancellationTokenSource,
                    testCases,
                    "Test case '{0}' does not have an associated class and cannot be run by XunitTestClassRunner",
                    sendTestClassMessages: true,
                    sendTestMethodMessages: true
                )
            );

        return ParallelTestClassRunner.Instance.Run(
            testClass,
            testCases,
            ctxt.ExplicitOption,
            ctxt.MessageBus,
            ctxt.TestCaseOrderer,
            ctxt.Aggregator.Clone(),
            ctxt.CancellationTokenSource,
            ctxt.CollectionFixtureMappings
        );
    }

    protected override async ValueTask<RunSummary> RunTestClasses(
        XunitTestCollectionRunnerContext ctxt,
        Exception? exception
    )
    {
        Guard.ArgumentNotNull(ctxt);

        var summary = new RunSummary();

        var grouping = ctxt.TestCases.GroupBy(tc => tc.TestClass, TestClassComparer.Instance).Index().ToArray();
        RunSummary[] results = new RunSummary[grouping.Length];

        await Parallel.ForEachAsync(
            grouping,
            ParallelTestFramework.CreateParallelOptions(ctxt),
            async (testCaseData, _) =>
            {
                var (index, testCasesByClass) = testCaseData;
                var testClass = testCasesByClass.Key as IXunitTestClass;
                var testCases = testCasesByClass.CastOrToReadOnlyCollection();

                var result = exception is not null
                    ? await FailTestClass(ctxt, testClass, testCases, exception)
                    : await RunTestClass(ctxt, testClass, testCases);

                results[index] = result;
            }
        );

        foreach (var result in results)
            summary.Aggregate(result);

        return summary;
    }
}

internal sealed class ParallelTestClassRunner : XunitTestClassRunner
{
    public static new ParallelTestClassRunner Instance { get; } = new();

    protected override async ValueTask<RunSummary> RunTestMethods(
        XunitTestClassRunnerContext ctxt,
        Exception? exception
    )
    {
        Guard.ArgumentNotNull(ctxt);

        var summary = new RunSummary();
        IReadOnlyCollection<IXunitTestCase> orderedTestCases;
        object?[] constructorArguments;

        if (exception is null)
        {
            orderedTestCases = OrderTestCases(ctxt);
            constructorArguments = await CreateTestClassConstructorArguments(ctxt);
            exception = ctxt.Aggregator.ToException();
            ctxt.Aggregator.Clear();
        }
        else
        {
            orderedTestCases = ctxt.TestCases;
            constructorArguments = [];
        }

        var grouping = orderedTestCases.GroupBy(tc => tc.TestMethod, TestMethodComparer.Instance).Index().ToArray();
        RunSummary[] results = new RunSummary[grouping.Length];
        await Parallel.ForEachAsync(
            grouping,
            ParallelTestFramework.CreateParallelOptions(ctxt),
            async (testCaseData, _) =>
            {
                var (index, testCasesByMethod) = testCaseData;
                var testMethod = testCasesByMethod.Key as IXunitTestMethod;
                var testCases = testCasesByMethod.CastOrToReadOnlyCollection();

                var result = exception is not null
                    ? await FailTestMethod(ctxt, testMethod, testCases, constructorArguments, exception)
                    : await RunTestMethod(ctxt, testMethod, testCases, constructorArguments);

                results[index] = result;
            }
        );

        foreach (var result in results)
            summary.Aggregate(result);

        return summary;
    }

    protected override ValueTask<RunSummary> RunTestMethod(
        XunitTestClassRunnerContext ctxt,
        IXunitTestMethod? testMethod,
        IReadOnlyCollection<IXunitTestCase> testCases,
        object?[] constructorArguments
    )
    {
        Guard.ArgumentNotNull(ctxt);

        // Technically not possible because of the design of TTestClass, but this signature is imposed
        // by the base class, which allows method-less tests
        return testMethod is null
            ? new(
                XunitRunnerHelper.FailTestCases(
                    ctxt.MessageBus,
                    ctxt.CancellationTokenSource,
                    testCases,
                    "Test case '{0}' does not have an associated method and cannot be run by XunitTestMethodRunner",
                    sendTestMethodMessages: true
                )
            )
            : ParallelTestMethodRunner.Instance.Run(
                testMethod,
                testCases,
                ctxt.ExplicitOption,
                ctxt.MessageBus,
                ctxt.Aggregator.Clone(),
                ctxt.CancellationTokenSource,
                constructorArguments
            );
    }
}

internal sealed class ParallelTestMethodRunner : XunitTestMethodRunner
{
    public static new ParallelTestMethodRunner Instance { get; } = new();

    protected override async ValueTask<RunSummary> RunTestCases(XunitTestMethodRunnerContext ctxt, Exception? exception)
    {
        Guard.ArgumentNotNull(ctxt);

        var summary = new RunSummary();

        RunSummary[] results = new RunSummary[ctxt.TestCases.Count];
        await Parallel.ForEachAsync(
            ctxt.TestCases.Index(),
            ParallelTestFramework.CreateParallelOptions(ctxt),
            async (testCaseData, _) =>
            {
                var (index, testCase) = testCaseData;

                var result = exception is not null
                    ? await FailTestCase(ctxt, testCase, exception)
                    : await RunTestCase(ctxt, testCase);

                results[index] = result;
            }
        );

        foreach (var result in results)
            summary.Aggregate(result);

        return summary;
    }

    protected override ValueTask<RunSummary> RunTestCase(XunitTestMethodRunnerContext ctxt, IXunitTestCase testCase)
    {
        Guard.ArgumentNotNull(ctxt);
        Guard.ArgumentNotNull(testCase);

        if (testCase is ISelfExecutingXunitTestCase selfExecutingTestCase)
            return selfExecutingTestCase.Run(
                ctxt.ExplicitOption,
                ctxt.MessageBus,
                ctxt.ConstructorArguments,
                ctxt.Aggregator.Clone(),
                ctxt.CancellationTokenSource
            );

        return XunitRunnerHelper.RunXunitTestCase(
            testCase,
            ctxt.MessageBus,
            ctxt.CancellationTokenSource,
            ctxt.Aggregator.Clone(),
            ctxt.ExplicitOption,
            ctxt.ConstructorArguments
        );
    }
}
