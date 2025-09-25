
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;

namespace Content.Shared._Mono.Detection;

/// <summary>
///     Contains helper methods for working with thermal signatures.
/// </summary>
// virtual entsys REAL?
[Virtual]
public class SharedThermalSignatureSystem : EntitySystem
{
    protected EntityQuery<ThermalSignatureComponent> SigQuery;

    /// <summary>
    ///     Minimum distance between a thermal signature's total heat and zero,
    ///         before it may not be considered in some calculations.
    /// </summary>
    public const float SignatureZeroEpsilon = 0.5f;

    public override void Initialize()
    {
        base.Initialize();
        Log.Debug("Initializing Shared Termal Sinature Sytem");
        SigQuery = GetEntityQuery<ThermalSignatureComponent>();
    }

    /// <summary>
    ///     Returns a thermal signature's strength at a distance.
    /// </summary>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static float ThermalDistantialFalloff(in float signature, in float distanceSq)
    {
        var maxDistanceSq = signature; // sqrt(signature) is the real max radius of a thermalsig
        return distanceSq < maxDistanceSq && distanceSq > float.Epsilon ?
            signature / distanceSq : // ISL
            0f;
    }

    /// <summary>
    ///     Returns the cached heat signature of an entity if it has <see cref="ThermalSignatureComponent"/>
    ///         Otherwise, returns 0.
    /// </summary>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetSignature(in Entity<ThermalSignatureComponent?> entity)
    {
        // resolvevil
        if (entity.Comp is { } signatureComponent || SigQuery.TryGetComponent(entity, out signatureComponent))
            return signatureComponent.TotalHeat;

        return 0f;
    }

    /// <summary>
    ///     Tries to resolve <see cref="ThermalSignatureComponent"/> and get it's heat signature, on the given entity. 
    /// </summary>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ResolveSignature(in EntityUid uid, [NotNullWhen(true)] ref ThermalSignatureComponent? signatureComponent, out float signature)
    {
        if (signatureComponent != null || SigQuery.TryGetComponent(uid, out signatureComponent))
        {
            signature = signatureComponent.TotalHeat;
            return true;
        }

        signature = 0f;
        return false;
    }
}