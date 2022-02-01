using CliWrap;

namespace Utils;

public static class CliWrapExtensions
{
    public static Command WithPipeToConsole(this Command cmd)
    {
        var stdout = Console.OpenStandardOutput();
        var stderr = Console.OpenStandardError();

        return cmd | (stdout, stderr);
    }
}
