namespace Scout.Operator.Tests.Fakes;

/// <summary>An <see cref="IFaceMouth"/> that answers <see cref="Box"/> for every picture and records the pictures it was asked about.</summary>
internal sealed class FoundMouth(OperatorMouthBox? box = null) : IFaceMouth
{
    public OperatorMouthBox? Box { get; } = box;

    public List<string> Pictures { get; } = [];

    public bool Configured => true;

    public Task<bool> AvailableAsync(CancellationToken ct) => Task.FromResult(Box is not null);

    public Task<OperatorMouthBox?> FindAsync(string picturePath, CancellationToken ct)
    {
        Pictures.Add(Path.GetFileName(picturePath));
        return Task.FromResult(Box);
    }
}
