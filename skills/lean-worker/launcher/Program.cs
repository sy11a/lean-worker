// LeanWorker: runs one well-scoped task in a separate, minimal-context worker process (Claude Code `claude -p`
// or opencode `opencode run`) and prints a compact report with token usage and cost.
//
// Settings resolve in this order: command-line option > profile in <runs-root>/profiles.json > built-in default.
// Project notes (<runs-root>/project.md) are given to every worker unless --no-project-notes;
// a per-task --system file is appended after them.
//
// Spend is metered live from the worker's stream with the price book (prices.json). Past the wrap-up share of the
// budget a pre-tool hook blocks every tool call, so the worker's last message is a handoff; at the budget the
// launcher stops the worker.
//
// Exit codes: 0 = worker finished without error (or a gate chain ended clean), 1 = worker reported an error,
// 2 = launcher failed, 3 = worker wrapped up near its budget and left a handoff (continue with
// --continue-from <run-dir>), 4 = a gate chain ended stuck, 5 = a gate chain ended in an error.

namespace LeanWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "hook" => Commands.Hook(args[1..]),
                "quota" => Commands.Quota(args[1..]),
                "cost" => Commands.Cost(args[1..]),
                "stats" => Commands.Stats(args[1..]),
                "prices" => Commands.Prices(args[1..]),
                _ => await Launcher.RunAsync(Options.Parse(args)).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is LaunchException or System.Text.Json.JsonException or FormatException or OverflowException
                                       or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            await Console.Out.WriteLineAsync($"LEAN-WORKER LAUNCH FAILED: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
    }
}