namespace SemanticSourceCode.Cli;

public static class CliArguments
{
    public static string? GetValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index >= 0 && index + 1 < args.Length)
        {
            return args[index + 1];
        }
        return null;
    }

    public static bool HasFlag(string[] args, string flag)
    {
        return Array.IndexOf(args, flag) >= 0;
    }
}
