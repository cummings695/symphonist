using Symphonist.Core.Config;

namespace Symphonist.Core.Integrations;

public sealed record ProjectIntegrations(ITracker Tracker, ISourceControl SourceControl, IBuildWatcher BuildWatcher);

public interface IIntegrationFactory
{
    ProjectIntegrations Create(ProjectConfig project);
}
