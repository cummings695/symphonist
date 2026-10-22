using Symphonist.Core.Config;
using Symphonist.Core.Integrations;

namespace Symphonist.Host.Tests.Fakes;

public sealed class FakeIntegrationFactory(ProjectIntegrations integrations) : IIntegrationFactory
{
    public int Created { get; private set; }

    public ProjectIntegrations Create(ProjectConfig project)
    {
        Created++;
        return integrations;
    }
}
