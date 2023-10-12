using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotCover;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.Npm;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

[GitHubActions(
    "test",
    GitHubActionsImage.UbuntuLatest,
    InvokedTargets = new[] { nameof(Test) },
    OnPushBranchesIgnore = new[] { "main", "origin/main" },
    FetchDepth = 0/*,
    JobNamePrefix = "test"*/)]
[GitHubActions(
    "pull-request",
    GitHubActionsImage.UbuntuLatest,
    On = new[] { GitHubActionsTrigger.PullRequest },
    InvokedTargets = new[] { nameof(Cover), nameof(IntegrationTest) },
    FetchDepth = 0/*,
    JobNamePrefix = "test"*/)]
[GitHubActions(
    "release",
    GitHubActionsImage.UbuntuLatest,
    InvokedTargets = new[] { nameof(Cover), nameof(DockerPush) },
    OnPushBranches = new[] { "main", "origin/main" },
    EnableGitHubToken = true,
    FetchDepth = 0/*,
    JobNamePrefix = "test"*/)]
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution(GenerateProjects = true)] readonly Solution Solution;
    [GitVersion] readonly GitVersion GitVersion;
    GitHubActions GitHubActions => GitHubActions.Instance;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath TestResultsDirectory => TestsDirectory / "TestResults";
    AbsolutePath CoverageResultsDirectory => TestsDirectory / "TestCoverage";
    AbsolutePath IntegrationTestsDirectory = RootDirectory / "integration" / "httpstatusintegrationtests";

    [Parameter(
        description: "The name to give the docker image when its built - Default is 'tonesandtones/httpstatus'",
        Name = "ImageName")]
    string dockerImageName = "tonesandtones/httpstatus";

    [Parameter(
        description: "The login hostname of the docker registry - Default is 'ghcr.io'",
        Name = "DockerRegistry")]
    string dockerRepository = "ghcr.io";

    [Parameter(
        description: "The host port to map to the docker container when running integration tests - Default is 8080",
        Name = "HostPort")]
    int hostPort = 8080;

    [Parameter(
        description: "The container port to map in the docker container when running integration tests. " +
                     "This must match Dockerfile's EXPOSE port - Default is 80",
        Name = "ContainerPort")]
    int containerPort = 80;

    [Parameter(
        description: "Whether to _not_ docker rm the container that's started for integration tests when the tests " +
                     "have finished. Only applies to the IntegrationTest target - Default is false",
        Name = "NoCleanUp")]
    bool noCleanup = false;

    string DockerFullImageName => $"{dockerRepository}/{dockerImageName}";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(AbsolutePathExtensions.DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(AbsolutePathExtensions.DeleteDirectory);
            CoverageResultsDirectory.DeleteDirectory();
            TestResultsDirectory.DeleteDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s.SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .EnableNoRestore());
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Before(DockerBuild) //if run with DockerBuild, then do test before DockerBuild
        .Produces(TestResultsDirectory)
        .OnlyWhenDynamic(() => !ExecutionPlan.Contains(Cover)) //Don't do Test if we're also doing Cover.
        .Executes(() =>
        {
            var testLoggerTypes = new[] { "trx", "html" };
            var testProjects = new[] { Solution.HttpStatusTests };

            foreach (var testProject in testProjects)
            {
                var testLoggers = testLoggerTypes.Select(x => $"{x};LogFilePrefix={testProject.Name}");
                DotNetTest(s => s
                    .SetLoggers(testLoggers)
                    .SetResultsDirectory(TestResultsDirectory)
                    .SetProjectFile(testProject)
                    //Disable config file watching - the tests start many instances of the web host
                    .SetProcessEnvironmentVariable("ASPNETCORE_hostBuilder__reloadConfigOnChange", "false")
                    //Don't log each request when running the tests
                    .SetProcessEnvironmentVariable("Logging__LogLevel__HttpLoggingMiddlewareOverride", "Warning")
                );
            }
        });

    Target Cover => _ => _
        .DependsOn(Compile)
        .Before(DockerBuild) //if run with DockerBuild, then do test before DockerBuild
        .Produces(CoverageResultsDirectory, TestResultsDirectory)
        .Executes(() =>
        {
            var testLoggerTypes = new[] { "trx", "html" };

            var dotnetPath = ToolPathResolver.GetPathExecutable("dotnet");
            var projectCoverageDirectory = CoverageResultsDirectory / "projects";
            var testProjects = new[] { Solution.HttpStatusTests };

            AbsolutePath coverageResultFile = null;
            AbsolutePath coverageReportFile = null;

            foreach (var testProject in testProjects)
            {
                var testLoggers = testLoggerTypes.Select(x => $"{x};LogFilePrefix={testProject.Name}");
                DotCoverTasks.DotCoverCover(s => s
                    .SetTargetExecutable(dotnetPath)
                    .SetOutputFile(projectCoverageDirectory / $"{testProject.Name}.snapshot")
                    .SetTargetWorkingDirectory(Solution.Directory)
                    .AddFilters("-:module=vstest.console")
                    .SetTargetArguments(
                        $"test {testProject} {testLoggers.Select(x => $"-l {x}").Join(' ')} --results-directory {TestResultsDirectory}")
                    //Disable config file watching - the tests start many instances of the web host
                    .SetProcessEnvironmentVariable("ASPNETCORE_hostBuilder__reloadConfigOnChange", "false")
                    //Don't log each request when running the tests
                    .SetProcessEnvironmentVariable("Logging__LogLevel__HttpLoggingMiddlewareOverride", "Warning")
                );

                var projectSnapshots = projectCoverageDirectory.GlobFiles("*.snapshot")
                    .Select(x => x.ToString())
                    .Aggregate((a, n) => a + ";" + n);

                //merge all the per-project coverage snapshots into one snapshot
                DotCoverTasks.DotCoverMerge(s => s
                    .SetSource(projectSnapshots)
                    .SetOutputFile(CoverageResultsDirectory / "coverage.snapshot")
                );

                coverageResultFile = CoverageResultsDirectory / "coverage.xml";
                coverageReportFile = CoverageResultsDirectory / "coverage.html";
                //generate the report
                DotCoverTasks.DotCoverReport(s => s
                    .SetReportType($"{DotCoverReportType.Html},{DotCoverReportType.DetailedXml}")
                    .SetSource(CoverageResultsDirectory / "coverage.snapshot")
                    .SetOutputFile($"{coverageReportFile};{coverageResultFile}"));
            }

            ReportSummary(s =>
            {
                // var r = TestResultSummaries.GetTestResultSummaryCounters(TestResultsDirectory);
                // if (r.HasResults)
                // {
                //     s.Add("Tests T/P/F", $"{r.TotalTests.Value}/{r.Passed.Value}/{r.Failed.Value}");
                // }

                if (coverageResultFile != null)
                {
                    var c = TestResultSummaries.GetTestCoverageSummary(coverageResultFile, testProjects);
                    if (c.HasCoverage)
                    {
                        s.Add("Coverage", $"{c.CoveredStatements}/{c.TotalStatements}/{c.CoveragePercent}%");
                    }
                }

                return s;
            });
        });

    Target DockerBuild => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DockerTasks.DockerBuild(s => new MyDockerBuildSettings()
                .SetTag(DockerFullImageName)
                .SetFile(Solution.Directory / "Dockerfile")
                .SetProgress("plain")
                .SetPath(Solution.Directory));
        });

    Target DockerRun => _ => _
        .DependsOn(DockerBuild)
        .Unlisted()
        .Executes(() =>
        {
            DockerTasks.DockerRun(s => s
                .SetPublish($"{hostPort}:{containerPort}")
                .EnableDetach()
                .SetImage(DockerFullImageName));
        });

    Target DockerLog => _ => _
        .After(DockerRun)
        .Unlisted()
        .Executes(() =>
        {
            var containerId = GetContainerIds().First();
            DockerTasks.DockerLogs(s => s.SetContainer(containerId));
        });

    Target IntegrationTestNpmCi => _ => _
        .Unlisted()
        .Executes(() => NpmTasks.NpmCi(s => s.SetProcessWorkingDirectory(IntegrationTestsDirectory)));

    Target IntegrationTest => _ => _
        .DependsOn(IntegrationTestNpmCi)
        .DependsOn(DockerRun)
        .DependsOn(DockerLog)
        .Executes(() =>
        {
            NpmTasks.NpmRun(s => s
                .SetArguments("test")
                .SetProcessWorkingDirectory(IntegrationTestsDirectory)
                .SetProcessEnvironmentVariable("NODE_ENV", "ci"));
        });

    Target DockerStop => _ => _
        .TriggeredBy(IntegrationTest)
        .AssuredAfterFailure()
        .Unlisted()
        .Executes(() =>
        {
            var containerId = GetContainerIds().First();
            DockerTasks.DockerStop(s => s.AddContainers(containerId));

            if (!noCleanup)
            {
                DockerTasks.DockerContainerRm(s => s.AddContainers(containerId));
            }
        });

    Target DockerLogin => _ => _
        .Unlisted()
        .OnlyWhenDynamic(() => IsRunningAsGitHubAction())
        .Executes(() =>
        {
            DockerTasks.DockerLogin(s => new MyDockerLoginSettings()
                .SetServer(dockerRepository)
                .SetUsername(GitHubActions?.Actor)
                .SetPassword(GitHubActions?.Token));
        });

    bool IsRunningAsGitHubAction() => GitHubActions is { Actor: { }, Token: { } };

    Target DockerPush => _ => _
        .DependsOn(IntegrationTest, DockerLogin)
        .After(DockerStop)
        .OnlyWhenDynamic(() => GitVersion.BranchName.Equals("main") || GitVersion.BranchName.Equals("origin/main"))
        .Executes(() =>
        {
            var targetImageName = $"{DockerFullImageName}:{GitVersion.FullSemVer}";
            DockerTasks.DockerImageTag(s => s
                .SetSourceImage($"{DockerFullImageName}:latest")
                .SetTargetImage(targetImageName)
            );
            DockerTasks.DockerPush(s => s
                .SetName(targetImageName));
        });

    IEnumerable<string> GetContainerIds(bool failIfNotFound = true)
    {
        var outputs = DockerTasks.DockerPs(s => s
            .EnableQuiet()
            .SetFilter($"ancestor={DockerFullImageName}"));
        var containerIds = outputs
            .Where(s => s.Type == OutputType.Std)
            .Select(x => x.Text)
            .ToList();
        if (!containerIds.Any() && failIfNotFound)
        {
            Assert.Fail($"Could not find a running container for image '{DockerFullImageName}'");
        }

        return containerIds;
    }
}