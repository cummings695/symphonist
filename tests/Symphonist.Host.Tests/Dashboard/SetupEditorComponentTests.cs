using Bunit;
using Symphonist.Core.Config;
using Symphonist.Core.Onboarding;
using Symphonist.Host.Components.Shared;

namespace Symphonist.Host.Tests.Dashboard;

/// <summary>Posted values the editors did not render (tampered or stale) must not break the circuit.</summary>
public sealed class SetupEditorComponentTests : IDisposable
{
    private readonly BunitContext ctx = new();

    public void Dispose() => ctx.Dispose();

    private static List<StageForm> Stages() =>
    [
        new() { Id = "plan", Kind = StageKind.Plan, Gate = GatePolicy.Human, EntryStatuses = ["To Do", "Backlog"] },
        new() { Id = "implement", Kind = StageKind.Implement, Gate = GatePolicy.Human },
        new() { Id = "done", Kind = StageKind.Done },
    ];

    [Theory]
    [InlineData("Bogus")]
    [InlineData("99")]
    [InlineData("-")]
    [InlineData("")]
    public void Invalid_kind_or_gate_is_ignored(string posted)
    {
        var stages = Stages();
        var cut = ctx.Render<MappingTable>(p => p.Add(c => c.Stages, stages));

        cut.Find("#stage-0-kind").Change(posted);
        cut.Find("#stage-0-gate").Change(posted);

        Assert.Equal(StageKind.Plan, stages[0].Kind);
        Assert.Equal(GatePolicy.Human, stages[0].Gate);
        cut.Find("#stage-0-gate").Change("AutoWhenLowRisk");
        Assert.Equal(GatePolicy.AutoWhenLowRisk, stages[0].Gate);
    }

    [Theory]
    [InlineData("button[aria-label='Remove stage done']")]
    [InlineData("button[aria-label='Move stage done up']")]
    public void Stale_stage_remove_or_move_does_not_throw(string button)
    {
        var stages = Stages();
        var cut = ctx.Render<MappingTable>(p => p.Add(c => c.Stages, stages));
        var stale = cut.Find(button);
        // The list changes before the posted click arrives (e.g. a double click on Remove).
        stages.RemoveAt(2);

        stale.Click();

        Assert.Equal(["plan", "implement"], stages.Select(s => s.Id));
    }

    [Fact]
    public void Stale_entry_status_edit_does_not_throw()
    {
        var stages = Stages();
        var cut = ctx.Render<MappingTable>(p => p.Add(c => c.Stages, stages));
        var secondEntry = cut.Find("#stage-0-entry-1");
        stages[0].EntryStatuses.RemoveAt(1);

        secondEntry.Change("");

        Assert.Equal(["To Do"], stages[0].EntryStatuses);
    }

    [Fact]
    public void Stale_risk_area_remove_does_not_throw()
    {
        var areas = new List<RiskAreaForm> { new() { Name = "pricing", Description = "d" }, new() { Name = "auth", Description = "d" } };
        var cut = ctx.Render<RiskAreaEditor>(p => p.Add(c => c.Areas, areas));
        var removeAuth = cut.Find("button[aria-label='Remove risk area auth']");
        areas.RemoveAt(1);

        removeAuth.Click();

        Assert.Equal(["pricing"], areas.Select(a => a.Name));
    }
}
