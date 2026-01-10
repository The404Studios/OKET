using OKET.Core.Types;
using OKET.Core.State;
using OKET.Core.Audio;
using OKET.Core.Detection;
using OKET.Core.Cognition;

namespace OKET.Agent.Fusion;

/// <summary>
/// Fuses vision, audio, and HUD into a unified belief state.
/// Implements cross-modal validation and confidence weighting.
/// </summary>
public sealed class MultimodalFusion
{
    private BeliefState? _lastBelief;
    private readonly ExponentialStatistics _agreementStats = new(0.1);

    // Recent events for temporal correlation
    private readonly Queue<(DateTime Time, string Source, string Event)> _eventLog = new();
    private const int MaxEventLogSize = 100;

    // Hit confirmation tracking
    private DateTime _lastVisualHit;
    private DateTime _lastAudioHit;
    private const double HitCorrelationWindowMs = 200;

    public BeliefState Fuse(GameState gameState, AudioSnapshot audioSnapshot)
    {
        // Extract proposals from each modality
        var visionProposal = ExtractVisionProposal(gameState);
        var audioProposal = ExtractAudioProposal(audioSnapshot);
        var hudProposal = ExtractHudProposal(gameState.Hud);

        // Cross-validate proposals
        var (agreement, conflicts) = ValidateProposals(visionProposal, audioProposal, hudProposal);

        // Calculate confidence weights based on agreement
        var weights = CalculateWeights(visionProposal, audioProposal, hudProposal, agreement);

        // Fuse into unified belief
        var belief = FuseProposals(visionProposal, audioProposal, hudProposal, weights, agreement, gameState.FrameId);

        // Track belief changes
        float beliefDelta = _lastBelief != null ? CalculateBeliefDelta(_lastBelief, belief) : 0;

        belief = belief with { BeliefDelta = beliefDelta };
        _lastBelief = belief;

        return belief;
    }

    private ModalityProposal ExtractVisionProposal(GameState state)
    {
        float threatLevel = 0;
        float threatDirection = 0;
        float threatProximity = 0;

        if (state.Detections.ThreatCount > 0)
        {
            // Calculate threat level from detections
            threatLevel = Math.Min(state.Detections.ThreatCount * 0.2f + state.DangerLevel * 0.5f, 1f);

            // Find primary threat direction
            var primary = state.Detections.PrimaryThreat;
            if (primary != null)
            {
                float centerX = state.ScreenSize.X / 2;
                threatDirection = (primary.Box.Center.X - centerX) / (state.ScreenSize.X / 2);
                threatProximity = 1f - Math.Clamp(state.NearestThreatDistance / 500f, 0f, 1f);
            }
        }

        // Check for visual hit confirmation
        bool visualHit = state.Aim.HitConfirmed;
        if (visualHit)
        {
            _lastVisualHit = DateTime.UtcNow;
            LogEvent("vision", "hit_marker");
        }

        return new ModalityProposal
        {
            ThreatLevel = threatLevel,
            ThreatDirection = threatDirection,
            ThreatProximity = threatProximity,
            HitDetected = visualHit,
            IsReloading = state.Hud.IsReloading,
            AmmoState = state.Hud.AmmoClip == 0 ? ReloadBelief.Empty :
                        state.Hud.AmmoClip <= 5 ? ReloadBelief.Low : ReloadBelief.Ready,
            HealthRisk = 1f - state.Hud.Health / 100f,
            Confidence = state.Hud.Confidence
        };
    }

    private ModalityProposal ExtractAudioProposal(AudioSnapshot audio)
    {
        if (!audio.IsValid)
        {
            return new ModalityProposal { Confidence = 0 };
        }

        float threatLevel = 0;
        float threatDirection = 0;
        bool hitDetected = false;
        bool reloadSound = false;
        bool damageSound = false;

        foreach (var evt in audio.Events)
        {
            switch (evt.Type)
            {
                case AudioEventType.ZombieGroan:
                case AudioEventType.ZombieScream:
                case AudioEventType.ZombieFootsteps:
                    threatLevel = Math.Max(threatLevel, evt.Confidence * 0.8f);
                    threatDirection = evt.Direction;
                    break;

                case AudioEventType.HitMarker:
                    hitDetected = true;
                    _lastAudioHit = DateTime.UtcNow;
                    LogEvent("audio", "hit_marker");
                    break;

                case AudioEventType.ReloadComplete:
                case AudioEventType.ReloadStart:
                    reloadSound = true;
                    break;

                case AudioEventType.DamageTaken:
                case AudioEventType.Heartbeat:
                    damageSound = true;
                    break;
            }
        }

        return new ModalityProposal
        {
            ThreatLevel = threatLevel,
            ThreatDirection = threatDirection,
            ThreatProximity = audio.HasThreatSounds ? 0.5f : 0f,
            HitDetected = hitDetected,
            IsReloading = reloadSound,
            AmmoState = ReloadBelief.Ready, // Audio can't determine ammo count
            HealthRisk = damageSound ? 0.5f : 0f,
            Confidence = audio.Events.Count > 0 ?
                audio.Events.Max(e => e.Confidence) : 0.3f
        };
    }

    private ModalityProposal ExtractHudProposal(HudState hud)
    {
        return new ModalityProposal
        {
            ThreatLevel = hud.IsCriticalHealth ? 0.8f : hud.IsLowHealth ? 0.5f : 0f,
            ThreatDirection = 0, // HUD can't determine direction
            ThreatProximity = 0, // HUD can't determine proximity
            HitDetected = false, // HUD doesn't show hits directly
            IsReloading = hud.IsReloading,
            AmmoState = hud.AmmoClip == 0 ? ReloadBelief.Empty :
                        hud.IsReloading ? ReloadBelief.Reloading :
                        hud.AmmoClip <= 5 ? ReloadBelief.Low : ReloadBelief.Ready,
            HealthRisk = 1f - hud.Health / 100f,
            Confidence = hud.Confidence
        };
    }

    private (float Agreement, List<string> Conflicts) ValidateProposals(
        ModalityProposal vision, ModalityProposal audio, ModalityProposal hud)
    {
        var conflicts = new List<string>();
        float agreementScore = 1f;

        // Check threat level agreement
        float maxThreat = Math.Max(Math.Max(vision.ThreatLevel, audio.ThreatLevel), hud.ThreatLevel);
        float minThreat = Math.Min(Math.Min(vision.ThreatLevel, audio.ThreatLevel), hud.ThreatLevel);

        if (maxThreat - minThreat > 0.5f)
        {
            conflicts.Add("threat_level_mismatch");
            agreementScore -= 0.2f;
        }

        // Vision sees threat but audio doesn't hear it
        if (vision.ThreatLevel > 0.5f && audio.ThreatLevel < 0.2f && audio.Confidence > 0.3f)
        {
            conflicts.Add("vision_threat_no_audio");
            agreementScore -= 0.15f;
        }

        // Audio hears threat but vision doesn't see it
        if (audio.ThreatLevel > 0.5f && vision.ThreatLevel < 0.2f && vision.Confidence > 0.3f)
        {
            conflicts.Add("audio_threat_no_vision");
            agreementScore -= 0.15f;
        }

        // Hit confirmation cross-check
        var timeSinceVisualHit = (DateTime.UtcNow - _lastVisualHit).TotalMilliseconds;
        var timeSinceAudioHit = (DateTime.UtcNow - _lastAudioHit).TotalMilliseconds;

        if (vision.HitDetected != audio.HitDetected)
        {
            // Check if they're close in time (acceptable)
            if (Math.Abs(timeSinceVisualHit - timeSinceAudioHit) > HitCorrelationWindowMs)
            {
                conflicts.Add("hit_confirmation_mismatch");
                agreementScore -= 0.1f;
            }
        }

        agreementScore = Math.Clamp(agreementScore, 0f, 1f);
        _agreementStats.Add(agreementScore);

        return (agreementScore, conflicts);
    }

    private ModalityWeights CalculateWeights(
        ModalityProposal vision, ModalityProposal audio, ModalityProposal hud, float agreement)
    {
        // Base weights from confidence
        float vw = vision.Confidence;
        float aw = audio.Confidence;
        float hw = hud.Confidence;

        // Boost agreeing modalities
        if (agreement > 0.7f)
        {
            // High agreement - trust all equally
        }
        else if (agreement < 0.5f)
        {
            // Low agreement - trust HUD more (it's most reliable)
            hw *= 1.3f;
            vw *= 0.8f;
            aw *= 0.8f;
        }

        // For specific beliefs, some modalities are authoritative
        // (these weights will be used selectively)

        // Normalize
        float total = vw + aw + hw;
        if (total < 0.01f) total = 1f;

        return new ModalityWeights
        {
            Vision = vw / total,
            Audio = aw / total,
            Hud = hw / total
        };
    }

    private BeliefState FuseProposals(
        ModalityProposal vision, ModalityProposal audio, ModalityProposal hud,
        ModalityWeights weights, float agreement, long frameId)
    {
        // Threat level: weighted average, but audio boosts if hearing threats
        float threatLevel = vision.ThreatLevel * weights.Vision +
                            audio.ThreatLevel * weights.Audio +
                            hud.ThreatLevel * weights.Hud;

        // If audio hears zombies but vision doesn't see them, increase threat
        if (audio.ThreatLevel > 0.4f && vision.ThreatLevel < 0.2f)
        {
            threatLevel = Math.Max(threatLevel, audio.ThreatLevel * 0.7f);
        }

        // Threat direction: prefer vision if available, fall back to audio
        float threatDirection = vision.ThreatLevel > 0.3f ? vision.ThreatDirection :
                                audio.ThreatLevel > 0.3f ? audio.ThreatDirection : 0;

        // Threat proximity: vision is authoritative here
        float threatProximity = vision.ThreatProximity;

        // Hit confirmation: require agreement OR high single-modality confidence
        bool hitConfirmed = false;
        float hitConfidence = 0;

        var timeSinceVisualHit = (DateTime.UtcNow - _lastVisualHit).TotalMilliseconds;
        var timeSinceAudioHit = (DateTime.UtcNow - _lastAudioHit).TotalMilliseconds;

        // Both agree (within time window)
        if (timeSinceVisualHit < HitCorrelationWindowMs && timeSinceAudioHit < HitCorrelationWindowMs)
        {
            hitConfirmed = true;
            hitConfidence = 0.95f;
        }
        // Only one detected, but with high confidence
        else if (timeSinceVisualHit < 100 && vision.Confidence > 0.7f)
        {
            hitConfirmed = true;
            hitConfidence = 0.7f;
        }
        else if (timeSinceAudioHit < 100 && audio.Confidence > 0.7f)
        {
            hitConfirmed = true;
            hitConfidence = 0.6f;
        }

        // Reload state: HUD is authoritative, audio can confirm
        var reloadState = hud.AmmoState;
        float reloadConfidence = hud.Confidence;

        if (audio.IsReloading && reloadState != ReloadBelief.Reloading)
        {
            reloadState = ReloadBelief.Reloading;
            reloadConfidence = Math.Max(reloadConfidence, audio.Confidence);
        }

        // Health risk: HUD is authoritative
        float healthRisk = hud.HealthRisk;

        // Overall confidence
        float confidence = agreement * 0.4f +
                           (weights.Vision * vision.Confidence +
                            weights.Audio * audio.Confidence +
                            weights.Hud * hud.Confidence) * 0.6f;

        return new BeliefState
        {
            FrameId = frameId,
            Timestamp = DateTime.UtcNow,
            ThreatLevel = threatLevel,
            ThreatDirection = threatDirection,
            ThreatProximity = threatProximity,
            HitConfirmed = hitConfirmed,
            HitConfidence = hitConfidence,
            ReloadState = reloadState,
            ReloadConfidence = reloadConfidence,
            HealthRisk = healthRisk,
            RepairActive = false, // Would need repair detection
            RepairConfidence = 0,
            Confidence = confidence,
            SensoryAgreement = agreement,
            VisionContribution = weights.Vision,
            AudioContribution = weights.Audio,
            HudContribution = weights.Hud
        };
    }

    private float CalculateBeliefDelta(BeliefState prev, BeliefState curr)
    {
        float delta = 0;
        delta += Math.Abs(prev.ThreatLevel - curr.ThreatLevel) * 0.3f;
        delta += Math.Abs(prev.ThreatDirection - curr.ThreatDirection) * 0.1f;
        delta += Math.Abs(prev.HealthRisk - curr.HealthRisk) * 0.3f;
        delta += (prev.HitConfirmed != curr.HitConfirmed ? 0.2f : 0);
        delta += (prev.ReloadState != curr.ReloadState ? 0.1f : 0);
        return Math.Clamp(delta, 0f, 1f);
    }

    private void LogEvent(string source, string eventType)
    {
        _eventLog.Enqueue((DateTime.UtcNow, source, eventType));
        while (_eventLog.Count > MaxEventLogSize)
            _eventLog.Dequeue();
    }

    private sealed record ModalityProposal
    {
        public float ThreatLevel { get; init; }
        public float ThreatDirection { get; init; }
        public float ThreatProximity { get; init; }
        public bool HitDetected { get; init; }
        public bool IsReloading { get; init; }
        public ReloadBelief AmmoState { get; init; }
        public float HealthRisk { get; init; }
        public float Confidence { get; init; }
    }

    private sealed record ModalityWeights
    {
        public float Vision { get; init; }
        public float Audio { get; init; }
        public float Hud { get; init; }
    }
}
