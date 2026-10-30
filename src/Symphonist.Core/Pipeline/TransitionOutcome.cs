using Symphonist.Core.Risk;
using Symphonist.Core.Storage;

namespace Symphonist.Core.Pipeline;

/// <param name="Decision">The gate decision this transition made (approve or send back from a waiting gate), if any.</param>
/// <param name="Touch">How the developer stepped in with this transition (a send-back or an unblock), if they did.</param>
public sealed record TransitionOutcome(bool Changed, string? SourceStatusToSet, string Message, GateDecisionInfo? Decision = null, TouchKind? Touch = null);

/// <summary>A decision on the gate round identified by (<paramref name="StageId"/>, <paramref name="WaitingSince"/>).</summary>
public sealed record GateDecisionInfo(string StageId, DateTimeOffset WaitingSince, GateDecision Decision, bool Auto);
