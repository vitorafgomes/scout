using Xunit;

namespace Scout.Host.Console.Tests;

/// <summary>
/// Runs the tests that set and remove process environment variables alone, because
/// every host composition in another test removes the worker gate token from it.
/// </summary>
[CollectionDefinition("process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}
