using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace OKET.Agent.Learning;

/// <summary>
/// PPO (Proximal Policy Optimization) training configuration.
/// </summary>
public sealed class PPOConfig
{
    public float LearningRate { get; init; } = 3e-4f;
    public float Gamma { get; init; } = 0.99f;
    public float Lambda { get; init; } = 0.95f;
    public float ClipEpsilon { get; init; } = 0.2f;
    public float ValueCoefficient { get; init; } = 0.5f;
    public float EntropyCoefficient { get; init; } = 0.01f;
    public int NumEpochs { get; init; } = 4;
    public int MiniBatchSize { get; init; } = 64;
    public float MaxGradNorm { get; init; } = 0.5f;
    public int RolloutLength { get; init; } = 2048;
}

/// <summary>
/// Training statistics for a single update.
/// </summary>
public readonly struct TrainingStats
{
    public float PolicyLoss { get; init; }
    public float ValueLoss { get; init; }
    public float EntropyLoss { get; init; }
    public float TotalLoss { get; init; }
    public float MeanAdvantage { get; init; }
    public float MeanReturn { get; init; }
    public float ClipFraction { get; init; }
    public float ExplainedVariance { get; init; }
    public int BatchSize { get; init; }
}

/// <summary>
/// PPO trainer for policy optimization.
/// Implements the clipped surrogate objective with value function learning.
/// </summary>
public sealed class PPOTrainer : IDisposable
{
    private readonly NeuralPolicy _policy;
    private readonly optim.Optimizer _optimizer;
    private readonly PPOConfig _config;

    // Training statistics
    private int _updateCount;
    private float _totalPolicyLoss;
    private float _totalValueLoss;
    private float _totalEntropyLoss;

    public int UpdateCount => _updateCount;
    public PPOConfig Config => _config;

    public PPOTrainer(NeuralPolicy policy, PPOConfig? config = null)
    {
        _policy = policy;
        _config = config ?? new PPOConfig();

        // Create optimizer
        _optimizer = optim.Adam(_policy.parameters(), lr: _config.LearningRate);
    }

    /// <summary>
    /// Perform a PPO update on the given experiences.
    /// </summary>
    public TrainingStats Update(Experience[] experiences)
    {
        if (experiences.Length == 0)
            return default;

        _policy.SetTrainingMode(true);

        // Compute advantages and returns using GAE
        var (advantages, returns) = AdvantageComputation.ComputeGAE(
            experiences, _config.Gamma, _config.Lambda);

        // Convert to tensors
        var states = tensor(experiences.SelectMany(e => e.State).ToArray())
            .reshape(experiences.Length, _policy.InputSize);
        var actions = tensor(experiences.Select(e => (long)e.Action).ToArray());
        var oldLogProbs = tensor(experiences.Select(e => e.LogProbability).ToArray());
        var advantagesTensor = tensor(advantages);
        var returnsTensor = tensor(returns);

        float totalPolicyLoss = 0;
        float totalValueLoss = 0;
        float totalEntropyLoss = 0;
        float totalClipFraction = 0;
        int numUpdates = 0;

        // Multiple epochs over the data
        for (int epoch = 0; epoch < _config.NumEpochs; epoch++)
        {
            // Shuffle indices
            var indices = Enumerable.Range(0, experiences.Length).OrderBy(_ => Random.Shared.Next()).ToArray();

            // Mini-batch updates
            for (int start = 0; start < experiences.Length; start += _config.MiniBatchSize)
            {
                int end = Math.Min(start + _config.MiniBatchSize, experiences.Length);
                var batchIndices = tensor(indices[start..end].Select(i => (long)i).ToArray());

                // Get batch data
                var batchStates = states.index_select(0, batchIndices);
                var batchActions = actions.index_select(0, batchIndices);
                var batchOldLogProbs = oldLogProbs.index_select(0, batchIndices);
                var batchAdvantages = advantagesTensor.index_select(0, batchIndices);
                var batchReturns = returnsTensor.index_select(0, batchIndices);

                // Forward pass
                var (logits, values) = _policy.forward(batchStates);

                // Compute new log probabilities
                var logProbs = functional.log_softmax(logits, dim: -1);
                var newLogProbs = logProbs.gather(1, batchActions.unsqueeze(1)).squeeze(1);

                // Compute probability ratio
                var ratio = (newLogProbs - batchOldLogProbs).exp();

                // Clipped surrogate objective
                var surr1 = ratio * batchAdvantages;
                var surr2 = ratio.clamp(1 - _config.ClipEpsilon, 1 + _config.ClipEpsilon) * batchAdvantages;
                var policyLoss = -torch.min(surr1, surr2).mean();

                // Value loss (MSE)
                var valueLoss = functional.mse_loss(values, batchReturns);

                // Entropy bonus (encourages exploration)
                var probs = functional.softmax(logits, dim: -1);
                var entropy = -(probs * logProbs).sum(dim: -1).mean();
                var entropyLoss = -_config.EntropyCoefficient * entropy;

                // Total loss
                var totalLoss = policyLoss + _config.ValueCoefficient * valueLoss + entropyLoss;

                // Backward pass
                _optimizer.zero_grad();
                totalLoss.backward();

                // Gradient clipping
                utils.clip_grad_norm_(_policy.parameters(), _config.MaxGradNorm);

                // Optimizer step
                _optimizer.step();

                // Track statistics
                totalPolicyLoss += policyLoss.item<float>();
                totalValueLoss += valueLoss.item<float>();
                totalEntropyLoss += entropy.item<float>();

                // Clip fraction (how often we hit the clip boundary)
                var clipped = ((ratio - 1).abs() > _config.ClipEpsilon).to_type(ScalarType.Float32);
                totalClipFraction += clipped.mean().item<float>();

                numUpdates++;
            }
        }

        _updateCount++;

        // Compute explained variance
        float meanReturn = returns.Average();
        float varReturn = returns.Select(r => (r - meanReturn) * (r - meanReturn)).Average();
        float meanValue = experiences.Select(e => e.Value).Average();
        float varResidual = experiences.Zip(returns)
            .Select(x => (x.First.Value - x.Second) * (x.First.Value - x.Second))
            .Average();
        float explainedVariance = varReturn > 0 ? 1 - varResidual / varReturn : 0;

        return new TrainingStats
        {
            PolicyLoss = totalPolicyLoss / numUpdates,
            ValueLoss = totalValueLoss / numUpdates,
            EntropyLoss = totalEntropyLoss / numUpdates,
            TotalLoss = (totalPolicyLoss + totalValueLoss - totalEntropyLoss * _config.EntropyCoefficient) / numUpdates,
            MeanAdvantage = advantages.Average(),
            MeanReturn = meanReturn,
            ClipFraction = totalClipFraction / numUpdates,
            ExplainedVariance = explainedVariance,
            BatchSize = experiences.Length
        };
    }

    /// <summary>
    /// Adjust learning rate (for learning rate scheduling).
    /// </summary>
    public void SetLearningRate(float lr)
    {
        foreach (var group in _optimizer.ParamGroups)
        {
            group.LearningRate = lr;
        }
    }

    /// <summary>
    /// Get current learning rate.
    /// </summary>
    public float GetLearningRate()
    {
        return (float)_optimizer.ParamGroups.First().LearningRate;
    }

    public string GetDiagnostics() => $"""
        PPOTrainer:
          Updates: {_updateCount}
          Learning Rate: {GetLearningRate():E3}
          Clip Epsilon: {_config.ClipEpsilon}
          Epochs: {_config.NumEpochs}
          Mini-Batch Size: {_config.MiniBatchSize}
        """;

    public void Dispose()
    {
        _optimizer.Dispose();
    }
}

/// <summary>
/// Learning rate scheduler for PPO.
/// </summary>
public sealed class LinearScheduler
{
    private readonly float _initialLr;
    private readonly float _finalLr;
    private readonly int _totalSteps;
    private int _currentStep;

    public LinearScheduler(float initialLr, float finalLr, int totalSteps)
    {
        _initialLr = initialLr;
        _finalLr = finalLr;
        _totalSteps = totalSteps;
    }

    public float Step()
    {
        _currentStep++;
        float progress = Math.Min(1f, (float)_currentStep / _totalSteps);
        return _initialLr + progress * (_finalLr - _initialLr);
    }

    public float CurrentLr => _initialLr + Math.Min(1f, (float)_currentStep / _totalSteps) * (_finalLr - _initialLr);
}
