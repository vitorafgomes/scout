using Xunit;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>
/// Serialises the UDS host tests. Each boots a Kestrel host on its own temp
/// socket; running them sequentially keeps resource pressure and teardown
/// timing deterministic.
/// </summary>
[CollectionDefinition("uds")]
public sealed class UdsCollection
{
}
