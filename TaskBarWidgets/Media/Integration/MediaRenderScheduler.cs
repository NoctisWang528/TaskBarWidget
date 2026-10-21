namespace TaskBarWidgets.Media.Integration;

internal enum MediaWorkKind
{
    None,
    FullState,
    Presentation
}

internal readonly record struct MediaWorkPlan(
    MediaWorkKind Kind,
    long StateRequestGeneration,
    long PresentationRequestGeneration,
    bool PresentationRequested,
    bool ForceMetadataRefresh,
    DateTimeOffset DueAt);

internal sealed class MediaRenderScheduler
{
    internal static readonly TimeSpan PresentationInterval = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(2);

    private readonly Func<DateTimeOffset> _utcNow;
    private bool _taskbarVisible;
    private bool _cardVisible;
    private bool _isPlaying;
    private bool _presentationActive = true;
    private bool _stateRequested = true;
    private bool _presentationRequested;
    private bool _forceMetadataRefresh = true;
    private long _stateRequestGeneration = 1;
    private long _presentationRequestGeneration = 1;
    private DateTimeOffset _nextStateUtc;
    private DateTimeOffset _nextPresentationUtc;
    private bool _initialized;

    internal MediaRenderScheduler(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal bool TaskbarVisible => _taskbarVisible;
    internal bool CardVisible => _cardVisible;
    internal bool HasVisibleSurface => _taskbarVisible || _cardVisible;
    internal bool HasActivePresentation =>
        HasVisibleSurface && _isPlaying && _presentationActive;

    internal void Initialize()
    {
        var now = _utcNow();
        _initialized = true;
        _nextStateUtc = now + FallbackInterval;
        _nextPresentationUtc = now;
        _stateRequested = true;
        _presentationRequested = true;
        _forceMetadataRefresh = true;
    }

    internal void SetVisibility(bool taskbarVisible, bool cardVisible)
    {
        var now = _utcNow();
        var becameVisible = (!_taskbarVisible && taskbarVisible) ||
            (!_cardVisible && cardVisible);
        _taskbarVisible = taskbarVisible;
        _cardVisible = cardVisible;
        if (!becameVisible) return;

        RequestState();
        _presentationRequested = true;
        _presentationRequestGeneration++;
        _nextPresentationUtc = now;
    }

    internal void RequestState(bool full = false)
    {
        _stateRequested = true;
        _stateRequestGeneration++;
        _forceMetadataRefresh |= full;
        _nextPresentationUtc = _utcNow();
    }

    internal void RequestPresentation()
    {
        _presentationRequested = true;
        _presentationRequestGeneration++;
    }

    internal void SetPlaybackState(bool isPlaying)
    {
        _isPlaying = isPlaying;
    }

    internal void SetPresentationActive(bool active)
    {
        if (_presentationActive == active) return;
        _presentationActive = active;
        if (active)
        {
            RequestPresentation();
        }
        else
        {
            _presentationRequested = false;
        }
    }

    internal void DeferState(
        TimeSpan delay,
        long? completedStateGeneration = null)
    {
        var due = _utcNow() + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        if (!_initialized || due < _nextStateUtc) _nextStateUtc = due;
        if (completedStateGeneration is null ||
            _stateRequestGeneration == completedStateGeneration.Value)
        {
            _stateRequested = false;
        }
        _forceMetadataRefresh = true;
    }

    internal MediaWorkPlan Poll()
    {
        if (!_initialized) Initialize();
        var now = _utcNow();
        var stateDue = now >= _nextStateUtc;
        if (_stateRequested || stateDue)
        {
            return new MediaWorkPlan(
                MediaWorkKind.FullState,
                _stateRequestGeneration,
                _presentationRequestGeneration,
                _presentationRequested,
                _forceMetadataRefresh || stateDue,
                now);
        }

        if (HasActivePresentation &&
            (_presentationRequested || now >= _nextPresentationUtc))
        {
            return new MediaWorkPlan(
                MediaWorkKind.Presentation,
                _stateRequestGeneration,
                _presentationRequestGeneration,
                _presentationRequested,
                false,
                now);
        }

        return new MediaWorkPlan(
            MediaWorkKind.None,
            _stateRequestGeneration,
            _presentationRequestGeneration,
            false,
            false,
            now);
    }

    internal void MarkCompleted(MediaWorkPlan plan)
    {
        var now = _utcNow();
        switch (plan.Kind)
        {
            case MediaWorkKind.FullState:
                if (_stateRequestGeneration == plan.StateRequestGeneration)
                {
                    _stateRequested = false;
                    _forceMetadataRefresh = false;
                }
                _nextStateUtc = now + FallbackInterval;
                if (plan.PresentationRequested &&
                    _presentationRequestGeneration == plan.PresentationRequestGeneration)
                {
                    _presentationRequested = false;
                }
                _nextPresentationUtc = now + PresentationInterval;
                break;
            case MediaWorkKind.Presentation:
                if (_presentationRequestGeneration == plan.PresentationRequestGeneration)
                {
                    _presentationRequested = false;
                }
                _nextPresentationUtc = now + PresentationInterval;
                break;
        }
    }

    internal TimeSpan TimeUntilNext()
    {
        if (!_initialized) return TimeSpan.Zero;
        var now = _utcNow();
        if (_stateRequested) return TimeSpan.Zero;

        var next = _nextStateUtc - now;
        if (HasActivePresentation)
        {
            if (_presentationRequested) return TimeSpan.Zero;
            var presentation = _nextPresentationUtc - now;
            if (presentation < next) next = presentation;
        }

        return next <= TimeSpan.Zero ? TimeSpan.Zero : next;
    }
}
