using System.Diagnostics;
using Kitsune.Core.Emulator;

namespace Kitsune.Core.Input;

/// <summary>What a macro plays against: touches, Android keys, and frames for checkpoints.</summary>
public interface IMacroTarget
{
    int GuestWidth { get; }
    int GuestHeight { get; }
    void Touch(int id, int x, int y, bool down);
    void AndroidKey(int evdevCode, bool down);
    Task<(byte[] Rgb, int Width, int Height)> GetFrameAsync(int width, int height, CancellationToken ct);
}

/// <summary>A running emulator as a macro target.</summary>
public sealed class GrpcMacroTarget : IMacroTarget
{
    private readonly EmulatorControl _control;
    public int GuestWidth { get; set; }
    public int GuestHeight { get; set; }
    public GrpcMacroTarget(EmulatorControl control, int guestWidth, int guestHeight)
    {
        _control = control; GuestWidth = guestWidth; GuestHeight = guestHeight;
    }
    public void Touch(int id, int x, int y, bool down) => _control.SendTouch(id, x, y, down);
    public void AndroidKey(int evdevCode, bool down) => _control.SendEvdevKey(evdevCode, down);
    public Task<(byte[] Rgb, int Width, int Height)> GetFrameAsync(int width, int height, CancellationToken ct) => _control.GetFrameAsync(width, height, 0, ct);
}

public sealed record MacroProgress(int Iteration, int StepIndex, int StepCount, string Message);

/// <summary>
/// Replays a macro with its recorded timing. Touch and key steps fire at their recorded offsets (scaled
/// by Speed); a WaitForScreen step blocks until the region matches its reference and then shifts the
/// rest of the timeline by the time it waited, which is what makes a replay survive lag and loading screens.
/// </summary>
public sealed class MacroPlayer
{
    private readonly IMacroTarget _target;
    public double Speed { get; set; } = 1.0;
    public int PollIntervalMs { get; set; } = 150;
    public event Action<MacroProgress>? Progress;

    public MacroPlayer(IMacroTarget target) => _target = target;

    /// <summary>Runs the macro <paramref name="repeat"/> times (0 = until cancelled). Returns the number of completed iterations.</summary>
    public async Task<int> RunAsync(Macro macro, int repeat, CancellationToken ct)
    {
        int done = 0;
        for (int iteration = 1; repeat == 0 || iteration <= repeat; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            var held = new HashSet<int>();
            var clock = Stopwatch.StartNew();
            long shift = 0; // ms the timeline has been pushed back by checkpoints and waits
            try
            {
                for (int i = 0; i < macro.Steps.Count; i++)
                {
                    var step = macro.Steps[i];
                    long due = (long)(step.T / Speed) + shift;
                    long wait = due - clock.ElapsedMilliseconds;
                    if (wait > 0) await Task.Delay((int)Math.Min(wait, int.MaxValue), ct).ConfigureAwait(false);

                    switch (step.Kind)
                    {
                        case MacroStepKind.Touch:
                        {
                            int x = (int)Math.Round(step.X * (_target.GuestWidth - 1)), y = (int)Math.Round(step.Y * (_target.GuestHeight - 1));
                            _target.Touch(step.Id, x, y, step.Down);
                            if (step.Down) held.Add(step.Id); else held.Remove(step.Id);
                            break;
                        }
                        case MacroStepKind.AndroidKey:
                            if (AndroidKeys.TryGet(step.AndroidKey, out var code)) _target.AndroidKey(code, step.Down);
                            break;
                        case MacroStepKind.Wait:
                        {
                            Progress?.Invoke(new MacroProgress(iteration, i, macro.Steps.Count, $"wait {step.Ms} ms"));
                            var before = clock.ElapsedMilliseconds;
                            await Task.Delay((int)(step.Ms / Speed), ct).ConfigureAwait(false);
                            shift += clock.ElapsedMilliseconds - before;
                            break;
                        }
                        case MacroStepKind.WaitForScreen:
                        {
                            var before = clock.ElapsedMilliseconds;
                            bool ok = await WaitForScreenAsync(step, iteration, i, macro.Steps.Count, ct).ConfigureAwait(false);
                            shift += clock.ElapsedMilliseconds - before;
                            if (!ok && step.StopOnTimeout)
                            {
                                Progress?.Invoke(new MacroProgress(iteration, i, macro.Steps.Count, $"checkpoint '{step.Label ?? (i + 1).ToString()}' timed out; stopping"));
                                return done;
                            }
                            break;
                        }
                    }
                    Progress?.Invoke(new MacroProgress(iteration, i + 1, macro.Steps.Count, step.Kind.ToString()));
                }
                done++;
                Progress?.Invoke(new MacroProgress(iteration, macro.Steps.Count, macro.Steps.Count, $"iteration {iteration} done"));
            }
            finally
            {
                // Never leave a finger on the glass, whatever happened.
                foreach (var id in held) { try { _target.Touch(id, 0, 0, false); } catch { } }
            }
        }
        return done;
    }

    private async Task<bool> WaitForScreenAsync(MacroStep step, int iteration, int index, int count, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        double last = 255;
        while (deadline.ElapsedMilliseconds < step.TimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Request the frame exactly as the recorder did (from the display size), not with the stored
                // result size: the emulator fits inside the requested box, so echoing 192x426 back would
                // yield 191x426 and never compare equal.
                var (fw, fh) = ScreenMatcher.FrameSizeFor(_target.GuestWidth, _target.GuestHeight);
                var (rgb, w, h) = await _target.GetFrameAsync(fw, fh, ct).ConfigureAwait(false);
                last = ScreenMatcher.Compare(step, rgb, w, h);
                if (last <= step.Tolerance) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* transient gRPC hiccup: keep polling */ }
            Progress?.Invoke(new MacroProgress(iteration, index, count, $"waiting for '{step.Label ?? "screen"}' (diff {last:F0} > {step.Tolerance})"));
            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
        }
        return false;
    }
}
