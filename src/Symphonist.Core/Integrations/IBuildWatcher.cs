namespace Symphonist.Core.Integrations;

public enum BuildStatus { NotFound, Running, Succeeded, Failed }

public interface IBuildWatcher
{
    Task<BuildStatus> GetStatusAsync(string commitHash, CancellationToken ct);
}
