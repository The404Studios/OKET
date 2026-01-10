using OKET.Core.Audio;
using OKET.Core.Interfaces;

namespace OKET.Vision.Audio;

/// <summary>
/// Template-matching audio classifier for game sounds.
/// Uses spectral signatures and envelope patterns.
/// </summary>
public sealed class TemplateAudioClassifier : IAudioClassifier
{
    public float ConfidenceThreshold { get; set; } = 0.4f;

    // Feature extraction settings
    private const int FftSize = 512;
    private const int NumMelBins = 20;

    // Recent state for temporal patterns
    private float _lastLevel;
    private DateTime _lastHitTime;
    private DateTime _lastDamageTime;

    public IReadOnlyList<AudioEvent> Classify(float[] samples, int sampleRate, DateTime timestamp)
    {
        var events = new List<AudioEvent>();

        if (samples.Length < FftSize) return events;

        // Extract features
        var features = ExtractFeatures(samples, sampleRate);

        // Check each sound type
        CheckGunfire(features, timestamp, events);
        CheckHitMarker(features, timestamp, events);
        CheckZombieSounds(features, timestamp, events);
        CheckDamageSounds(features, timestamp, events);
        CheckReloadSounds(features, timestamp, events);
        CheckBarricadeSounds(features, timestamp, events);

        // Filter by confidence
        return events.Where(e => e.Confidence >= ConfidenceThreshold).ToList();
    }

    private AudioFeatures ExtractFeatures(float[] samples, int sampleRate)
    {
        // RMS energy
        float rms = MathF.Sqrt(samples.Average(s => s * s));

        // Peak detection
        float peak = samples.Max(Math.Abs);

        // Zero crossing rate (correlates with frequency)
        int zeroCrossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i] >= 0) != (samples[i - 1] >= 0))
                zeroCrossings++;
        }
        float zcr = zeroCrossings / (float)samples.Length;

        // Simple onset detection (energy change)
        float onset = Math.Abs(rms - _lastLevel);
        _lastLevel = rms;

        // Envelope shape (attack time)
        float attackTime = EstimateAttackTime(samples);

        // Spectral features (simplified without full FFT)
        float spectralFlux = CalculateSpectralFlux(samples);
        float spectralFlatness = CalculateSpectralFlatness(samples);

        // Stereo difference (if we had stereo - approximated)
        float stereoBalance = 0; // Would calculate from stereo samples

        return new AudioFeatures
        {
            Rms = rms,
            Peak = peak,
            ZeroCrossingRate = zcr,
            OnsetStrength = onset,
            AttackTime = attackTime,
            SpectralFlux = spectralFlux,
            SpectralFlatness = spectralFlatness,
            StereoBalance = stereoBalance
        };
    }

    private float EstimateAttackTime(float[] samples)
    {
        // Find peak index
        int peakIdx = 0;
        float peakVal = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            var abs = Math.Abs(samples[i]);
            if (abs > peakVal)
            {
                peakVal = abs;
                peakIdx = i;
            }
        }

        // Find 10% threshold before peak
        float threshold = peakVal * 0.1f;
        int attackStart = peakIdx;
        for (int i = peakIdx; i >= 0; i--)
        {
            if (Math.Abs(samples[i]) < threshold)
            {
                attackStart = i;
                break;
            }
        }

        // Normalize to 0-1 (short attack = 0, long attack = 1)
        return Math.Clamp((peakIdx - attackStart) / (float)samples.Length * 10f, 0f, 1f);
    }

    private float CalculateSpectralFlux(float[] samples)
    {
        // Simplified: measure how much the signal changes
        float flux = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            float diff = Math.Abs(samples[i] - samples[i - 1]);
            flux += diff;
        }
        return Math.Clamp(flux / samples.Length * 5f, 0f, 1f);
    }

    private float CalculateSpectralFlatness(float[] samples)
    {
        // Ratio of geometric mean to arithmetic mean of |samples|
        // Approximated without FFT
        float sum = 0, logSum = 0;
        int count = 0;

        foreach (var s in samples)
        {
            var abs = Math.Abs(s) + 1e-10f;
            sum += abs;
            logSum += MathF.Log(abs);
            count++;
        }

        if (count == 0) return 0;

        float arithmeticMean = sum / count;
        float geometricMean = MathF.Exp(logSum / count);

        return Math.Clamp(geometricMean / arithmeticMean, 0f, 1f);
    }

    private void CheckGunfire(AudioFeatures f, DateTime timestamp, List<AudioEvent> events)
    {
        // Gunfire: loud, sharp attack, high flux
        if (f.Peak > 0.3f && f.AttackTime < 0.2f && f.SpectralFlux > 0.3f)
        {
            float confidence = (f.Peak * 0.4f + (1 - f.AttackTime) * 0.3f + f.SpectralFlux * 0.3f);

            events.Add(new AudioEvent
            {
                Type = f.Peak > 0.6f ? AudioEventType.GunfireNear : AudioEventType.GunfireFar,
                Confidence = confidence,
                Timestamp = timestamp,
                Intensity = f.Peak,
                DurationMs = 50
            });
        }
    }

    private void CheckHitMarker(AudioFeatures f, DateTime timestamp, List<AudioEvent> events)
    {
        // Hit marker: distinctive "ding" sound
        // High frequency, moderate level, very short
        if (f.ZeroCrossingRate > 0.3f && f.Peak > 0.2f && f.Peak < 0.6f &&
            f.AttackTime < 0.15f && f.SpectralFlatness < 0.3f)
        {
            // Debounce: not too soon after last hit
            if ((timestamp - _lastHitTime).TotalMilliseconds > 100)
            {
                float confidence = f.ZeroCrossingRate * 0.4f + (1 - f.AttackTime) * 0.3f +
                                   (1 - f.SpectralFlatness) * 0.3f;

                events.Add(new AudioEvent
                {
                    Type = AudioEventType.HitMarker,
                    Confidence = confidence,
                    Timestamp = timestamp,
                    Intensity = f.Peak,
                    DurationMs = 30
                });

                _lastHitTime = timestamp;
            }
        }
    }

    private void CheckZombieSounds(AudioFeatures f, DateTime timestamp, List<AudioEvent> events)
    {
        // Zombie groan: low frequency, sustained, rough spectrum
        if (f.ZeroCrossingRate < 0.2f && f.Rms > 0.1f && f.SpectralFlatness > 0.4f)
        {
            float confidence = (1 - f.ZeroCrossingRate) * 0.4f + f.SpectralFlatness * 0.3f +
                               Math.Min(f.Rms * 2, 1f) * 0.3f;

            events.Add(new AudioEvent
            {
                Type = AudioEventType.ZombieGroan,
                Confidence = confidence,
                Timestamp = timestamp,
                Intensity = f.Rms,
                DurationMs = 500
            });
        }

        // Zombie scream: high frequency, loud, harsh
        if (f.ZeroCrossingRate > 0.4f && f.Peak > 0.4f && f.SpectralFlatness > 0.5f)
        {
            float confidence = f.ZeroCrossingRate * 0.3f + f.Peak * 0.4f + f.SpectralFlatness * 0.3f;

            events.Add(new AudioEvent
            {
                Type = AudioEventType.ZombieScream,
                Confidence = confidence,
                Timestamp = timestamp,
                Intensity = f.Peak,
                DurationMs = 300
            });
        }
    }

    private void CheckDamageSounds(AudioFeatures f, DateTime timestamp, List<AudioEvent> events)
    {
        // Damage taken: distinctive pain sound
        // Usually includes a thud + voice
        if (f.Peak > 0.35f && f.SpectralFlux > 0.4f && f.AttackTime < 0.25f)
        {
            // Debounce
            if ((timestamp - _lastDamageTime).TotalMilliseconds > 200)
            {
                float confidence = f.Peak * 0.4f + f.SpectralFlux * 0.3f + (1 - f.AttackTime) * 0.3f;

                events.Add(new AudioEvent
                {
                    Type = AudioEventType.DamageTaken,
                    Confidence = confidence * 0.8f, // Lower confidence - easy to false positive
                    Timestamp = timestamp,
                    Intensity = f.Peak,
                    DurationMs = 100
                });

                _lastDamageTime = timestamp;
            }
        }

        // Heartbeat: very low frequency, rhythmic
        if (f.ZeroCrossingRate < 0.1f && f.Rms > 0.05f && f.AttackTime > 0.3f)
        {
            events.Add(new AudioEvent
            {
                Type = AudioEventType.Heartbeat,
                Confidence = 0.5f,
                Timestamp = timestamp,
                Intensity = f.Rms,
                DurationMs = 200
            });
        }
    }

    private void CheckReloadSounds(AudioFeatures f, DateTime timestamp, List<AudioEvent> events)
    {
        // Reload: mechanical sounds, moderate level, specific rhythm
        // This is harder to detect without templates

        // Magazine insertion: click sound
        if (f.Peak > 0.2f && f.Peak < 0.5f && f.AttackTime < 0.1f &&
            f.SpectralFlatness < 0.4f && f.ZeroCrossingRate > 0.2f)
        {
            events.Add(new AudioEvent
            {
                Type = AudioEventType.ReloadComplete,
                Confidence = 0.4f, // Low confidence without templates
                Timestamp = timestamp,
                Intensity = f.Peak,
                DurationMs = 100
            });
        }
    }

    private void CheckBarricadeSounds(AudioFeatures f, DateTime timestamp, List<AudioEvent> events)
    {
        // Barricade repair: hammering/construction sounds
        // Rhythmic impacts
        if (f.Peak > 0.15f && f.Peak < 0.4f && f.AttackTime < 0.15f &&
            f.SpectralFlux > 0.3f && f.OnsetStrength > 0.1f)
        {
            events.Add(new AudioEvent
            {
                Type = AudioEventType.BarricadeRepair,
                Confidence = 0.4f,
                Timestamp = timestamp,
                Intensity = f.Peak,
                DurationMs = 80
            });
        }

        // Barricade hit: wood breaking/impact
        if (f.Peak > 0.3f && f.AttackTime < 0.2f && f.SpectralFlatness > 0.5f)
        {
            events.Add(new AudioEvent
            {
                Type = AudioEventType.BarricadeHit,
                Confidence = 0.45f,
                Timestamp = timestamp,
                Intensity = f.Peak,
                DurationMs = 150
            });
        }
    }

    private record AudioFeatures
    {
        public float Rms { get; init; }
        public float Peak { get; init; }
        public float ZeroCrossingRate { get; init; }
        public float OnsetStrength { get; init; }
        public float AttackTime { get; init; }
        public float SpectralFlux { get; init; }
        public float SpectralFlatness { get; init; }
        public float StereoBalance { get; init; }
    }
}
