using OKET.Core.Types;

namespace OKET.Input;

/// <summary>
/// Provides smoothed mouse movement for human-like aiming.
/// </summary>
public sealed class SmoothMouseController
{
    private readonly Win32Input _input;

    private Vector2 _currentVelocity;
    private Vector2 _targetPosition;
    private Vector2 _currentPosition;

    // Smoothing parameters
    public float Acceleration { get; set; } = 0.3f;
    public float Deceleration { get; set; } = 0.5f;
    public float MaxSpeed { get; set; } = 80f;
    public float MinSpeed { get; set; } = 2f;
    public float SnapDistance { get; set; } = 5f;

    // Human-like variation
    public float Jitter { get; set; } = 0.5f;
    public bool AddMicroCorrections { get; set; } = true;

    public SmoothMouseController(Win32Input input)
    {
        _input = input;
    }

    /// <summary>
    /// Move the mouse toward a target offset over multiple frames.
    /// Call this every frame for smooth movement.
    /// </summary>
    public void MoveToward(Vector2 targetOffset)
    {
        _targetPosition = _currentPosition + targetOffset;
        UpdateMovement();
    }

    /// <summary>
    /// Update the smooth movement. Call once per frame.
    /// </summary>
    public void UpdateMovement()
    {
        var delta = _targetPosition - _currentPosition;
        float distance = delta.Length;

        if (distance < SnapDistance)
        {
            // Close enough - do final snap
            if (distance > 0.5f)
            {
                _input.MouseMove(delta.X, delta.Y);
                _currentPosition = _targetPosition;
            }
            _currentVelocity = Vector2.Zero;
            return;
        }

        // Calculate desired velocity
        var direction = delta.Normalized;

        // Speed based on distance (faster when far, slower when close)
        float targetSpeed = MathF.Min(distance * Acceleration * 2, MaxSpeed);
        targetSpeed = MathF.Max(targetSpeed, MinSpeed);

        // Smooth velocity change
        var targetVelocity = direction * targetSpeed;
        _currentVelocity = Vector2.Lerp(_currentVelocity, targetVelocity, Deceleration);

        // Add human-like jitter
        if (Jitter > 0)
        {
            _currentVelocity += new Vector2(
                (Random.Shared.NextSingle() - 0.5f) * Jitter,
                (Random.Shared.NextSingle() - 0.5f) * Jitter
            );
        }

        // Apply movement
        var move = _currentVelocity;

        // Add micro-corrections (overshoot and correct)
        if (AddMicroCorrections && distance < 50 && Random.Shared.NextSingle() < 0.1f)
        {
            // Occasionally overshoot slightly
            move = move * 1.1f;
        }

        _input.MouseMove(move.X, move.Y);
        _currentPosition = _currentPosition + move;
    }

    /// <summary>
    /// Immediately move the mouse (no smoothing).
    /// </summary>
    public void MoveImmediate(float dx, float dy)
    {
        _input.MouseMove(dx, dy);
        _currentPosition = _currentPosition + new Vector2(dx, dy);
    }

    /// <summary>
    /// Reset tracking state.
    /// </summary>
    public void Reset()
    {
        _currentVelocity = Vector2.Zero;
        _targetPosition = Vector2.Zero;
        _currentPosition = Vector2.Zero;
    }
}
