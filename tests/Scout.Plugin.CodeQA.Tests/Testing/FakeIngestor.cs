using Scout.Plugin.CodeQA.Indexing;

namespace Scout.Plugin.CodeQA.Tests.Testing;

/// <summary>
/// Deterministic <see cref="IIngestor"/> for tests that need a real
/// ingestion pipeline without going through GitHub. Returns the exact
/// <see cref="RawFile"/> snapshot it was handed at construction time, in
/// the same order.
/// </summary>
internal sealed class FakeIngestor : IIngestor
{
    private readonly IReadOnlyList<RawFile> _files;

    public FakeIngestor(IEnumerable<RawFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files.ToArray();
    }

    public Task<IReadOnlyList<RawFile>> IngestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_files);
    }
}
