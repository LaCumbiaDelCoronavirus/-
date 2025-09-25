// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 Ilya246
// SPDX-FileCopyrightText: 2025 Redrover1760
// SPDX-FileCopyrightText: 2025 RikuTheKiller
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Map;

namespace Content.Server._Mono.Projectiles.TargetSeeking;

/// <summary>
/// Component that allows a projectile to seek and track thermal targets autonomously.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class TargetSeekingComponent : Component
{
    /// <summary>
    /// The next time this seeker can look for a target.
    /// </summary>
    // The reason this is kept on component, and not a global update over all target-seekers,
    // is because target-seekers are rather time sensitive. This way, it can update immediately
    // when launching and still keep consistent with the cooldown.
    [DataField, AutoPausedField]
    public TimeSpan NextTargetAcquisitionAttempt = TimeSpan.MinValue;

    /// <summary>
    /// The influence of targets is decided by this equation: Q/d^this;
    /// meaning, the higher this is, closer targets will be prioritised more than hotter targets.
    /// </summary>
    [DataField]
    public float TargetDistanceScoringPower = 0.4f;

    /// <summary>
    /// Minimum thermal signature that a potential target may have.
    /// </summary>
    // shouldn't be 0, keep it a low and reasonable value to avoid doing excess calculations on a ton of things
    [DataField]
    public float ThermalSignatureThreshold = 224.9f; // mob's passive thermal signature is 225

    /// <summary>
    /// Maximum distance to search for potential targets.
    /// </summary>
    [DataField]
    public float DetectionRange = 300f;

    /// <summary>
    /// Minimum angular deviation from directly facing the target.
    /// </summary>
    [DataField]
    public Angle Tolerance = Angle.FromDegrees(1);

    /// <summary>
    /// How quickly the projectile can change direction in degrees per second.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public Angle? TurnRate = 100f;

    /// <summary>
    /// The score of the current target being seeked, as in from TargetSeekingSystem.GetTargetInfluence.
    /// </summary>
    [DataField]
    public float? CurrentTargetScore;

    /// <summary>
    /// The current target entity being tracked, and positional offset from it.
    /// </summary>
    [DataField]
    public EntityUid? CurrentTarget;

    /// <summary>
    /// While tracking a target, is the missile allowed to switch to a different target?
    /// </summary>
    [DataField]
    public bool CanLoseTarget = true;

    /// <summary>
    /// Incase of <see cref="CanLoseTarget"/>, must the new target have a higher thermal signature
    /// than the current target, to be considered a potential new target? 
    /// </summary>
    [DataField]
    public bool TargetingComparesThermalSignature = false;

    /// <summary>
    /// Tracking algorithm used for intercepting the target.
    /// </summary>
    [DataField]
    public TrackingMethod TrackingAlgorithm = TrackingMethod.AdvancedPredictive;

    /// <summary>
    /// How fast the projectile accelerates in m/s².
    /// </summary>
    [DataField]
    public float Acceleration = 50f;

    /// <summary>
    /// Maximum speed the projectile can reach in m/s.
    /// </summary>
    [DataField]
    public float MaxSpeed = 50f;

    /// <summary>
    /// Initial speed of the projectile in m/s.
    /// </summary>
    [DataField]
    public float LaunchSpeed = 10f;

    /// <summary>
    /// Has the projectile had its launch speed applied yet?
    /// </summary>
    [DataField]
    public bool Launched = false;

    /// <summary>
    /// The amount of time in seconds left the missile starts searching for targets. // Mono
    /// </summary>
    [DataField]
    public float TrackDelay = 0f;

    /// <summary>
    /// Field of view in degrees for target detection.
    /// </summary>
    [DataField]
    public float ScanArc = 90f;

    /// <summary>
    /// Whether seeking has been disabled (e.g., after entering an enemy grid).
    /// </summary>
    public bool SeekingDisabled;
}

/// <summary>
/// Defines different tracking algorithms that can be used.
/// </summary>
[Serializable]
public enum TrackingMethod
{
    /// <summary>
    /// Basic tracking that simply points directly at the target.
    /// </summary>
    Direct = 1,

    /// <summary>
    /// Advanced tracking that predicts target movement.
    /// </summary>
    Predictive = 2,

    /// <summary>
    /// Even more accurate tracking.
    /// </summary>
    AdvancedPredictive = 3
}
