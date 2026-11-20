using AiAssistant.Core.Domain;

namespace AiAssistant.Summarization;

public sealed record ActivitySegment(
    int Index,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<ActivityEvent> Events,
    IReadOnlyDictionary<string, int> AppCounts)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>Splits a day into meaningful work segments using time gaps and a bounded event count.</summary>
public sealed class ActivitySegmenter(
    TimeSpan? inactivityGap = null,
    int maxEventsPerSegment = 80)
{
    private readonly TimeSpan _gap = inactivityGap ?? TimeSpan.FromMinutes(12);

    public IReadOnlyList<ActivitySegment> Segment(IReadOnlyList<ActivityEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return [];
        }

        var ordered = events
            .Where(item => item.Source != SourceKind.SystemControl)
            .OrderBy(item => item.Timestamp.OccurredAtUtc)
            .ToArray();
        var segments = new List<ActivitySegment>();
        var current = new List<ActivityEvent>();

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            var start = current[0].Timestamp.OccurredAtUtc;
            var end = current[^1].EndedAtUtc ?? current[^1].Timestamp.OccurredAtUtc;
            if (end <= start)
            {
                end = start.AddSeconds(30);
            }

            var apps = current
                .GroupBy(item => item.AppName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            segments.Add(new ActivitySegment(segments.Count, start, end, current.ToArray(), apps));
            current = [];
        }

        foreach (var item in ordered)
        {
            if (current.Count > 0)
            {
                var previous = current[^1].Timestamp.OccurredAtUtc;
                if (item.Timestamp.OccurredAtUtc - previous > _gap || current.Count >= maxEventsPerSegment)
                {
                    Flush();
                }
            }

            current.Add(item);
        }

        Flush();
        return segments;
    }
}
