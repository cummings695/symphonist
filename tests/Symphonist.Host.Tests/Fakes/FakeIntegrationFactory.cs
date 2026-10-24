using Symphonist.Core.Config;
using Symphonist.Core.Integrations;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeIntegrationFactory(ProjectIntegrations integrations) : IIntegrationFactory
{
    public int Created { get; private set; }

    /// <summary>Thrown from Create when set (e.g. a missing secret).</summary>
    public Exception? Error { get; set; }

    /// <summary>What Create returns; replace it to simulate a changed secret (new clients).</summary>
    public ProjectIntegrations Integrations { get; set; } = integrations;

    public ProjectIntegrations Create(ProjectConfig project)
    {
        if (Error is not null) throw Error;
        Created++;
        return Integrations;
    }
}
