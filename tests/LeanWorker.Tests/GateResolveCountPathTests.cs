using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class GateResolveCountPathTests
{
    private static JsonNode Root(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Default_path_walks_an_array_index_then_a_property()
    {
        JsonNode root = Root("""{"runs":[{"results":[1,2,3]}]}""");
        JsonNode? node = Gate.ResolveCountPath(root, "runs[0].results");
        _ = Assert.IsType<JsonArray>(node);
        Assert.Equal(3, ((JsonArray)node!).Count);
    }

    [Fact]
    public void Nested_objects_resolve()
    {
        JsonNode root = Root("""{"a":{"b":{"c":[1,2]}}}""");
        JsonNode? node = Gate.ResolveCountPath(root, "a.b.c");
        _ = Assert.IsType<JsonArray>(node);
    }

    [Fact]
    public void Out_of_range_index_returns_null() =>
        Assert.Null(Gate.ResolveCountPath(Root("""{"runs":[{"results":[1]}]}"""), "runs[5].results"));

    [Fact]
    public void Missing_property_returns_null() =>
        Assert.Null(Gate.ResolveCountPath(Root("""{"runs":[{}]}"""), "runs[0].results"));

    [Fact]
    public void Non_object_in_the_middle_returns_null() =>
        Assert.Null(Gate.ResolveCountPath(Root("""{"runs":"not an object"}"""), "runs.results"));
}
