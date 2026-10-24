using Symphonist.Core.Agents;

namespace Symphonist.Core.Tests.Agents;

public class AgentRunnersTests
{
    private sealed class StubRunner : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    [Fact]
    public void Known_agent_type_resolves_its_runner()
    {
        var claude = new StubRunner();
        var runners = new AgentRunners(new Dictionary<string, IAgentRunner> { ["claude-code"] = claude });
        Assert.Same(claude, runners.For("claude-code"));
    }

    [Fact]
    public void Unknown_agent_type_is_reported()
    {
        var runners = new AgentRunners(new Dictionary<string, IAgentRunner> { ["claude-code"] = new StubRunner() });
        var ex = Assert.Throws<NotSupportedException>(() => runners.For("codex"));
        Assert.Equal("no agent runner for 'codex'", ex.Message);
    }
}
