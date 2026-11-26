using System.Reflection;
using RnRomManager.Core.Ingest;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>A review repository whose first call of one method fails, as a DB outage at that moment would.</summary>
public class FailFirstResolution : DispatchProxy
{
    public IIngestReviewRepository Inner { get; set; } = null!;
    public string Method { get; set; } = nameof(IIngestReviewRepository.SetResolutionAsync);
    private bool _failed;

    public static IIngestReviewRepository Wrap(IIngestReviewRepository inner, string method = nameof(IIngestReviewRepository.SetResolutionAsync))
    {
        var proxy = Create<IIngestReviewRepository, FailFirstResolution>();
        ((FailFirstResolution)(object)proxy).Inner = inner;
        ((FailFirstResolution)(object)proxy).Method = method;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == Method && !_failed)
        {
            _failed = true;
            return Task.FromException(new InvalidOperationException("db down"));
        }
        return method.Invoke(Inner, args);
    }
}
