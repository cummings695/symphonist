using Symphonist.Core.Config;
using Symphonist.Core.Integrations;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeIntegrationFactory(ProjectIntegrations integrations) : IIntegrationFactory
{
    public int Created { get; private set; }

    /// <summary>Thrown from Create when set (e.g. a missing secret).</summary>
    public Exception? Error { get; set; }

    public ProjectIntegrations Create(ProjectConfig project)
    {
        if (Error is not null) throw Error;
        Created++;
        return integrations;
    }
}
