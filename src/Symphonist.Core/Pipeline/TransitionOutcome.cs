using Symphonist.Core.Risk;

namespace Symphonist.Core.Pipeline;

/// <param name="Decision">The gate decision this transition made (approve or send back from a waiting gate), if any.</param>
public sealed record TransitionOutcome(bool Changed, string? SourceStatusToSet, string Message, GateDecisionInfo? Decision = null);

/// <summary>A decision on the gate round identified by (<paramref name="StageId"/>, <paramref name="WaitingSince"/>).</summary>
public sealed record GateDecisionInfo(string StageId, DateTimeOffset WaitingSince, GateDecision Decision, bool Auto);
