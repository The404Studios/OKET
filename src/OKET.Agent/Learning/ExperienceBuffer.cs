using System.Collections.Concurrent;

namespace OKET.Agent.Learning;

/// <summary>
/// A single experience transition for RL training.
/// </summary>
public readonly struct Experience
{
    public required float[] State { get; init; }
    public required int Action { get; init; }
    public required float Reward { get; init; }
    public required float[] NextState { get; init; }
    public required bool Done { get; init; }
    public required float LogProbability { get; init; }
    public required float Value { get; init; }
}

/// <summary>
/// Trajectory: a sequence of experiences from one episode or rollout.
/// </summary>
public sealed class Trajectory
{
    public List<Experience> Experiences { get; } = new();
    public float TotalReward => Experiences.Sum(e => e.Reward);
    public int Length => Experiences.Count;

    public void Add(Experience exp) => Experiences.Add(exp);
    public void Clear() => Experiences.Clear();
}

/// <summary>
/// Experience replay buffer for storing and sampling training data.
/// Supports both random sampling (DQN-style) and sequential rollouts (PPO-style).
/// </summary>
public sealed class ExperienceBuffer
{
    private readonly ConcurrentQueue<Experience> _buffer = new();
    private readonly int _maxSize;
    private readonly object _lock = new();
    private readonly Random _random = new();

    // Statistics
    private long _totalAdded;
    private long _totalSampled;

    public int Count => _buffer.Count;
    public int MaxSize => _maxSize;
    public long TotalAdded => _totalAdded;
    public long TotalSampled => _totalSampled;
    public bool IsFull => _buffer.Count >= _maxSize;

    public ExperienceBuffer(int maxSize = 100_000)
    {
        _maxSize = maxSize;
    }

    /// <summary>
    /// Add a single experience to the buffer.
    /// </summary>
    public void Add(Experience experience)
    {
        _buffer.Enqueue(experience);
        Interlocked.Increment(ref _totalAdded);

        // Remove old experiences if over capacity
        while (_buffer.Count > _maxSize)
        {
            _buffer.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Add an entire trajectory to the buffer.
    /// </summary>
    public void AddTrajectory(Trajectory trajectory)
    {
        foreach (var exp in trajectory.Experiences)
        {
            Add(exp);
        }
    }

    /// <summary>
    /// Sample a random batch for DQN-style training.
    /// </summary>
    public Experience[] SampleRandom(int batchSize)
    {
        lock (_lock)
        {
            var allExperiences = _buffer.ToArray();
            if (allExperiences.Length == 0) return [];

            var actualBatchSize = Math.Min(batchSize, allExperiences.Length);
            var batch = new Experience[actualBatchSize];

            for (int i = 0; i < actualBatchSize; i++)
            {
                batch[i] = allExperiences[_random.Next(allExperiences.Length)];
            }

            Interlocked.Add(ref _totalSampled, actualBatchSize);
            return batch;
        }
    }

    /// <summary>
    /// Get all experiences and clear the buffer (for PPO-style on-policy training).
    /// </summary>
    public Experience[] DrainAll()
    {
        lock (_lock)
        {
            var result = _buffer.ToArray();
            while (_buffer.TryDequeue(out _)) { }
            Interlocked.Add(ref _totalSampled, result.Length);
            return result;
        }
    }

    /// <summary>
    /// Sample mini-batches from current buffer without removing (for PPO epochs).
    /// </summary>
    public IEnumerable<Experience[]> SampleMiniBatches(int miniBatchSize, int numEpochs = 1)
    {
        var allExperiences = _buffer.ToArray();
        if (allExperiences.Length == 0) yield break;

        for (int epoch = 0; epoch < numEpochs; epoch++)
        {
            // Shuffle
            var shuffled = allExperiences.OrderBy(_ => _random.Next()).ToArray();

            // Yield mini-batches
            for (int i = 0; i < shuffled.Length; i += miniBatchSize)
            {
                var batchSize = Math.Min(miniBatchSize, shuffled.Length - i);
                var batch = new Experience[batchSize];
                Array.Copy(shuffled, i, batch, 0, batchSize);
                Interlocked.Add(ref _totalSampled, batchSize);
                yield return batch;
            }
        }
    }

    public void Clear()
    {
        while (_buffer.TryDequeue(out _)) { }
    }

    public string GetDiagnostics() => $"""
        ExperienceBuffer:
          Count: {Count:N0} / {MaxSize:N0}
          Total Added: {TotalAdded:N0}
          Total Sampled: {TotalSampled:N0}
        """;
}

/// <summary>
/// Computes advantages and returns for PPO training.
/// </summary>
public static class AdvantageComputation
{
    /// <summary>
    /// Compute Generalized Advantage Estimation (GAE).
    /// </summary>
    public static (float[] advantages, float[] returns) ComputeGAE(
        Experience[] experiences,
        float gamma = 0.99f,
        float lambda = 0.95f)
    {
        int n = experiences.Length;
        var advantages = new float[n];
        var returns = new float[n];

        float lastGaeLam = 0;
        float lastValue = 0;

        // Process in reverse order
        for (int t = n - 1; t >= 0; t--)
        {
            var exp = experiences[t];
            float nextValue = t == n - 1 ? 0 : experiences[t + 1].Value;
            float mask = exp.Done ? 0 : 1;

            // TD error: r + gamma * V(s') - V(s)
            float delta = exp.Reward + gamma * nextValue * mask - exp.Value;

            // GAE: sum of discounted TD errors
            lastGaeLam = delta + gamma * lambda * mask * lastGaeLam;
            advantages[t] = lastGaeLam;

            // Returns = advantages + values
            returns[t] = advantages[t] + exp.Value;
        }

        // Normalize advantages
        float mean = advantages.Average();
        float std = (float)Math.Sqrt(advantages.Select(a => (a - mean) * (a - mean)).Average() + 1e-8);
        for (int i = 0; i < n; i++)
        {
            advantages[i] = (advantages[i] - mean) / std;
        }

        return (advantages, returns);
    }
}
