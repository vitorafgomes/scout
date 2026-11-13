using System.Reflection;

namespace Scout.ControlPlane.Tests.Integration;

/// <summary>Builds a wrapper over a real service that fails the calls a fault names; every other call reaches the real one.</summary>
internal static class FailingCalls
{
    /// <summary>A <typeparamref name="T"/> that forwards to <paramref name="inner"/> except where <paramref name="fault"/> answers an exception.</summary>
    public static T Wrap<T>(T inner, Func<MethodInfo, object?[]?, Exception?> fault)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, FailingCallsProxy<T>>();
        ((FailingCallsProxy<T>)(object)proxy).Configure(inner, fault);
        return proxy;
    }
}

/// <summary>The proxy behind <see cref="FailingCalls"/>; public and unsealed because the runtime derives the proxy type from it.</summary>
public class FailingCallsProxy<T> : DispatchProxy
    where T : class
{
    private T? _inner;
    private Func<MethodInfo, object?[]?, Exception?> _fault = (_, _) => null;

    internal void Configure(T inner, Func<MethodInfo, object?[]?, Exception?> fault)
    {
        _inner = inner;
        _fault = fault;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (_fault(targetMethod, args) is not { } failure)
        {
            return targetMethod.Invoke(_inner, args);
        }

        if (targetMethod.ReturnType == typeof(Task))
        {
            return Task.FromException(failure);
        }

        return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
            .MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0])
            .Invoke(null, [failure]);
    }
}
