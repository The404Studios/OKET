using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;
using OKET.Core.State;
using OKET.Core.Actions;
using OKET.Core.Interfaces;

namespace OKET.Agent.Learning;

/// <summary>
/// Neural network policy for Actor-Critic RL.
/// Takes game state features and outputs action probabilities + state value.
/// </summary>
public sealed class NeuralPolicy : Module<Tensor, (Tensor logits, Tensor value)>, IPolicy
{
    // Number of strategic modes (actions)
    public const int NumActions = 10; // Idle, Fight, Kite, Reload, Heal, Repair, Reposition, Buy, Support, Unstick

    // Network architecture
    private readonly Sequential _shared;
    private readonly Linear _policyHead;
    private readonly Linear _valueHead;

    // For inference without gradients
    private readonly Random _random = new();
    private bool _training = true;

    public string Name => "NeuralPolicy";
    public int InputSize { get; }

    public NeuralPolicy(int inputSize = 18, int hiddenSize = 128) : base(nameof(NeuralPolicy))
    {
        InputSize = inputSize;

        // Shared feature extraction layers
        _shared = Sequential(
            Linear(inputSize, hiddenSize),
            ReLU(),
            Linear(hiddenSize, hiddenSize),
            ReLU(),
            Linear(hiddenSize, hiddenSize / 2),
            ReLU()
        );

        // Policy head: outputs logits for each action
        _policyHead = Linear(hiddenSize / 2, NumActions);

        // Value head: outputs scalar state value
        _valueHead = Linear(hiddenSize / 2, 1);

        // Register modules
        RegisterComponents();

        // Initialize weights
        InitializeWeights();
    }

    private void InitializeWeights()
    {
        foreach (var param in parameters())
        {
            if (param.dim() >= 2)
            {
                init.xavier_uniform_(param);
            }
        }
    }

    public override (Tensor logits, Tensor value) forward(Tensor x)
    {
        var features = _shared.forward(x);
        var logits = _policyHead.forward(features);
        var value = _valueHead.forward(features).squeeze(-1);
        return (logits, value);
    }

    /// <summary>
    /// Get action probabilities from state features.
    /// </summary>
    public Tensor GetActionProbs(Tensor state)
    {
        var (logits, _) = forward(state);
        return functional.softmax(logits, dim: -1);
    }

    /// <summary>
    /// Get value estimate from state features.
    /// </summary>
    public Tensor GetValue(Tensor state)
    {
        var (_, value) = forward(state);
        return value;
    }

    /// <summary>
    /// Sample an action and get log probability.
    /// </summary>
    public (int action, float logProb, float value) SampleAction(float[] stateFeatures)
    {
        using var _ = no_grad();

        var stateTensor = tensor(stateFeatures).unsqueeze(0);
        var (logits, valueTensor) = forward(stateTensor);

        // Convert to probabilities
        var probs = functional.softmax(logits, dim: -1).squeeze(0);
        var probsArray = probs.data<float>().ToArray();

        // Sample action from categorical distribution
        int action = SampleCategorical(probsArray);

        // Get log probability
        var logProbs = functional.log_softmax(logits, dim: -1).squeeze(0);
        float logProb = logProbs[action].item<float>();

        // Get value
        float value = valueTensor.item<float>();

        return (action, logProb, value);
    }

    /// <summary>
    /// Get deterministic action (argmax) for evaluation.
    /// </summary>
    public int GetBestAction(float[] stateFeatures)
    {
        using var _ = no_grad();

        var stateTensor = tensor(stateFeatures).unsqueeze(0);
        var (logits, _) = forward(stateTensor);

        return (int)logits.argmax(dim: -1).item<long>();
    }

    /// <summary>
    /// IPolicy implementation - convert neural network output to strategic mode.
    /// </summary>
    public (StrategicMode Mode, float Confidence) Decide(GameState state)
    {
        var features = state.ToFeatureVector();

        if (_training)
        {
            // During training, sample from distribution
            var (action, _, _) = SampleAction(features);
            var probs = GetActionProbabilities(features);
            return (ActionToMode(action), probs[action]);
        }
        else
        {
            // During evaluation, use best action
            var action = GetBestAction(features);
            var probs = GetActionProbabilities(features);
            return (ActionToMode(action), probs[action]);
        }
    }

    /// <summary>
    /// Get action probabilities as array (for logging/debugging).
    /// </summary>
    public float[] GetActionProbabilities(float[] stateFeatures)
    {
        using var _ = no_grad();

        var stateTensor = tensor(stateFeatures).unsqueeze(0);
        var probs = GetActionProbs(stateTensor).squeeze(0);
        return probs.data<float>().ToArray();
    }

    /// <summary>
    /// Convert action index to StrategicMode.
    /// </summary>
    public static StrategicMode ActionToMode(int action) => action switch
    {
        0 => StrategicMode.Idle,
        1 => StrategicMode.Fight,
        2 => StrategicMode.Kite,
        3 => StrategicMode.Reload,
        4 => StrategicMode.Heal,
        5 => StrategicMode.Repair,
        6 => StrategicMode.Reposition,
        7 => StrategicMode.Buy,
        8 => StrategicMode.Support,
        9 => StrategicMode.Unstick,
        _ => StrategicMode.Idle
    };

    /// <summary>
    /// Convert StrategicMode to action index.
    /// </summary>
    public static int ModeToAction(StrategicMode mode) => mode switch
    {
        StrategicMode.Idle => 0,
        StrategicMode.Fight => 1,
        StrategicMode.Kite => 2,
        StrategicMode.Reload => 3,
        StrategicMode.Heal => 4,
        StrategicMode.Repair => 5,
        StrategicMode.Reposition => 6,
        StrategicMode.Buy => 7,
        StrategicMode.Support => 8,
        StrategicMode.Unstick => 9,
        _ => 0
    };

    private int SampleCategorical(float[] probs)
    {
        float u = (float)_random.NextDouble();
        float cumSum = 0;
        for (int i = 0; i < probs.Length; i++)
        {
            cumSum += probs[i];
            if (u < cumSum) return i;
        }
        return probs.Length - 1;
    }

    public void SetTrainingMode(bool training)
    {
        _training = training;
        if (training)
            train();
        else
            eval();
    }

    /// <summary>
    /// Save model to file.
    /// </summary>
    public void SaveModel(string path)
    {
        save(path);
    }

    /// <summary>
    /// Load model from file.
    /// </summary>
    public void LoadModel(string path)
    {
        load(path);
    }

    /// <summary>
    /// Create a copy of this policy for target network (if using DQN-style).
    /// </summary>
    public NeuralPolicy Clone()
    {
        var clone = new NeuralPolicy(InputSize);
        clone.load_state_dict(state_dict());
        return clone;
    }

    /// <summary>
    /// Soft update from another policy (for target network updates).
    /// </summary>
    public void SoftUpdate(NeuralPolicy source, float tau = 0.005f)
    {
        var sourceDict = source.state_dict();
        var targetDict = state_dict();

        foreach (var key in sourceDict.Keys)
        {
            var sourceParam = sourceDict[key];
            var targetParam = targetDict[key];
            targetDict[key] = tau * sourceParam + (1 - tau) * targetParam;
        }

        load_state_dict(targetDict);
    }

    public string GetDiagnostics()
    {
        var paramCount = parameters().Sum(p => p.numel());
        return $"""
            NeuralPolicy:
              Parameters: {paramCount:N0}
              Input Size: {InputSize}
              Actions: {NumActions}
              Training: {_training}
            """;
    }
}
