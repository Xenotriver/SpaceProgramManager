namespace Sim.Time;

/// <summary>
/// Deterministic game clock and fixed-step accumulator. The clock never reads
/// the wall clock or the engine: callers feed real elapsed seconds and apply the
/// returned number of equal <see cref="FixedStepHours"/> steps to the campaign.
///
/// Time scale for M1: at 1×, one real second is one game hour. Speeds are 1×,
/// 2× and 4×; speed 0 pauses and drops any partial step so resuming cannot
/// inject a fractional tick that never happened.
/// </summary>
public sealed class SimulationClock
{
    /// <summary>Game hours per real second at 1×.</summary>
    public const double HoursPerRealSecond = 1.0;

    /// <summary>Simulation step. Every tick the campaign sees is exactly this long.</summary>
    public const double FixedStepHours = 0.25;

    /// <summary>Supported time multipliers; index 0 is pause.</summary>
    public static readonly IReadOnlyList<int> Speeds = new[] { 1, 2, 4 };

    private double _pendingHours;

    /// <summary>Total game time the clock has produced, in hours.</summary>
    public double GameTimeHours { get; private set; }

    /// <summary>Scaled real time not yet converted into a whole fixed step.</summary>
    public double PendingHours => _pendingHours;

    /// <summary>Current multiplier: 0 (paused), 1, 2 or 4.</summary>
    public int Speed { get; private set; }

    /// <summary>True when no time is produced.</summary>
    public bool IsPaused => Speed == 0;

    /// <summary>Pause (index 0) or one of the supported multipliers.</summary>
    public int SpeedIndex => Speed switch
    {
        1 => 1,
        2 => 2,
        >= 4 => 3,
        _ => 0,
    };

    /// <summary>Short label for the HUD.</summary>
    public string Label => Speed == 0 ? "paused" : $"{Speed}×";

    /// <summary>Sets the clock to a saved campaign time and drops pending time.</summary>
    public void Reset(double gameTimeHours)
    {
        GameTimeHours = Math.Max(0.0, gameTimeHours);
        _pendingHours = 0.0;
    }

    /// <summary>Sets the multiplier; anything other than 1, 2 or 4 pauses.</summary>
    public void SetSpeed(int speed)
    {
        Speed = speed is 1 or 2 or 4 ? speed : 0;
        if (Speed == 0)
            _pendingHours = 0.0;
    }

    /// <summary>Pauses a running clock, or resumes at 1× when paused.</summary>
    public void TogglePause() => SetSpeed(IsPaused ? 1 : 0);

    /// <summary>
    /// Feeds real elapsed seconds and returns how many fixed steps are due.
    /// <see cref="GameTimeHours"/> advances by <c>steps × FixedStepHours</c>.
    /// </summary>
    public int Advance(double realSeconds)
    {
        if (Speed == 0 || realSeconds <= 0.0)
            return 0;

        _pendingHours += realSeconds * HoursPerRealSecond * Speed;

        // The epsilon absorbs float accumulation at 60 fps without ever
        // producing an extra step for a value genuinely below the threshold.
        var steps = (int)Math.Floor(_pendingHours / FixedStepHours + 1e-9);
        if (steps <= 0)
            return 0;

        var consumed = steps * FixedStepHours;
        _pendingHours = Math.Max(0.0, _pendingHours - consumed);
        GameTimeHours += consumed;
        return steps;
    }
}
