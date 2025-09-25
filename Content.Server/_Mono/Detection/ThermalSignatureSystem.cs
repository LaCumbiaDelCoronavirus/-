// SPDX-FileCopyrightText: 2025 Ilya246
//
// SPDX-License-Identifier: MPL-2.0

using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.Detection;
using Content.Shared._Mono.Ships;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DependencyAttribute = Robust.Shared.IoC.DependencyAttribute;

namespace Content.Server._Mono.Detection;

/// <summary>
///     Handles the logic for thermal signatures.
/// </summary>
public sealed class ThermalSignatureSystem : SharedThermalSignatureSystem
{
    [Dependency] private readonly SharedPowerReceiverSystem _power = default!;
    [Dependency] private readonly TransformSystem _transformSystem = default!;

    private readonly Stopwatch _stopwatch = new();
    private TimeSpan _updateInterval = TimeSpan.FromSeconds(0.5);
    private TimeSpan _updateAccumulator = TimeSpan.FromSeconds(0);
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<GunComponent> _gunQuery;

    // length of cells in SolveSignatureCollections; map gets higher resolution the lower this is, therefore making this take a generally longer time to process
    // stats: 20 resolution gives ~.61 ms processing time for 55 signatures
    private const float SignatureResolution = 20f;
    private const float SignatureResolutionSq = SignatureResolution * SignatureResolution;

    public override void Initialize()
    {
        base.Initialize();

        // some of this could also be handled in shared but there's no point since PVS is a thing
        SubscribeLocalEvent<MachineThermalSignatureComponent, GetThermalSignatureEvent>(OnMachineGetSignature);
        SubscribeLocalEvent<PassiveThermalSignatureComponent, GetThermalSignatureEvent>(OnPassiveGetSignature);

        SubscribeLocalEvent<ThermalSignatureComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<PowerSupplierComponent, GetThermalSignatureEvent>(OnPowerGetSignature);
        SubscribeLocalEvent<ThrusterComponent, GetThermalSignatureEvent>(OnThrusterGetSignature);
        SubscribeLocalEvent<FTLDriveComponent, GetThermalSignatureEvent>(OnFTLGetSignature);

        _gridQuery = GetEntityQuery<MapGridComponent>();
        _gunQuery = GetEntityQuery<GunComponent>();
    }

    private void OnGunShot(Entity<ThermalSignatureComponent> ent, ref GunShotEvent args)
    {
        if (_gunQuery.TryComp(ent, out var gun))
            ent.Comp.StoredHeat += gun.ShootThermalSignature;
    }

    private void OnMachineGetSignature(Entity<MachineThermalSignatureComponent> ent, ref GetThermalSignatureEvent args)
    {
        if (_power.IsPowered(ent.Owner))
            args.Signature += ent.Comp.Signature;
    }

    private void OnPassiveGetSignature(Entity<PassiveThermalSignatureComponent> ent, ref GetThermalSignatureEvent args)
    {
        args.Signature += ent.Comp.Signature;
    }

    private void OnPowerGetSignature(Entity<PowerSupplierComponent> ent, ref GetThermalSignatureEvent args)
    {
        args.Signature += ent.Comp.CurrentSupply * ent.Comp.HeatSignatureRatio;
    }

    private void OnThrusterGetSignature(Entity<ThrusterComponent> ent, ref GetThermalSignatureEvent args)
    {
        if (ent.Comp.Firing)
            args.Signature += ent.Comp.Thrust * ent.Comp.HeatSignatureRatio;
    }

    private void OnFTLGetSignature(Entity<FTLDriveComponent> ent, ref GetThermalSignatureEvent args)
    {
        var xform = Transform(ent);
        if (!TryComp<FTLComponent>(xform.GridUid, out var ftl))
            return;

        if (ftl.State == FTLState.Starting || ftl.State == FTLState.Cooldown)
            args.Signature += ent.Comp.ThermalSignature;
    }

    /// <summary>
    ///     Applys the heat of thermal signatures to surrounding cells where possible.
    /// 
    ///     This is done because "feature `ref and unsafe in async and iterator methods` is not available in C# 12.0."
    ///     TODO: Remove this when on C# 13.0+
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] // AIL is (probably?) fine because this is private and only used once.
    private static void ApplyGridEmissions(Dictionary<Vector2i, float> grid, List<(Vector2i Coordinates, float Emission, EntityUid)> emissions, out Vector2i? hottestCell, out float lastHottest)
    {
        hottestCell = null;
        lastHottest = float.MinValue;

        // go through each grid cell, and for each cell go through every thermal signature and apply it's heat to this one
        foreach (var (gridCoordinates, _) in grid)
        {
            var thisHeat = 0f;
            ref var thisCellSignature = ref CollectionsMarshal.GetValueRefOrNullRef(grid, gridCoordinates);

            foreach (var (otherCoordinates, signature, _) in emissions)
            {
                var heat = ThermalDistantialFalloff(signature, Vector2.DistanceSquared(gridCoordinates, otherCoordinates) * SignatureResolutionSq);

                thisCellSignature += signature;
                thisHeat += heat;
            }

            if (thisHeat > lastHottest)
            {
                lastHottest = thisHeat;
                hottestCell = gridCoordinates;
            }
        }
    }

    /// <summary>
    ///     Creates a map of thermal signatures. This is used to simulate many, separate thermal signatures
    ///         in a small area emitting alot of heat. Returns the signatures in the hottest cell, and the signature of its cell.
    /// 
    ///     Only cells that have thermal signatures in them are modelled.
    /// </summary>
    public IEnumerable<(EntityUid, float Signature)> SolveSignatureCollections(Dictionary<EntityUid, (MapCoordinates Coordinates, float Signature)> entities)
    {
        _stopwatch.Restart();

        // cell position: total heat in that cell
        var grid = new Dictionary<Vector2i, float>();
        // list of every emission with the cell it's in, with it's cell coordinates
        var emissions = new List<(Vector2i Coordinates, float Emission, EntityUid)>(entities.Count);

        /// curse of 220 foreaches
        // map out emissions, initialise the grid (as we only process cells with signatures in them)
        foreach (var (uid, (coordinates, signature)) in entities)
        {
            // the cell that this signature is in
            var cellCoordinates = new Vector2i((int)MathF.Floor(coordinates.X / SignatureResolution), (int)MathF.Floor(coordinates.Y / SignatureResolution));
            grid[cellCoordinates] = default;

            emissions.Add((cellCoordinates, signature, uid));
        }

        // distribute heat across cells
        ApplyGridEmissions(grid, emissions, out var hottestCell, out var lastHottest);

        if (lastHottest <= float.MinValue || hottestCell == null)
            yield break;

        var hottestCellSignature = grid[hottestCell.Value];
        foreach (var (cellCoordinates, _, uid) in emissions)
        {
            if (cellCoordinates == hottestCell)
                yield return (uid, hottestCellSignature);
        }

        Log.Debug($"Took {_stopwatch.Elapsed.TotalMilliseconds}ms to process {entities.Count} signatures.");
    }

    public override void Update(float frameTime)
    {
        _updateAccumulator += TimeSpan.FromSeconds(frameTime);
        if (_updateAccumulator < _updateInterval)
            return;
        _updateAccumulator -= _updateInterval;

        var interval = (float)_updateInterval.TotalSeconds;

        var gridQuery = EntityQueryEnumerator<MapGridComponent>();
        while (gridQuery.MoveNext(out var uid, out _))
        {
            var sigComp = EnsureComp<ThermalSignatureComponent>(uid);
            sigComp.TotalHeat = 0f;
        }

        var query = EntityQueryEnumerator<ThermalSignatureComponent>();
        while (query.MoveNext(out var uid, out var sigComp))
        {
            var ev = new GetThermalSignatureEvent(interval);
            RaiseLocalEvent(uid, ref ev);
            sigComp.StoredHeat += ev.Signature * interval;
            sigComp.StoredHeat *= MathF.Pow(sigComp.HeatDissipation, interval);
            if (_gridQuery.HasComp(uid))
            {
                sigComp.TotalHeat += sigComp.StoredHeat;
            }
            else
            {
                var xform = Transform(uid);
                sigComp.TotalHeat = sigComp.StoredHeat;
                if (xform.GridUid != null && SigQuery.TryComp(xform.GridUid, out var gridSig))
                    gridSig.TotalHeat += sigComp.StoredHeat;
            }
        }

        var gridQuery2 = EntityQueryEnumerator<MapGridComponent, ThermalSignatureComponent>();
        while (gridQuery2.MoveNext(out var uid, out _, out var sigComp))
        {
            Dirty(uid, sigComp); // sync to client
        }
    }
}