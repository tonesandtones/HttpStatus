using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.Git;
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
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution(GenerateProjects = true)] readonly Solution Solution;
    [GitVersion] readonly GitVersion GitVersion;
    [GitRepository] readonly GitRepository GitRepository;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath TestResultsDirectory => TestsDirectory / "results";
    AbsolutePath CoverageResultsDirectory => TestsDirectory / "coverage";
    AbsolutePath IntegrationTestsDirectory = RootDirectory / "integration" / "httpstatusintegrationtests";

    [Parameter(
        description: "The name to give the docker image when its built - Default is 'ghcr.io/tonesandtones/httpstatus'",
        Name = "ImageName")]
    string dockerImageName = "ghcr.io/tonesandtones/httpstatus";

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
        description: "Whether to generate coverage snapshots and reports when running test. Only applies to the " +
                     "Test target - Default is false",
        Name = "Cover")]
    bool enableCoverage = false;

    [Parameter(
        description: "Whether to _not_ docker rm the container that's started for integration tests when the tests " +
                     "have finished. Only applies to the IntegrationTest target - Default is false",
        Name = "NoCleanUp")]
    bool noCleanup = false;

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            DeleteDirectory(CoverageResultsDirectory);
            DeleteDirectory(TestResultsDirectory);
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
        .Produces(CoverageResultsDirectory)
        .Executes(() =>
        {
            var testLoggerTypes = new[] { "trx", "html" };

            var dotnetPath = ToolPathResolver.GetPathExecutable("dotnet");
            var projectCoverageDirectory = CoverageResultsDirectory / "projects";
            var testProjects = new[] { Solution.HttpStatusTests };

            AbsolutePath coverageResultFile = null;
            AbsolutePath coverageReportFile = null;
            if (!enableCoverage)
            {
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
                        .SetProcessEnvironmentVariable("Logging__LogLevel__Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware", "Warning")
                    );
                }
            }
            else
            {
                foreach (var testProject in testProjects)
                {
                    var testLoggers = testLoggerTypes.Select(x => $"{x};LogFilePrefix={testProject.Name}");
                    DotCoverTasks.DotCoverCover(s => s
                        .SetTargetExecutable(dotnetPath)
                        .SetOutputFile(projectCoverageDirectory / $"{testProject.Name}.snapshot")
                        .SetTargetWorkingDirectory(Solution.Directory)
                        .SetTargetArguments(
                            $"test {testProject} {testLoggers.Select(x => $"-l {x}").Join(' ')} -r {TestResultsDirectory}")
                    );
                }

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
                .SetTag(dockerImageName)
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
                .SetImage(dockerImageName));
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

    Target DockerPush => _ => _
        .DependsOn(IntegrationTest)
        .After(DockerStop)
        .Executes(() =>
        {
            var targetImageName = $"{dockerImageName}:{GitVersion.FullSemVer}";
            DockerTasks.DockerImageTag(s => s
                .SetSourceImage($"{dockerImageName}:latest")
                .SetTargetImage(targetImageName)
            );
            DockerTasks.DockerPush(s => s
                .SetName(targetImageName));
        });

    IEnumerable<string> GetContainerIds(bool failIfNotFound = true)
    {
        var outputs = DockerTasks.DockerPs(s => s
            .EnableQuiet()
            .SetFilter($"ancestor={dockerImageName}"));
        var containerIds = outputs
            .Where(s => s.Type == OutputType.Std)
            .Select(x => x.Text)
            .ToList();
        if (!containerIds.Any() && failIfNotFound)
        {
            Assert.Fail($"Could not find a running container for image '{dockerImageName}'");
        }

        return containerIds;
    }
}