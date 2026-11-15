namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 会话状态机。停止幂等，非法转换一律拒绝，避免界面连点把句柄/文件弄成半死状态。
/// 2026/10/24 12:40:43 20211
/// </summary>
public sealed class CaptureSessionStateMachine
{
    public CaptureSessionState State { get; private set; } = CaptureSessionState.Created;

    public bool IsTerminal => State is CaptureSessionState.Stopped or CaptureSessionState.Failed;

    public bool BeginProbe() => Move(CaptureSessionState.Created, CaptureSessionState.Probing);

    public bool MarkReady() => Move(CaptureSessionState.Probing, CaptureSessionState.Ready);

    public bool Start() => Move(CaptureSessionState.Ready, CaptureSessionState.Running);

    public bool Pause() => Move(CaptureSessionState.Running, CaptureSessionState.Paused);

    public bool Resume() => Move(CaptureSessionState.Paused, CaptureSessionState.Running);

    /// <summary>任何非终止状态都能停；已停止再停返回 false（幂等，调用方不必先判断）。</summary>
    public bool Stop()
    {
        if (IsTerminal) return false;
        State = CaptureSessionState.Stopped;
        return true;
    }

    /// <summary>失败也保留已有证据：只改状态，不清数据。</summary>
    public bool Fail()
    {
        if (State == CaptureSessionState.Stopped) return false;
        State = CaptureSessionState.Failed;
        return true;
    }

    private bool Move(CaptureSessionState from, CaptureSessionState to)
    {
        if (State != from) return false;
        State = to;
        return true;
    }
}
