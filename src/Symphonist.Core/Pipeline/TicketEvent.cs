namespace Symphonist.Core.Pipeline;

public sealed class TicketEvent
{
    public long Id { get; set; }
    public Guid TicketId { get; set; }
    public DateTimeOffset At { get; set; }
    public string StageId { get; set; } = "";
    public string Message { get; set; } = "";
}
