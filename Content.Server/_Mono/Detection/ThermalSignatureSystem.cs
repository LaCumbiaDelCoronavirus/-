using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._Mono.Detection;
using Content.Shared._Mono.Ships;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Microsoft.Extensions.ObjectPool;
using Robust.Shared.Collections;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using DependencyAttribute = Robust.Shared.IoC.DependencyAttribute;
using Robust.Shared.Timing;

namespace Content.Server._Mono.Detection;

/// <summary>
///     Handles the logic for thermal signatures.
/// </summary>
public sealed class ThermalSignatureSystem : SharedThermalSignatureSystem
{
    [Dependency] private readonly SharedPowerReceiverSystem _power = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private const float UpdateIntervalSeconds = 1f;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(UpdateIntervalSeconds);
    private TimeSpan _nextUpdateTime;

    private const float HeatChangeThreshold = 1.02f;

    private List<Entity<ThermalSignatureComponent>> _gridQueue = new();

    private readonly ObjectPool<Dictionary<Vector2i, float>> _gridMatrixPool =
        new DefaultObjectPool<Dictionary<Vector2i, float>>(new DictPolicy<Vector2i, float>());

    private readonly Stopwatch _stopwatch = new();
    private TimeSpan _updateInterval = TimeSpan.FromSeconds(0.5);
    private TimeSpan _updateAccumulator = TimeSpan.FromSeconds(0);
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<GunComponent> _gunQuery;
    private EntityQuery<MapGridComponent> _mapGridQuery;

    // length of cells in SolveSignatureCollections; map gets higher resolution the lower this is, therefore making this take a generally longer time to process
    // stats: 20 resolution gives ~.061 ms processing time for 55 signatures
    private const float SignatureResolution = 30f;
    private const float SignatureResolutionSq = SignatureResolution * SignatureResolution;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GridInitializeEvent>(OnGridInitialized);

        // some of this could also be handled in shared but there's no point since PVS is a thing
        SubscribeLocalEvent<MachineThermalSignatureComponent, GetThermalSignatureEvent>(OnMachineGetSignature);
        SubscribeLocalEvent<PassiveThermalSignatureComponent, GetThermalSignatureEvent>(OnPassiveGetSignature);

        SubscribeLocalEvent<ThermalSignatureComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<PowerSupplierComponent, GetThermalSignatureEvent>(OnPowerGetSignature);
        SubscribeLocalEvent<ThrusterComponent, GetThermalSignatureEvent>(OnThrusterGetSignature);
        SubscribeLocalEvent<FTLDriveComponent, GetThermalSignatureEvent>(OnFTLGetSignature);

        _gridQuery = GetEntityQuery<MapGridComponent>();
        _gunQuery = GetEntityQuery<GunComponent>();
        _mapGridQuery = GetEntityQuery<MapGridComponent>();
    }

    private void OnGridInitialized(GridInitializeEvent args)
    {
        EnsureComp<ThermalSignatureComponent>(args.EntityUid);
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
    ///     This is separated into another method because "feature `ref and unsafe in async and iterator methods` is not available in C# 12.0."
    ///     TODO: Remove this when on C# 13.0+
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] // AIL is (probably?) fine because this is private and only used once.
    private void ApplyGridEmissions<TEntityValue, TPassedData>(Dictionary<Vector2i, float> grid, in ValueList<(Vector2i Coordinates, float Emission, TEntityValue, TPassedData?)> emissions, out Vector2i? hottestCell, out float lastHottest) where TEntityValue : notnull
    {
        hottestCell = null;
        lastHottest = float.MinValue;

        // go through each grid cell, and for each cell go through every thermal signature and apply it's heat to this one
        foreach (var (gridCoordinates, _) in grid)
        {
            var thisCellSignature = 0f;
            foreach (var (otherCoordinates, signature, _, _) in emissions)
            {
                var heat = ThermalDistantialFalloff(signature, Vector2.DistanceSquared(gridCoordinates, otherCoordinates) * SignatureResolutionSq);
                thisCellSignature += heat;
            }

            Log.Debug($"Grid at {gridCoordinates}, emission at {thisCellSignature}");

            grid[gridCoordinates] = thisCellSignature;
            if (thisCellSignature > lastHottest)
            {
                hottestCell = gridCoordinates;
                lastHottest = thisCellSignature;
            }
        }
    }

    /// <summary>
    ///     Creates a map of thermal signatures. This is used to simulate many, separate thermal signatures
    ///         in a small area emitting alot of heat. Returns the signatures in the hottest cell, and the signature of its cell.
    ///
    ///     Only cells that have thermal signatures in them are modelled.
    /// </summary>
    public IEnumerable<(TEntityValue, float Signature, TPassedData?)> SolveSignatureCollections<TEntityValue, TPassedData>(Dictionary<TEntityValue, (Vector2 Coordinates, float Signature, TPassedData?)> entities) where TEntityValue : notnull
    {
        _stopwatch.Restart();

        // cell position: total heat in that cell
        var grid = _gridMatrixPool.Get();
        var emissions = new ValueList<(Vector2i Coordinates, float Emission, TEntityValue, TPassedData?)>(entities.Count);

        /// curse of 220 foreaches
        // map out emissions, initialise the grid (as we only process cells with signatures in them)
        foreach (var (entityValue, (coordinates, signature, data)) in entities)
        {
            // the cell that this signature is in
            var cellCoordinates = new Vector2i((int)MathF.Floor(coordinates.X / SignatureResolution), (int)MathF.Floor(coordinates.Y / SignatureResolution));
            grid[cellCoordinates] = default;

            emissions.Add((cellCoordinates, signature, entityValue, data));
        }

        // distribute heat across cells
        ApplyGridEmissions(grid, emissions, out var hottestCell, out var lastHottest);
        Log.Debug($"Took {_stopwatch.Elapsed.TotalMilliseconds}ms to process {entities.Count} signatures.");

        if (lastHottest <= float.MinValue || hottestCell == null)
        {
            grid.Clear();
            _gridMatrixPool.Return(grid);

            yield break;
        }

        var hottestCellSignature = grid[hottestCell.Value];
        Log.Debug($"Got hottest cell! At {hottestCell}, with {hottestCellSignature}");
        foreach (var (cellCoordinates, _, entityValue, data) in emissions)
        {
            if (cellCoordinates == hottestCell)
                yield return (entityValue, hottestCellSignature, data);
        }

        grid.Clear();
        _gridMatrixPool.Return(grid);
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextUpdateTime)
            return;

        _nextUpdateTime = _timing.CurTime + UpdateInterval;

        var gridQuery = EntityQueryEnumerator<MapGridComponent, ThermalSignatureComponent>();
        while (gridQuery.MoveNext(out _, out _, out var gridSigComp))
        {
            gridSigComp.TotalHeat = 0f;
        }

        _gridQueue.Clear();
        var query = EntityQueryEnumerator<ThermalSignatureComponent>();
        while (query.MoveNext(out var uid, out var sigComp))
        {
            var ev = new GetThermalSignatureEvent();
            RaiseLocalEvent(uid, ref ev);

            sigComp.StoredHeat += ev.Signature * UpdateIntervalSeconds;
            sigComp.StoredHeat *= MathF.Pow(sigComp.HeatDissipation, UpdateIntervalSeconds);

            if (_mapGridQuery.HasComp(uid))
            {
                _gridQueue.Add((uid, sigComp));
                continue;
            }
            else
            {
                var xform = Transform(uid);
                sigComp.TotalHeat = sigComp.StoredHeat;
                if (xform.GridUid != null && SigQuery.TryGetComponent(xform.GridUid.Value, out var gridSig))
                    gridSig.TotalHeat += sigComp.StoredHeat;
            }
        }

        foreach (var ent in _gridQueue)
        {
            ent.Comp.TotalHeat += ent.Comp.StoredHeat;

            // don't sync it if it didn't change heat much since last time, we don't need to sync 500 cold asteroids every system update
            if (ent.Comp.TotalHeat <= ent.Comp.LastUpdateHeat * HeatChangeThreshold
                && ent.Comp.TotalHeat >= ent.Comp.LastUpdateHeat / HeatChangeThreshold)
                continue;

            ent.Comp.LastUpdateHeat = ent.Comp.TotalHeat;
            Dirty(ent);
        }
    }
}
