// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Examine;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Popups;
using Content.Shared.PowerCell;
using Robust.Shared.Timing;

namespace Content.Shared._White.Blocking;

public abstract class SharedRechargeableBlockingSystem : EntitySystem
{
    [Dependency] protected readonly IGameTiming GameTiming = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedPowerCellSystem _powerCellSystem = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<RechargeableBlockingComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<RechargeableBlockingComponent, ItemToggleActivateAttemptEvent>(AttemptToggle);
    }

    private void OnExamined(Entity<RechargeableBlockingComponent> entity, ref ExaminedEvent args)
    {
        if (!entity.Comp.Discharged)
        {
            _powerCellSystem.OnBatteryExamined(entity.Comp.CachedChargePercentage, ref args);
            return;
        }

        args.PushMarkup(Loc.GetString("rechargeable-blocking-discharged"));

        var remainingTimeUntilCharged = GameTiming.CurTime - entity.Comp.CachedTimeOfRecharge;
        if (remainingTimeUntilCharged == null)
            return;

        args.PushMarkup(Loc.GetString("rechargeable-blocking-remaining-time", ("remainingTime", remainingTimeUntilCharged.Value.Seconds)));
    }

    private void AttemptToggle(Entity<RechargeableBlockingComponent> entity, ref ItemToggleActivateAttemptEvent args)
    {
        if (!entity.Comp.Discharged)
            return;

        var remainingTimeUntilCharged = GameTiming.CurTime - entity.Comp.CachedTimeOfRecharge;
        if (remainingTimeUntilCharged != null)
            _popup.PopupEntity(Loc.GetString("rechargeable-blocking-remaining-time-popup",
                ("remainingTime", remainingTimeUntilCharged.Value.Seconds)),

        args.User ?? entity);
        args.Cancelled = true;
    }
}
