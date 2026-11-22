using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Reads and checks the status.json of a finished run dir.
/// </summary>
internal static class StatusFileAssert
{
    public static JsonObject Read(string runDir) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(runDir, "status.json")))!.AsObject();

    /// <summary>
    /// The run ended through the normal path: phase done, the end status the summary's, seq at least 1.
    /// </summary>
    public static void Done(string runDir, JsonObject summary)
    {
        JsonObject status = Read(runDir);
        Assert.Equal("lean-worker.status/1", status["schema"]!.GetValue<string>());
        Assert.Equal("done", status["phase"]!.GetValue<string>());
        Assert.Equal(summary["status"]!.GetValue<string>(), status["end"]!["status"]!.GetValue<string>());
        Assert.True(status["seq"]!.GetValue<long>() >= 1);
    }
}
