namespace Symphonist.Core.Storage;

/// <summary>How the developer stepped in on a ticket.</summary>
public enum TouchKind { SendBack, Unblock }

/// <summary>The developer stepping in at <paramref name="StageId"/> (the stage the ticket left).</summary>
public sealed record TouchRecord(Guid TicketId, string ProjectId, string StageId, TouchKind Kind, DateTimeOffset At);
