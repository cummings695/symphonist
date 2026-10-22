namespace Symphonist.Core.Pipeline;

public sealed record TransitionOutcome(bool Changed, string? SourceStatusToSet, string Message);
