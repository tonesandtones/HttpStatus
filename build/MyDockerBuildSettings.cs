using System;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Serilog;

[Serializable]
public class MyDockerBuildSettings : DockerBuildSettings
{
    //docker buildkit sends _all_ output to stderr, not just errors. So provide a different ProcessCustomLogger
    //that can split the stderr to Debug, Warning, or Error
    public override Action<OutputType, string> ProcessLogger => CustomLogger;

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