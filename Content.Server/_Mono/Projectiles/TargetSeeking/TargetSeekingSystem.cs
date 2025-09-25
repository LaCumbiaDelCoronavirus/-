// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 Ilya246
// SPDX-FileCopyrightText: 2025 Redrover1760
// SPDX-FileCopyrightText: 2025 RikuTheKiller
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel.DataAnnotations;
using System.Numerics;
using Content.Shared.Interaction;
using Content.Server.Shuttles.Components;
using Content.Shared.Projectiles;
using Robust.Server.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;
using Content.Server._Mono.Detection;
using JetBrains.Annotations;
using Content.Shared._Mono.Detection;
using Robust.Shared.Map;
using System.Linq;

namespace Content.Server._Mono.Projectiles.TargetSeeking;

/// <summary>
/// Handles the logic for target-seeking projectiles.
/// </summary>
public sealed class TargetSeekingSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = null!;
    [Dependency] private readonly RotateToFaceSystem _rotateToFace = null!;
    [Dependency] private readonly PhysicsSystem _physics = null!;
    [Dependency] private readonly ThermalSignatureSystem _thermalSignatureSystem = null!;
    [Dependency] private readonly IGameTiming _gameTiming = default!; // Mono

    /// <summary>
    /// Minimum amount of time that must pass since the last attempt to acquire a target,
    /// before trying to acquire a target again.
    /// </summary>
    private static readonly TimeSpan SeekerTargetAcquisitionInterval = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// How many potential targets can we have in <see cref="AcquireTarget"/> before
    /// stopping looking for any new ones? 
    /// </summary>
    private const int MaximumPotentialTargets = 120;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TargetSeekingComponent, ProjectileHitEvent>(OnProjectileHit);
        SubscribeLocalEvent<TargetSeekingComponent, EntParentChangedMessage>(OnParentChanged);
    }

    /// <summary>
    /// Called when a target-seeking projectile hits something.
    /// </summary>
    private void OnProjectileHit(EntityUid uid, TargetSeekingComponent component, ref ProjectileHitEvent args)
    {
        // If we hit our actual target, we could perform additional effects here
        if (component.CurrentTarget.HasValue && component.CurrentTarget.Value == args.Target)
        {
            // Target hit successfully
        }

        // Reset the target since we've hit something
        component.CurrentTarget = null;
    }

    /// <summary>
    /// Called when a target-seeking projectile changes parent (e.g., enters a grid).
    /// </summary>
    private void OnParentChanged(EntityUid uid, TargetSeekingComponent component, EntParentChangedMessage args)
    {
        // Check if the projectile has entered a grid
        if (args.Transform.GridUid == null)
            return;

        // Get the shooter's grid to compare
        if (!TryComp<ProjectileComponent>(uid, out var projectile) ||
            !TryComp<TransformComponent>(projectile.Shooter, out var shooterTransform))
            return;

        var shooterGridUid = shooterTransform.GridUid;
        var currentGridUid = args.Transform.GridUid;

        // If we've entered a different grid than the shooter's grid, disable seeking
        if (currentGridUid != shooterGridUid)
        {
            component.SeekingDisabled = true;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var ticktime = _gameTiming.TickPeriod;

        var query = EntityQueryEnumerator<TargetSeekingComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var seekingComp, out var body, out var xform))
        {
            var acceleration = seekingComp.Acceleration * frameTime;
            // Initialize launch speed.
            if (seekingComp.Launched == false)
            {
                acceleration += seekingComp.LaunchSpeed;
                seekingComp.Launched = true;
            }

            // Apply acceleration in the direction the projectile is facing
            _physics.SetLinearVelocity(uid, body.LinearVelocity + _transform.GetWorldRotation(xform).ToWorldVec() * acceleration, body: body);

            // Damping applied for missiles above max speed.
            if (body.LinearVelocity.Length() > seekingComp.MaxSpeed)
                _physics.SetLinearDamping(uid, body, seekingComp.Acceleration * (float)ticktime.TotalSeconds * 1.5f);
            else
            {
                _physics.SetLinearDamping(uid, body, 0f);
            }

            // Skip seeking behavior if disabled (e.g., after entering an enemy grid)
            if (seekingComp.SeekingDisabled)
                continue;

            if (seekingComp.TrackDelay > 0f)
            {
                seekingComp.TrackDelay -= frameTime;
                continue;
            }

            // Whether we should try and look for a new target.
            // If we can lose our target, then always try to find a new one.
            var tryGetTarget = seekingComp.CanLoseTarget;

            // If we have a target, track it using the selected algorithm
            if (seekingComp.CurrentTarget.HasValue && !TerminatingOrDeleted(seekingComp.CurrentTarget))
            {
                var target = seekingComp.CurrentTarget.Value;
                var targetXform = Transform(target);
                var wantAngle = new Angle(0);

                var ourEnt = (uid, seekingComp, xform);
                var targEnt = (target, targetXform);

                switch (seekingComp.TrackingAlgorithm)
                {
                    case TrackingMethod.Direct:
                        wantAngle = ApplyDirectTracking(ourEnt, targEnt, frameTime); break;
                    case TrackingMethod.Predictive:
                        wantAngle = ApplyPredictiveTracking(ourEnt, targEnt, frameTime); break;
                    case TrackingMethod.AdvancedPredictive:
                        wantAngle = ApplyAdvancedTracking(ourEnt, targEnt, frameTime); break;
                }

                _rotateToFace.TryRotateTo(
                    uid,
                    wantAngle,
                    frameTime,
                    seekingComp.Tolerance,
                    seekingComp.TurnRate?.Theta ?? MathF.PI * 2,
                    xform
                );
            }
            else // if we don't have a target, try and get one
                tryGetTarget = true;

            if (tryGetTarget &&
                _gameTiming.CurTime > seekingComp.NextTargetAcquisitionAttempt)
            {
                // Its time to find and kill
                AcquireTarget(uid, seekingComp, xform);
                seekingComp.NextTargetAcquisitionAttempt = _gameTiming.CurTime + SeekerTargetAcquisitionInterval;
            }
        }
    }

    /// <summary>
    /// Returns a score for how 'attractive' a target is to a target-seeker, depending on thermal signature and distance.
    /// </summary>
    [Pure]
    private float GetTargetInfluence(in float thermalSignature, in float distance, in float power)
        => thermalSignature / (MathF.Pow(distance, power) + float.Epsilon);

    /// <summary>
    /// Finds the most optimal valid target within range and tracking parameters.
    /// </summary>
    public void AcquireTarget(EntityUid uid, TargetSeekingComponent component, TransformComponent transform)
    {
        // this method is a tutorial on how to troll the gc
        /*
        Normally, the required threshold for a target to be considered is our ThermalSignatureThreshold.

        However, if we can change targets and have to compare thermalsig, the new target must have a higher
        signature.

        TODO: give every owned grid TargetSeekable instead of relying on shuttleconsoles?
        */

        // Are we allowed to replace our current target with a new one?
        var canRealisticallyLoseTarget = component.CurrentTarget.HasValue;

        // Minimum required cellsig.
        var minimumRequiredSignature = canRealisticallyLoseTarget && component.TargetingComparesThermalSignature ?
            MathF.Max(component.ThermalSignatureThreshold, _thermalSignatureSystem.GetSignature(component.CurrentTarget!.Value)) : component.ThermalSignatureThreshold;

        if (minimumRequiredSignature < SharedThermalSignatureSystem.SignatureZeroEpsilon)
            minimumRequiredSignature = SharedThermalSignatureSystem.SignatureZeroEpsilon;

        // entity: heat-signature+position
        var validSignatureEntities = new Dictionary<EntityUid, (MapCoordinates, float)>();

        var targetQuery = EntityQueryEnumerator<TargetSeekableComponent, TransformComponent>();
        var sourcePos = _transform.ToMapCoordinates(transform.Coordinates).Position;

        var detectionRangeSquared = component.DetectionRange * component.DetectionRange;

        // The uid of the entity that shot this missile/target-seeker.
        EntityUid? shooterUid = null;
        if (TryComp<ProjectileComponent>(uid, out var projectile) &&
            TryComp(projectile.Shooter, out TransformComponent? shooterTransform))
            shooterUid = shooterTransform.GridUid ?? projectile.Shooter;

        while (targetQuery.MoveNext(out var targetUid, out _, out var targetTransformComponent))
        {
            if (targetTransformComponent.MapID != transform.MapID)
                continue;

            /*
            The body can either be a seekable target, or it's grid.
            If the target has a grid, use that as the body.

            Ignore this body if it's our current target, or is the body that launched us.
            */

            var bodyUid = targetTransformComponent.GridUid ?? targetUid;
            if (bodyUid == component.CurrentTarget || // dont target our current target
                bodyUid == shooterUid || // dont target ourself
                validSignatureEntities.ContainsKey(bodyUid))
                continue;

            var bodySignature = _thermalSignatureSystem.GetSignature(bodyUid);
            if (bodySignature < minimumRequiredSignature)
                continue;

            var targetMapCoordinates = _transform.GetMapCoordinates(targetTransformComponent);
            // out of sight, out of mind
            if (Vector2.DistanceSquared(targetMapCoordinates.Position, sourcePos) > detectionRangeSquared)
                continue;

            validSignatureEntities[bodyUid] = (targetMapCoordinates, bodySignature);

            // just stop
            if (validSignatureEntities.Count > MaximumPotentialTargets)
                break;
        }

        if (validSignatureEntities.Count == 0)
            return;

        // get the strongest-signature group of entities
        var bestSignatures = _thermalSignatureSystem.SolveSignatureCollections(validSignatureEntities);

        var bestScore = component.CurrentTargetScore ?? float.MinValue;
        EntityUid? bestTarget = component.CurrentTarget ?? null;

        // Look for things to target
        foreach (var (targetUid, targetSignature) in bestSignatures)
        {
            /// continue if the target doesn't have high enough of a thermal signature
            if (!TryComp(targetUid, out TransformComponent? targetXform))
                continue;

            // Get angle to the target
            var targetPos = _transform.ToMapCoordinates(targetXform.Coordinates).Position;
            var angleToTarget = (targetPos - sourcePos).ToWorldAngle();

            // we can't update if this target is worse than the last best score
            var targetScore = GetTargetInfluence(targetSignature, Vector2.Distance(sourcePos, targetPos), component.TargetDistanceScoringPower);
            if (targetScore < bestScore)
                continue;

            // Get current direction of the projectile
            var currentRotation = _transform.GetWorldRotation(transform);

            // Check if target is within field of view
            var angleDifference = Angle.ShortestDistance(currentRotation, angleToTarget).Degrees;
            if (MathF.Abs((float)angleDifference) > component.ScanArc / 2)
            {
                continue; // Target is outside our field of view
            }

            bestScore = targetScore;
            bestTarget = targetUid;
        }

        // Set our new target
        if (bestTarget.HasValue)
        {
            component.CurrentTargetScore = bestScore;
            component.CurrentTarget = bestTarget;

            Log.Debug($"Locked onto a target! {ToPrettyString(bestTarget.Value)}");
        }
    }

    /// <summary>
    /// Advanced tracking that predicts where the target will be based on its velocity.
    /// </summary>
    public Angle ApplyPredictiveTracking(Entity<TargetSeekingComponent, TransformComponent> ent, Entity<TransformComponent> target, float frameTime)
    {
        if (!TryComp<PhysicsComponent>(target, out var targetBody) || !TryComp<PhysicsComponent>(ent, out var body))
            return new Angle(0);

        // Get current positions
        var currentTargetPosition = _transform.GetWorldPosition(target.Comp);
        var sourcePosition = _transform.GetWorldPosition(ent.Comp2);

        // Calculate current distance
        var toTargetVec = currentTargetPosition - sourcePosition;
        var currentDistance = toTargetVec.Length();

        var targetVelocity = _physics.GetMapLinearVelocity(target, targetBody, target.Comp);
        var ourVelocity = _physics.GetMapLinearVelocity(ent, body, ent.Comp2);
        var relVel = ourVelocity - targetVelocity;

        // Calculate time to intercept (using closing rate)
        var closingRate = Vector2.Dot(relVel, toTargetVec) / toTargetVec.Length();
        var timeToIntercept = currentDistance / closingRate;

        // Prevent negative or very small intercept times that could cause erratic behavior
        timeToIntercept = MathF.Max(timeToIntercept, 0.1f);

        // Predict where the target will be when we reach it
        var predictedPosition = currentTargetPosition + (targetVelocity * timeToIntercept);

        // Calculate angle to the predicted position
        var targetAngle = (predictedPosition - sourcePosition).ToWorldAngle();

        return targetAngle;
    }

    /// <summary>
    /// More advanced and accurate tracking.
    /// Works best for missiles with low friction and high max speed, where they spend all or most of their lifetime accelerating and being under max speed.
    /// </summary>
    // see: https://github.com/Ilya246/orbitfight/blob/master/src/entities.cpp for original
    public Angle ApplyAdvancedTracking(Entity<TargetSeekingComponent, TransformComponent> ent, Entity<TransformComponent> target, float frameTime)
    {
        if (!TryComp<PhysicsComponent>(target, out var targetBody) || !TryComp<PhysicsComponent>(ent, out var body))
            return new Angle(0);

        const int guidanceIterations = 3;

        var accel = ent.Comp1.Acceleration;

        var ownVel = _physics.GetMapLinearVelocity(ent);
        var ownPos = _transform.GetWorldPosition(ent.Comp2);
        var targetVel = _physics.GetMapLinearVelocity(target);
        var targetPos = _transform.GetWorldPosition(target.Comp);
        var relVel = targetVel - ownVel;
        var relPos = targetPos - ownPos;

        var dVx = relVel.X;
        var dVy = relVel.Y;
        var dX = relPos.X;
        var dY = relPos.Y;
        var refRot = MathF.Atan2(dVy, dVx);
        var vel = dVx / MathF.Cos(refRot);
        var projX = dX * MathF.Cos(refRot) + dY * MathF.Sin(refRot);
        var projY = dY * MathF.Cos(refRot) - dX * MathF.Sin(refRot);
        var itime = GuessInterceptTime(0f, -projX, -vel, projY, accel);
        for (var i = 0; i < guidanceIterations; i++)
            itime = GuessInterceptTime(itime, -projX, -vel, projY, accel);

        var targetRot = (relPos + relVel * itime).ToWorldAngle();

        return targetRot;

        // the explanation for how this works would take more space than the enclosing method so it's not included here
        float GuessInterceptTime(float prev, float x0, float vel, float y0, float accel)
        {
            var x = x0 + vel * prev;
            var d = MathF.Sqrt(x * x + y0 * y0);
            var dd = vel * x / d;
            return (dd + MathF.Sqrt(dd * dd + 2f * accel * (d - dd * prev))) / (accel);
        }
    }

    /// <summary>
    /// Basic tracking that points directly at the current target position.
    /// </summary>
    public Angle ApplyDirectTracking(Entity<TargetSeekingComponent, TransformComponent> ent, Entity<TransformComponent> target, float frameTime)
    {
        // Get the angle directly toward the target
        var angleToTarget = (_transform.GetWorldPosition(target.Comp) - _transform.GetWorldPosition(ent.Comp2)).ToWorldAngle();

        return angleToTarget;
    }
}
