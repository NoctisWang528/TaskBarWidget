using System.Threading.Channels;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace TaskBarWidgets.UI.Settings;

internal sealed class PointerDragButton : Button
{
    public string DragLabel { get; set; } = "";
    public Func<PointerDragButton, PointerRoutedEventArgs, bool>? StartPointerDrag { get; set; }
    protected override void OnPointerPressed(PointerRoutedEventArgs args)
    {
        if (StartPointerDrag?.Invoke(this, args) == true) args.Handled = true;
        else base.OnPointerPressed(args);
    }
}

internal sealed class PointerReorder
{
    private readonly FrameworkElement _root;
    private readonly Canvas _overlay;
    private readonly Action _resetFeedback;
    private readonly DispatcherQueueTimer _scrollTick;
    private readonly Border _ghost;
    private readonly ContentPresenter _ghostContent;
    private readonly Border _insertion;
    private readonly Queue<string> _entries = new();
    private readonly HashSet<FrameworkElement> _previewRows = [];
    private readonly HashSet<FrameworkElement> _touchedRows = [];
    private readonly Dictionary<FrameworkElement, Vector3> _baseOffsets = [];
    private readonly bool _animationsEnabled;
    private Gesture? _gesture;
    private FrameworkElement? _previewSource;
    private double _sourceOpacity = 1;
    private long _sequence;
    private sealed class Gesture(long sequence, Button source, Pointer pointer, Point origin, Action? tap, Action<Point> move, Action<Point> drop)
    {
        public long Sequence { get; } = sequence;
        public Button Source { get; } = source;
        public Pointer Pointer { get; } = pointer;
        public Point Origin { get; } = origin;
        public Point LastPoint { get; set; } = origin;
        public Action? Tap { get; } = tap;
        public Action<Point> Move { get; } = move;
        public Action<Point> Drop { get; } = drop;
        public bool Started { get; set; }
        public bool AwaitingTerminal { get; set; }
        public long LastMoveLog { get; set; }
    }
    public PointerReorder(FrameworkElement root, Canvas overlay, Action resetFeedback, bool? animationsEnabled = null)
    {
        _root = root; _overlay = overlay; _resetFeedback = resetFeedback;
        _ghostContent = new ContentPresenter { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _ghost = UiThemeStyles.Apply(new Border
        {
            Child = _ghostContent,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Opacity = .96,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(1),
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 16)
        }, "SettingsDragGhost");
        _insertion = UiThemeStyles.Apply(new Border { IsHitTestVisible = false, Visibility = Visibility.Collapsed }, "SettingsInsertion");
        overlay.Children.Add(_insertion); overlay.Children.Add(_ghost);
        _animationsEnabled = animationsEnabled ?? new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        _scrollTick = root.DispatcherQueue.CreateTimer(); _scrollTick.Interval = TimeSpan.FromMilliseconds(40);
        _scrollTick.Tick += (_, _) => { if (_gesture is { Started: true, AwaitingTerminal: false } gesture) gesture.Move(gesture.LastPoint); };
        root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) => Move(args)), true);
        root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) => Release(args)), true);
        root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, args) => { if (Matches(args)) Cancel("pointer-canceled"); }), true);
        root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, args) => CaptureLost(args)), true);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) => { if (args.Key == VirtualKey.Escape && _gesture is not null) { Cancel("escape"); args.Handled = true; } }), true);
    }
    public bool IsActive => _gesture is not null;
    internal string[] Entries => _entries.ToArray();
    public void Record(string stage, string detail = "")
    {
        var entry = $"{DateTimeOffset.Now:o} gesture={_gesture?.Sequence ?? _sequence} {stage} {detail}";
        _entries.Enqueue(entry); while (_entries.Count > 128) _entries.Dequeue();
        PointerGestureLog.Write(entry);
    }
    public bool Begin(Button source, PointerRoutedEventArgs args, Action? tap, Action<Point> move, Action<Point> drop,
        FrameworkElement? sourceRow = null, FrameworkElement? ghostContent = null)
    {
        if (_gesture is not null || !args.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return false;
        var gesture = new Gesture(++_sequence, source, args.Pointer, args.GetCurrentPoint(_root).Position, tap, move, drop);
        if (!_root.CapturePointer(args.Pointer)) { Record("capture-failed", $"pointer={args.Pointer.PointerId} contact={args.GetCurrentPoint(_root).IsInContact} loaded={_root.IsLoaded}"); return false; }
        _gesture = gesture;
        _ghostContent.Content = ghostContent ?? new TextBlock
        {
            Text = source is PointerDragButton button ? button.DragLabel : source.Content?.ToString() ?? "移动",
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 230
        };
        _previewSource = sourceRow;
        _sourceOpacity = sourceRow?.Opacity ?? 1;
        Record("press", $"pointer={args.Pointer.PointerId} source={source.Tag} x={gesture.Origin.X:F1} y={gesture.Origin.Y:F1}");
        source.Focus(FocusState.Pointer);
        return true;
    }
    private bool Matches(PointerRoutedEventArgs args) => _gesture?.Pointer.PointerId == args.Pointer.PointerId;
    private void Move(PointerRoutedEventArgs args)
    {
        if (!Matches(args)) return;
        var gesture = _gesture!;
        var sample = args.GetCurrentPoint(_root); gesture.LastPoint = sample.Position;
        if (!sample.Properties.IsLeftButtonPressed) { Complete(gesture, sample.Position, "move-button-up"); args.Handled = true; return; }
        if (gesture.AwaitingTerminal) return;
        if (!gesture.Started && Math.Pow(sample.Position.X - gesture.Origin.X, 2) + Math.Pow(sample.Position.Y - gesture.Origin.Y, 2) < 36) return;
        if (!gesture.Started) { gesture.Started = true; Record("drag-start"); _scrollTick.Start(); }
        if (_previewSource is not null) _previewSource.Opacity = .18; else gesture.Source.Opacity = .45;
        _ghost.Visibility = Visibility.Visible;
        Canvas.SetLeft(_ghost, Math.Clamp(sample.Position.X + 14, 0, Math.Max(0, _root.ActualWidth - Math.Max(120, _ghost.ActualWidth))));
        Canvas.SetTop(_ghost, Math.Clamp(sample.Position.Y + 14, 0, Math.Max(0, _root.ActualHeight - Math.Max(36, _ghost.ActualHeight))));
        if (Environment.TickCount64 - gesture.LastMoveLog > 150) { gesture.LastMoveLog = Environment.TickCount64; Record("move", $"x={sample.Position.X:F1} y={sample.Position.Y:F1}"); }
        gesture.Move(sample.Position); args.Handled = true;
    }
    private void Release(PointerRoutedEventArgs args)
    {
        if (!Matches(args)) return;
        Complete(_gesture!, args.GetCurrentPoint(_root).Position, "pointer-release"); args.Handled = true;
    }
    private void CaptureLost(PointerRoutedEventArgs args)
    {
        if (!Matches(args) || _root.PointerCaptures?.Any(pointer => pointer.PointerId == args.Pointer.PointerId) == true) return;
        var gesture = _gesture!; var sample = args.GetCurrentPoint(_root);
        Record("capture-lost", $"left={sample.Properties.IsLeftButtonPressed} started={gesture.Started}");
        if (sample.Properties.IsLeftButtonPressed) { Cancel("capture-stolen-while-pressed"); return; }
        gesture.AwaitingTerminal = true; _scrollTick.Stop();
        // WinUI may release capture before bubbling PointerReleased. Give that
        // event the first chance; identity makes the fallback exactly once.
        if (!_root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => {
            if (ReferenceEquals(_gesture, gesture)) Complete(gesture, sample.Position, "capture-loss-button-up");
        })) Cancel("dispatcher-unavailable");
    }
    private void Complete(Gesture gesture, Point point, string reason)
    {
        if (!ReferenceEquals(_gesture, gesture)) return;
        Record("release", $"reason={reason} x={point.X:F1} y={point.Y:F1}");
        Reset(); // Clear ownership before releasing capture or modifying the visual tree.
        if (gesture.Started) { Record("drop-dispatch"); gesture.Drop(point); }
        else {
            var origin = gesture.Source.TransformToVisual(_root).TransformPoint(new Point(0, 0));
            if (point.X >= origin.X && point.X <= origin.X + gesture.Source.ActualWidth && point.Y >= origin.Y && point.Y <= origin.Y + gesture.Source.ActualHeight) gesture.Tap?.Invoke();
        }
    }
    public void ShowInsertion(Rect bounds)
    {
        _insertion.Width = Math.Max(1, bounds.Width); _insertion.Height = Math.Max(1, bounds.Height);
        _insertion.CornerRadius = new CornerRadius(2);
        _insertion.Opacity = 1;
        Canvas.SetLeft(_insertion, bounds.X); Canvas.SetTop(_insertion, bounds.Y); _insertion.Visibility = Visibility.Visible;
    }
    public void ShowDropZone(Rect bounds)
    {
        _insertion.Width = Math.Max(1, bounds.Width);
        _insertion.Height = Math.Max(1, bounds.Height);
        _insertion.CornerRadius = new CornerRadius(6);
        _insertion.Opacity = .18;
        Canvas.SetLeft(_insertion, bounds.X);
        Canvas.SetTop(_insertion, bounds.Y);
        _insertion.Visibility = Visibility.Visible;
    }
    public void ShowVerticalGap(FrameworkElement anchor, bool after, double inset = 6)
    {
        if (anchor.ActualWidth <= 0 || anchor.ActualHeight <= 0) return;
        var origin = anchor.TransformToVisual(_root).TransformPoint(new Point(0, 0));
        ShowInsertion(new Rect(
            origin.X + inset,
            origin.Y + (after ? anchor.ActualHeight - 1 : -2),
            Math.Max(16, anchor.ActualWidth - inset * 2),
            3));
    }
    public void HideInsertion() => _insertion.Visibility = Visibility.Collapsed;
    internal Rect InsertionBounds => new(
        Canvas.GetLeft(_insertion),
        Canvas.GetTop(_insertion),
        _insertion.ActualWidth > 0 ? _insertion.ActualWidth : _insertion.Width,
        _insertion.ActualHeight > 0 ? _insertion.ActualHeight : _insertion.Height);
    internal Visibility InsertionVisibility => _insertion.Visibility;

    internal static FrameworkElement CreateDragGhost(string glyph, string title, string subtitle)
    {
        var row = new Grid { ColumnSpacing = 10, MinWidth = 180 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = string.IsNullOrEmpty(glyph) ? "\uE8D6" : glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 });
        labels.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Opacity = .68, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 });
        Grid.SetColumn(labels, 1); row.Children.Add(labels);
        return row;
    }

    // Same-lane reorder: gap uses the pre-removal list semantics.
    public void ShowRowPreview(
        IReadOnlyList<FrameworkElement> rows,
        int sourceIndex,
        int targetGap,
        double rowExtent,
        bool vertical = true)
    {
        if (rows.Count == 0 || sourceIndex < 0 || sourceIndex >= rows.Count) return;
        var offsets = new Dictionary<FrameworkElement, double>();
        if (sourceIndex < targetGap)
            for (var index = sourceIndex + 1; index < Math.Min(targetGap, rows.Count); index++) offsets[rows[index]] = -rowExtent;
        else
            for (var index = Math.Max(0, targetGap); index < sourceIndex; index++) offsets[rows[index]] = rowExtent;
        ApplyRowPreview(rows[sourceIndex], offsets, vertical);
    }

    // Cross-lane reorder: close the source slot and open a slot in the target lane.
    public void ShowCrossLanePreview(
        IReadOnlyList<FrameworkElement> sourceRows,
        int sourceIndex,
        IReadOnlyList<FrameworkElement> targetRows,
        int targetGap,
        double rowExtent,
        bool vertical = true)
    {
        if (sourceRows.Count == 0 || sourceIndex < 0 || sourceIndex >= sourceRows.Count) return;
        var offsets = new Dictionary<FrameworkElement, double>();
        for (var index = sourceIndex + 1; index < sourceRows.Count; index++) offsets[sourceRows[index]] = -rowExtent;
        for (var index = Math.Max(0, targetGap); index < targetRows.Count; index++) offsets[targetRows[index]] = rowExtent;
        ApplyRowPreview(sourceRows[sourceIndex], offsets, vertical);
    }

    private void ApplyRowPreview(FrameworkElement source, IReadOnlyDictionary<FrameworkElement, double> offsets, bool vertical)
    {
        if (!ReferenceEquals(_previewSource, source))
        {
            if (_previewSource is not null) _previewSource.Opacity = _sourceOpacity;
            _previewSource = source;
            _sourceOpacity = source.Opacity;
        }
        source.Opacity = .18;
        foreach (var stale in _previewRows.Where(row => !offsets.ContainsKey(row)).ToArray())
        {
            AnimateRowOffset(stale, 0, vertical);
            _previewRows.Remove(stale);
        }
        foreach (var (row, offset) in offsets)
        {
            _previewRows.Add(row);
            _touchedRows.Add(row);
            AnimateRowOffset(row, offset, vertical);
        }
    }

    private void AnimateRowOffset(FrameworkElement row, double offset, bool vertical)
    {
        var visual = ElementCompositionPreview.GetElementVisual(row);
        if (!_baseOffsets.TryGetValue(row, out var baseOffset))
        {
            baseOffset = visual.Offset;
            _baseOffsets[row] = baseOffset;
        }
        var target = baseOffset + (vertical ? new Vector3(0, (float)offset, 0) : new Vector3((float)offset, 0, 0));
        visual.StopAnimation(nameof(Visual.Offset));
        if (!_animationsEnabled)
        {
            visual.Offset = target;
            return;
        }
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(140);
        animation.InsertKeyFrame(0, visual.Offset);
        animation.InsertKeyFrame(1, target, visual.Compositor.CreateCubicBezierEasingFunction(
            new Vector2(.16f, 1), new Vector2(.3f, 1)));
        visual.StartAnimation(nameof(Visual.Offset), animation);
    }

    public void Cancel(string reason = "page-or-operation-change")
    {
        if (_gesture is not null) Record("cancel", "reason=" + reason);
        Reset();
    }
    private void Reset()
    {
        var gesture = _gesture; _gesture = null;
        _scrollTick.Stop(); _ghost.Visibility = _insertion.Visibility = Visibility.Collapsed;
        foreach (var row in _touchedRows) AnimateRowOffset(row, 0, true);
        _previewRows.Clear();
        _touchedRows.Clear();
        _baseOffsets.Clear();
        if (_previewSource is not null) _previewSource.Opacity = _sourceOpacity;
        _previewSource = null;
        _sourceOpacity = 1;
        _ghostContent.Content = null;
        if (gesture is not null) {
            gesture.Source.Opacity = 1;
            if (_root.PointerCaptures?.Any(pointer => pointer.PointerId == gesture.Pointer.PointerId) == true) _root.ReleasePointerCapture(gesture.Pointer);
        }
        _resetFeedback();
    }
}

internal static class PointerGestureLog
{
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    static PointerGestureLog() => _ = Task.Run(async () => {
        await foreach (var entry in Queue.Reader.ReadAllAsync()) {
            try {
                var path = Path.Combine(AppContext.BaseDirectory, "ui-gestures.log");
                if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024) File.WriteAllText(path, "");
                await File.AppendAllTextAsync(path, entry[..Math.Min(entry.Length, 512)] + "\n");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    });
    public static void Write(string entry) => Queue.Writer.TryWrite(entry);
}
