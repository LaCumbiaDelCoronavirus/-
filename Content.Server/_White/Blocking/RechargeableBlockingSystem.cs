// SPDX-FileCopyrightText: 2024 Aviu00
// SPDX-FileCopyrightText: 2025 Redrover1760
// SPDX-FileCopyrightText: 2025 starch
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._White.Blocking;
using Content.Shared.Damage;
using Content.Shared.Item.ItemToggle;
using Content.Shared.PowerCell.Components;

namespace Content.Server._White.Blocking;

public sealed class RechargeableBlockingSystem : SharedRechargeableBlockingSystem
{
    [Dependency] private readonly BatterySystem _battery = default!;
    [Dependency] private readonly ItemToggleSystem _itemToggleSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RechargeableBlockingComponent, ChargeChangedEvent>(OnChargeChanged);
        SubscribeLocalEvent<RechargeableBlockingComponent, PowerCellChangedEvent>(OnPowerCellChanged);
        SubscribeLocalEvent<RechargeableBlockingComponent, DamageChangedEvent>(OnDamageChanged);
    }

    /// <summary>
    ///     Tries to update the given entity's <see cref="RechargeableBlockingComponent.TimeUntilRecharge"/>.
    ///         Dirties the associated field.
    /// </summary>
    /// <returns>True if successfully updated.</returns>
    private bool UpdateTimeUntilRecharge(in Entity<RechargeableBlockingComponent> rechargeableBlockingEntity)
    {
        if (!_battery.TryGetBatteryComponent(rechargeableBlockingEntity, out var batteryComponent, out var batteryUid)
            || !TryComp<BatterySelfRechargerComponent>(batteryUid, out var rechargerComponent)
            || rechargerComponent is not { AutoRechargeRate: > 0, AutoRecharge: true })
            return false;

        var remainingTimeUntilCharged = (batteryComponent.MaxCharge - batteryComponent.CurrentCharge) / rechargerComponent.AutoRechargeRate;

        rechargeableBlockingEntity.Comp.CachedTimeOfRecharge = GameTiming.CurTime + TimeSpan.FromSeconds(remainingTimeUntilCharged);
        DirtyField(rechargeableBlockingEntity, rechargeableBlockingEntity.Comp, nameof(rechargeableBlockingEntity.Comp.CachedTimeOfRecharge));

        return true;
    }

    private void OnChargeChanged(Entity<RechargeableBlockingComponent> rechargeableBlockingEntity, ref ChargeChangedEvent args)
    {
        UpdateCharge(rechargeableBlockingEntity);
    }

    private void OnPowerCellChanged(Entity<RechargeableBlockingComponent> rechargeableBlockingEntity, ref PowerCellChangedEvent args)
    {
        UpdateCharge(rechargeableBlockingEntity);
    }

    private void OnDamageChanged(EntityUid uid, RechargeableBlockingComponent component, DamageChangedEvent args)
    {
        if (!_battery.TryGetBatteryComponent(uid, out var batteryComponent, out var batteryUid)
            || !_itemToggleSystem.IsActivated(uid)
            || args.DamageDelta == null)
            return;

        var batteryUse = Math.Min(args.DamageDelta.GetTotal().Float(), batteryComponent.CurrentCharge);
        _battery.TryUseCharge(batteryUid.Value, batteryUse, batteryComponent);
    }

    private void UpdateCharge(in Entity<RechargeableBlockingComponent> rechargeableBlockingEntity)
    {
        var (uid, rechargeableBlockingComponent) = rechargeableBlockingEntity;

        if (!_battery.TryGetBatteryComponent(uid, out var batteryComponent, out _))
            return;

        // Battery-percentage
        var roundedBatteryPercentageHundred = (int)((batteryComponent.CurrentCharge - batteryComponent.MaxCharge) * 100);
        if (roundedBatteryPercentageHundred != rechargeableBlockingComponent.CachedChargePercentage)
        {
            rechargeableBlockingComponent.CachedChargePercentage = roundedBatteryPercentageHundred;
            DirtyField(rechargeableBlockingEntity, rechargeableBlockingComponent, nameof(rechargeableBlockingComponent.CachedChargePercentage));
        }

        UpdateTimeUntilRecharge(rechargeableBlockingEntity);

        // Charge-rate, discharged-or-not
        /*
            If charge is at 0, the battery is considered discharged
                until charge is at maximum again.

            If charge is at the maximum, only then is the battery not
                considered discharged anymore, until charge is at 0 again.
        */

        float? rechargeRate = null;
        if (MathHelper.CloseTo(batteryComponent.CurrentCharge, 0f))
        {
            rechargeableBlockingComponent.Discharged = true;
            rechargeRate = rechargeableBlockingComponent.DischargedRechargeRate;

            _itemToggleSystem.TryDeactivate(uid, predicted: false);
            DirtyField(rechargeableBlockingEntity, rechargeableBlockingComponent, nameof(rechargeableBlockingComponent.Discharged));
        }
        else if (MathHelper.CloseTo(batteryComponent.CurrentCharge, batteryComponent.MaxCharge))
        {
            rechargeableBlockingComponent.Discharged = false;
            rechargeRate = rechargeableBlockingComponent.ChargedRechargeRate;

            DirtyField(rechargeableBlockingEntity, rechargeableBlockingComponent, nameof(rechargeableBlockingComponent.Discharged));
        }

        if (rechargeRate != null && TryComp<BatterySelfRechargerComponent>(uid, out var recharger))
            recharger.AutoRechargeRate = rechargeRate.Value;
    }
}
