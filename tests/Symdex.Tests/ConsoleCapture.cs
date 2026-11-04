namespace Symdex.Tests;

internal static class ConsoleCapture
{
    private static readonly object Gate = new();

    public static (string Stdout, string Stderr, int ExitCode) Run(Func<int> action)
    {
        lock (Gate)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var previousOut = Console.Out;
            var previousError = Console.Error;
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var code = action();
                return (stdout.ToString(), stderr.ToString(), code);
            }
            finally
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
        }
    }
}
