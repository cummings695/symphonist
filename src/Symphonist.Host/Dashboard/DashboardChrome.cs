namespace Symphonist.Host.Dashboard;

/// <summary>Who reported a status-line error; each source owns one error slot.</summary>
public enum StatusSource { Shell, Board, Ticket }

/// <summary>One per circuit: what pages tell the frame (status line, tab title, highlighted project). Used only on the circuit's sync context, so it is not thread-safe.</summary>
public sealed class DashboardChrome
{
    private string? commandMessage;
    private string? shellError;
    private string? boardError;
    private string? ticketError;

    /// <summary>Raised after every state change.</summary>
    public event Action? Changed;

    /// <summary>Tab-title page label, e.g. the ticket key; null on the board.</summary>
    public string? PageLabel { get; private set; }

    /// <summary>Project id to highlight; null means All projects.</summary>
    public string? CurrentProject { get; private set; }

    /// <summary>The first error in Shell, Board, Ticket order, else the command message.</summary>
    public string? Message => shellError ?? boardError ?? ticketError ?? commandMessage;

    /// <summary><see cref="Message"/> is an error.</summary>
    public bool IsError => shellError is not null || boardError is not null || ticketError is not null;

    public void SetPage(string? label, string? project)
    {
        PageLabel = label;
        CurrentProject = project;
        Changed?.Invoke();
    }

    /// <summary>Sets the command message; the latest wins.</summary>
    public void ShowMessage(string message)
    {
        commandMessage = message;
        Changed?.Invoke();
    }

    /// <summary>Sets or, with null, clears the error of <paramref name="source"/> only.</summary>
    public void SetError(StatusSource source, string? error)
    {
        switch (source)
        {
            case StatusSource.Shell: shellError = error; break;
            case StatusSource.Board: boardError = error; break;
            case StatusSource.Ticket: ticketError = error; break;
            default: throw new ArgumentOutOfRangeException(nameof(source));
        }
        Changed?.Invoke();
    }

    /// <summary>Clears the command message and the Board and Ticket errors; Shell errors stay.</summary>
    public void ClearForNavigation()
    {
        commandMessage = null;
        boardError = null;
        ticketError = null;
        Changed?.Invoke();
    }
}
