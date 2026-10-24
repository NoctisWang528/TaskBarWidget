namespace TaskBarWidgets.Media.Integration;

public sealed record MediaSourceChoice(string Id, string Name);

public sealed record MediaControlState(
    string Title, string Artist, string Source,
    double PositionSeconds, double DurationSeconds, bool IsPlaying,
    bool CanPrevious, bool CanNext, bool CanPlayPause, bool CanSeek,
    IReadOnlyList<MediaSourceChoice>? Sources = null,
    string? SelectedSourceId = null);
