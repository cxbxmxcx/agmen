namespace Agmen.Hosting;

/// <summary>A message waiting to be typed into a session's prompt, from another session or from Agmen itself.</summary>
public sealed class OutboxMessage
{
    public required string Text { get; init; }
    /// <summary>"parent", "child", "sibling", or "agmen" for Agmen's own notes about finished turns.</summary>
    public string Origin { get; init; } = "parent";
    /// <summary>Rechecked on the workspace thread before delivering a message scoped to a live relationship.</summary>
    public Func<bool>? CanDeliver { get; init; }
    /// <summary>The tab to tell when the turn this message starts is over; none means nobody is told.</summary>
    public Guid? ReportTo { get; init; }
    public bool ReportBack => ReportTo is not null;
    /// <summary>How long the session must have been idle first, so a scheduled message doesn't cut in on the user.</summary>
    public TimeSpan MinIdle { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Messages with the same key replace each other, e.g. one pending report per tab.</summary>
    public string? Key { get; init; }
    public DateTime QueuedAt { get; } = DateTime.Now;
}
