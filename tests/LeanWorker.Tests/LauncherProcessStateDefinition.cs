using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Tests below mutate PATH, Console.Out and the current directory: process-global state that must not
/// race with another test running in parallel.
/// </summary>
[CollectionDefinition("launcher-process-state", DisableParallelization = true)]
public sealed class LauncherProcessStateDefinition;
