using System.Reflection;
using RnRomManager.Core.Library;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Forwards every call; runs <c>beforeGet</c> first on each <see cref="IGameRepository.GetAsync"/>.</summary>
public class LockingGames : DispatchProxy
{
    private IGameRepository _inner = null!;
    private Action _beforeGet = null!;

    public static IGameRepository Wrap(IGameRepository inner, Action beforeGet)
    {
        var proxy = Create<IGameRepository, LockingGames>();
        ((LockingGames)(object)proxy)._inner = inner;
        ((LockingGames)(object)proxy)._beforeGet = beforeGet;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IGameRepository.GetAsync))
            _beforeGet();
        try
        {
            return method.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }
}
