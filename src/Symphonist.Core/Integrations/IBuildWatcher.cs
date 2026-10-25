namespace Symphonist.Core.Integrations;

public enum BuildStatus { NotFound, Running, Succeeded, Failed }

/// <summary>Why a commit's build is watched: the build stage (main) or the deploy stage (production).</summary>
public enum BuildPurpose { Build, Deploy }

public interface IBuildWatcher
{
    Task<BuildStatus> GetStatusAsync(string commitHash, BuildPurpose purpose, CancellationToken ct);
}
