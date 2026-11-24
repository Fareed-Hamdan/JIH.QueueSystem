using JIH.Queue.Core.Domain;

namespace JIH.Queue.Core.Rules;

/// <summary>
/// Pure ordering rules for a clinic queue. The queue order is defined only by SortKey;
/// these helpers compute where a ticket goes and renumber keys (10, 20, 30…).
///
///  * New ticket            → end of the queue.
///  * Priority ticket       → after the last waiting priority ticket (ahead of all normal tickets).
///  * Skipped ticket        → moved back <c>offset</c> positions (fewer if the queue is shorter → end).
///  * Reinstated / transfer → end of the queue (or the priority block if flagged).
/// </summary>
public static class QueueOrdering
{
    public const long Step = 10;

    public static List<QueueTicket> Ordered(IEnumerable<QueueTicket> waiting) =>
        waiting.Where(t => t.Status == TicketStatus.Waiting).OrderBy(t => t.SortKey).ThenBy(t => t.Id).ToList();

    /// <summary>Index at which a new ticket is inserted into the current ordered waiting list.</summary>
    public static int InsertIndexForNew(IReadOnlyList<QueueTicket> ordered, bool priority)
    {
        if (!priority) return ordered.Count;
        var lastPriority = -1;
        for (var i = 0; i < ordered.Count; i++) if (ordered[i].IsPriority) lastPriority = i;
        return lastPriority + 1;
    }

    /// <summary>Index for a skipped ticket: <paramref name="offset"/> patients will be called before it.</summary>
    public static int InsertIndexForSkip(IReadOnlyList<QueueTicket> ordered, int offset) =>
        Math.Min(Math.Max(0, offset), ordered.Count);

    /// <summary>Inserts <paramref name="ticket"/> at <paramref name="index"/> and renumbers SortKeys of the whole queue.</summary>
    public static void InsertAndRenumber(List<QueueTicket> ordered, QueueTicket ticket, int index)
    {
        ordered.Remove(ticket);
        ordered.Insert(Math.Clamp(index, 0, ordered.Count), ticket);
        Renumber(ordered);
    }

    public static void Renumber(IList<QueueTicket> ordered)
    {
        for (var i = 0; i < ordered.Count; i++) ordered[i].SortKey = (i + 1) * Step;
    }

    /// <summary>Number of patients ahead of the ticket (0 = next). -1 if not waiting.</summary>
    public static int PeopleAhead(IReadOnlyList<QueueTicket> ordered, int ticketId)
    {
        for (var i = 0; i < ordered.Count; i++) if (ordered[i].Id == ticketId) return i;
        return -1;
    }

    public static string FormatNumber(string clinicCode, int sequence) => $"{clinicCode}-{sequence:000}";
}
