using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
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
using Serilog;
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
        description: "The name to give the docker image when its built - Default is 'httpstatus'",
        Name = "ImageName")]
    string dockerImageName = "httpstatus";

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
                .SetFileVersion(GitVersion.NuGetVersionV2)
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

            if (!enableCoverage)
            {
                foreach (var testProject in testProjects)
                {
                    var testLoggers = testLoggerTypes.Select(x => $"{x};LogFilePrefix={testProject.Name}");
                    DotNetTest(s => s
                        .SetLoggers(testLoggers)
                        .SetResultsDirectory(TestResultsDirectory)
                        .SetProjectFile(testProject));
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

                //generate the report
                DotCoverTasks.DotCoverReport(s => s
                    .SetReportType($"{DotCoverReportType.Html},{DotCoverReportType.DetailedXml}")
                    .SetSource(CoverageResultsDirectory / "coverage.snapshot")
                    .SetOutputFile(
                        $"{CoverageResultsDirectory / "coverage.html"};{CoverageResultsDirectory / "coverage.xml"}"));
            }

            ReportSummary(s =>
            {
                var (hasSummary, total, passed, failed) = GetTestResultSummaryCounters(TestResultsDirectory);
                if (hasSummary)
                {
                    s.Add("Tests T/P/F", $"{total}/{passed}/{failed}");
                }

                // s.Add("Coverage", "84.5%");
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
        .Executes(() =>
        {
            DockerTasks.DockerRun(s => s
                .SetPublish($"{hostPort}:{containerPort}")
                .EnableDetach()
                .SetImage(dockerImageName));
        });

    Target DockerLog => _ => _
        .After(DockerRun)
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
        .Executes(() =>
        {
            var containerId = GetContainerIds().First();
            DockerTasks.DockerStop(s => s.AddContainers(containerId));

            if (!noCleanup)
            {
                DockerTasks.DockerContainerRm(s => s.AddContainers(containerId));
            }
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

    //(bool, int, int, int) ... I'm so sorry 😥
    (bool, int, int, int) GetTestResultSummaryCounters(AbsolutePath testResultsLocation)
    {
        var testResultTrxs = testResultsLocation.GlobFiles("*.trx");
        var counters = testResultTrxs.Select(x =>
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.Load(x);
                return xmlDoc
                    .SelectNodes("//*[name()='TestRun']/*[name()='ResultSummary']/*[name()='Counters']/@*")
                    .AsEnumerable<XmlNode>()
                    .Where(x => x.NodeType == XmlNodeType.Attribute)
                    .ToDictionary(
                        x => ((XmlAttribute)x).Name,
                        x =>
                        {
                            var v = ((XmlAttribute)x).Value;
                            if (int.TryParse(v, out var i)) return (int?)i;
                            return null;
                        });
            })
            .ToList();
        
        var total = counters.Sum(x => x["total"]);
        var passed = counters.Sum(x => x["passed"]);
        var failed = counters.Sum(x => x["failed"]);
        if (total.HasValue && passed.HasValue && failed.HasValue)
        {
            return (true, total.Value, passed.Value, failed.Value);
        }
        return (false, 0, 0, 0);
    }
}

[Serializable]
public class MyDockerBuildSettings : DockerBuildSettings
{
    //docker buildkit sends _all_ output to stderr, not just errors. So provide a different ProcessCustomLogger
    //that can split the stderr to Debug, Warning, or Error
    public override Action<OutputType, string> ProcessCustomLogger => CustomLogger;

    internal static void CustomLogger(OutputType type, string output)
    {
        switch (type)
        {
            case OutputType.Std:
                Log.Debug(output);
                break;
            case OutputType.Err:
            {
                if (output.StartsWith("WARNING!"))
                    Log.Warning(output);
                else if (output.Contains("ERROR:"))
                    Log.Error(output);
                else Log.Debug(output);
                break;
            }
        }
    }

    public static void DoSomething()
    {
        var location = "C:/pd/HttpStatus/HttpStatus/tests/results/HttpStatusTests_net6.0_20220801152647.trx";
        var doc = new XmlDocument();
        doc.Load(location);
        var root = doc.DocumentElement;

        var queries = new[]
        {
            "//*[name()='TestRun']/*[name()='ResultSummary']/*[name()='Counters']/@*",
            "//*[name()='TestRun']/*[name()='ResultSummary']/*[name()='Counters']/@total",
            "//*[name()='TestRun']/*[name()='ResultSummary']/*[name()='Counters']/@passed",
            "//*[name()='TestRun']/*[name()='ResultSummary']/*[name()='Counters']/@failed",
        };

        foreach (var query in queries)
        {
            Console.Out.WriteLine($"Testing {query}");

            try
            {
                var nodes = root.SelectNodes(query);
                Console.Out.WriteLine($"Got nodes: {nodes?.Count}");
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }
    }
}