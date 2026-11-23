using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// What the sweep says about the layout of <c>--worktree</c> (lean-worker#139) before it looks at any repository.
/// The sweeps over real worktrees of that layout are in <c>WorktreeSweepTests.Layout.cs</c>.
/// </summary>
[Collection("launcher-process-state")]
public sealed class WorktreeSweepLayoutTests
{
    [Fact]
    public async Task The_help_names_both_built_in_protected_foldersAsync()
    {
        TextWriter oldOut = Console.Out;
        await using StringWriter captured = new();
        Console.SetOut(captured);
        try
        {
            Assert.Equal(0, await WorktreeSweep.RunAsync(["--help"]));
        }
        finally
        {
            Console.SetOut(oldOut);
        }

        Assert.Contains("built in: *-pinned, pinned", captured.ToString(), StringComparison.Ordinal);
    }
}
