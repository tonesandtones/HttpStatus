using System;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Serilog;

[Serializable]
class MyDockerLoginSettings : DockerLoginSettings
{
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
                {
                    //suppress warnings and errors about passing password using --password {password}
                    //instead of piping on stdin and --password-stdin
                    if (output.Contains("password")) 
                    {
                        Log.Debug(output);
                    }
                    else
                    {
                        Log.Warning(output);
                    }
                }
                else if (output.Contains("ERROR:"))
                    Log.Error(output);
                else Log.Debug(output);

                break;
            }
        }
    }
}