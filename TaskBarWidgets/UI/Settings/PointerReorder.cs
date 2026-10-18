using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private readonly TextBlock _ghostText;
    private readonly Border _insertion;
    private readonly Queue<string> _entries = new();
    private Gesture? _gesture;
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
    public PointerReorder(FrameworkElement root, Canvas overlay, Action resetFeedback)
    {
        _root = root; _overlay = overlay; _resetFeedback = resetFeedback;
        _ghostText = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 };
        _ghost = UiThemeStyles.Apply(new Border { Child = _ghostText, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(6), Opacity = .94, IsHitTestVisible = false, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1) }, "SettingsDragGhost");
        _insertion = UiThemeStyles.Apply(new Border { IsHitTestVisible = false, Visibility = Visibility.Collapsed }, "SettingsInsertion");
        overlay.Children.Add(_insertion); overlay.Children.Add(_ghost);
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
    public bool Begin(Button source, PointerRoutedEventArgs args, Action? tap, Action<Point> move, Action<Point> drop)
    {
        if (_gesture is not null || !args.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return false;
        var gesture = new Gesture(++_sequence, source, args.Pointer, args.GetCurrentPoint(_root).Position, tap, move, drop);
        if (!_root.CapturePointer(args.Pointer)) { Record("capture-failed", $"pointer={args.Pointer.PointerId} contact={args.GetCurrentPoint(_root).IsInContact} loaded={_root.IsLoaded}"); return false; }
        _gesture = gesture;
        _ghostText.Text = source is PointerDragButton button ? button.DragLabel : source.Content?.ToString() ?? "移动";
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
        gesture.Source.Opacity = .45; _ghost.Visibility = Visibility.Visible;
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
        Canvas.SetLeft(_insertion, bounds.X); Canvas.SetTop(_insertion, bounds.Y); _insertion.Visibility = Visibility.Visible;
    }
    public void HideInsertion() => _insertion.Visibility = Visibility.Collapsed;
    public void Cancel(string reason = "page-or-operation-change")
    {
        if (_gesture is not null) Record("cancel", "reason=" + reason);
        Reset();
    }
    private void Reset()
    {
        var gesture = _gesture; _gesture = null;
        _scrollTick.Stop(); _ghost.Visibility = _insertion.Visibility = Visibility.Collapsed;
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
