// The one argument loop: the launcher and the sweep read their options the same way.

namespace LeanWorker;

/// <summary>
/// The argument loop the launcher and the sweep share: every argument is handed to a handler that says
/// whether it knows the option, and the value that follows an option is read through the getter it gets.
/// </summary>
internal static class ArgReader
{
    /// <summary>
    /// Reads the arguments in order. <paramref name="apply"/> gets the argument and a getter for the value
    /// behind it; it returns false for an option it does not know, and the read stops with an unknown option.
    /// </summary>
    public static void Read(string[] args, Func<string, Func<string>, bool> apply)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new LaunchException($"{arg} needs a value");
            if (!apply(arg, () => Next()))
            {
                throw new LaunchException($"unknown option '{arg}' (see --help)");
            }
        }
    }
}
