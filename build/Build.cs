using System;
using System.Linq;
using System.Runtime.Serialization;
using Nuke.Common;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotCover;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;
    [GitVersion] readonly GitVersion GitVersion;
    [GitRepository] readonly GitRepository GitRepository;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath CoverageDirectory => TestsDirectory / "coverage";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj", "**/TestResults").ForEach(DeleteDirectory);
            DeleteDirectory(CoverageDirectory);
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
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetLoggers("trx;LogFilePrefix=TestResults", "html;LogFilePrefix=TestResults")
                .SetProjectFile(Solution));
        });

    Target Cover => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var dotnetPath = ToolPathResolver.GetPathExecutable("dotnet");
            var testProjects = TestsDirectory.GlobFiles("**/*.csproj").ToList();
            var projectCoverageDirectory = CoverageDirectory / "projects";
            foreach (var testProject in testProjects)
            {
                var testProjectDirectory = testProject.Parent;
                //generate coverage snapshots for each test project
                DotCoverTasks.DotCoverCover(s => s
                    .SetTargetExecutable(dotnetPath)
                    .SetOutputFile(projectCoverageDirectory / $"{testProjectDirectory!.Name}.snapshot")
                    .SetTargetWorkingDirectory(Solution.Directory)
                    .SetTargetArguments("test " + testProject)
                );
            }

            var projectSnapshots = projectCoverageDirectory.GlobFiles("*.snapshot")
                .Select(x => x.ToString())
                .Aggregate((a, n) => a + ";" + n);

            //merge all the per-project coverage snapshots into one snapshot
            DotCoverTasks.DotCoverMerge(s => s
                .SetSource(projectSnapshots)
                .SetOutputFile(CoverageDirectory / "coverage.snapshot")
            );

            //generate the report
            DotCoverTasks.DotCoverReport(s => s
                .SetReportType($"{DotCoverReportType.Html},{DotCoverReportType.DetailedXml}")
                .SetSource(CoverageDirectory / "coverage.snapshot")
                .SetOutputFile($"{CoverageDirectory / "coverage.html"};{CoverageDirectory / "coverage.xml"}"));
        });

    Target DockerBuild => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DockerTasks.DockerBuild(s => new MyDockerBuildSettings()
                .SetTag("httpstatus")
                .SetFile(Solution.Directory / "Dockerfile")
                .SetProgress("plain")
                .SetPath(Solution.Directory));
        });
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
}
