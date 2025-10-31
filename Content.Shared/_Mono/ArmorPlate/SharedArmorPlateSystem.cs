// SPDX-FileCopyrightText: 2025 ark1368
//
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Destructible;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Inventory;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Storage;
using Robust.Shared.Containers;

namespace Content.Shared._Mono.ArmorPlate;

/// <summary>
/// Handles armor plate insertion, removal, and speed modifier application.
/// </summary>
public abstract class SharedArmorPlateSystem : EntitySystem
{
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeed = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly StaminaSystem _stamina = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    private EntityQuery<ArmorPlateHolderComponent> _plateHolderQuery;
    private EntityQuery<ArmorPlateItemComponent> _plateItemQuery;

    public override void Initialize()
    {
        base.Initialize();

        _plateHolderQuery = GetEntityQuery<ArmorPlateHolderComponent>();
        _plateItemQuery = GetEntityQuery<ArmorPlateItemComponent>();

        SubscribeLocalEvent<ArmorPlateHolderComponent, EntInsertedIntoContainerMessage>(OnPlateInserted);
        SubscribeLocalEvent<ArmorPlateHolderComponent, EntRemovedFromContainerMessage>(OnPlateRemoved);
        SubscribeLocalEvent<ArmorPlateHolderComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<ArmorPlateHolderComponent, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent>>(OnRefreshMoveSpeed);

        SubscribeLocalEvent<InventoryComponent, BeforeDamageChangedEvent>(OnBeforeDamageChanged);
        SubscribeLocalEvent<ArmorPlateItemComponent, EntityTerminatingEvent>(OnPlateDestroyed);
    }

    private void OnPlateInserted(Entity<ArmorPlateHolderComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID != StorageComponent.ContainerId)
            return;

        if (!_plateItemQuery.TryGetComponent(args.Entity, out var plateComp))
            return;

        var holder = ent.Comp;

        if (holder.ActivePlate == null)
        {
            SetActivePlate(ent, args.Entity, plateComp, holder);
        }
    }

    private void OnPlateRemoved(Entity<ArmorPlateHolderComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID != StorageComponent.ContainerId)
            return;

        var removedEntity = args.Entity;
        var holder = ent.Comp;

        if (holder.ActivePlate != removedEntity)
            return;

        ClearActivePlate(ent, holder);

        if (TryComp<StorageComponent>(ent, out var storage))
        {
            foreach (var item in storage.Container.ContainedEntities)
            {
                if (_plateItemQuery.TryGetComponent(item, out var plateComp))
                {
                    SetActivePlate(ent, item, plateComp, holder);
                    break;
                }
            }
        }
    }

    private void OnExamined(Entity<ArmorPlateHolderComponent> ent, ref ExaminedEvent args)
    {
        var holder = ent.Comp;

        if (!TryComp<StorageComponent>(ent, out _))
        {
            args.PushMarkup(Loc.GetString("armor-plate-examine-no-storage"));
            return;
        }

        if (holder.ActivePlate == null)
        {
            args.PushMarkup(Loc.GetString("armor-plate-examine-no-plate"));
            return;
        }

        var plateName = MetaData(holder.ActivePlate.Value).EntityName;

        if (!_plateItemQuery.TryGetComponent(holder.ActivePlate.Value, out var plateItem))
        {
            args.PushMarkup(Loc.GetString("armor-plate-examine-with-plate-simple", ("plateName", plateName)));
            return;
        }

        var maxDurability = (float?)plateItem?.MaxDurability;
        if (maxDurability != null &&
            TryComp<DamageableComponent>(holder.ActivePlate.Value, out var damageable))
        {
            var totalDamage = damageable.TotalDamage.Int();

            var durabilityPercent = (maxDurability.Value - totalDamage) / maxDurability.Value;
            durabilityPercent = Math.Clamp(durabilityPercent, 0f, 1f);

            // goes from red to green as durability gets higher
            var durabilityColor = Color.InterpolateBetween(Color.Red, Color.Green, durabilityPercent);

            args.PushMarkup(Loc.GetString("armor-plate-examine-with-plate",
                ("plateName", plateName),
                ("percent", (int)durabilityPercent),
                ("durabilityColor", durabilityColor)));
        }
        else
        {
            args.PushMarkup(Loc.GetString("armor-plate-examine-with-plate-simple", ("plateName", plateName)));
        }
    }

    private void OnRefreshMoveSpeed(EntityUid uid, ArmorPlateHolderComponent component, InventoryRelayedEvent<RefreshMovementSpeedModifiersEvent> args)
    {
        args.Args.ModifySpeed(component.WalkSpeedModifier, component.SprintSpeedModifier);
    }

    /// <summary>
    /// Sets the active plate and updates speed modifiers.
    /// </summary>
    private void SetActivePlate(EntityUid holderUid, EntityUid plateUid, ArmorPlateItemComponent plateComp, ArmorPlateHolderComponent holder)
    {
        holder.ActivePlate = plateUid;
        holder.WalkSpeedModifier = plateComp.WalkSpeedModifier;
        holder.SprintSpeedModifier = plateComp.SprintSpeedModifier;

        Dirty(holderUid, holder);
        RefreshMovementSpeed(holderUid);
    }

    /// <summary>
    /// Clears the active plate and resets speed modifiers.
    /// </summary>
    private void ClearActivePlate(EntityUid holderUid, ArmorPlateHolderComponent holder)
    {
        holder.ActivePlate = null;
        holder.WalkSpeedModifier = 1.0f;
        holder.SprintSpeedModifier = 1.0f;

        Dirty(holderUid, holder);
        RefreshMovementSpeed(holderUid);
    }

    /// <summary>
    /// Refreshes movement speed for the entity wearing this armor, if possible.
    /// </summary>
    private void RefreshMovementSpeed(EntityUid armorUid)
    {
        if (_inventory.TryGetContainingEntity(armorUid, out var wearer))
            _movementSpeed.RefreshMovementSpeedModifiers(wearer.Value);
    }

    /// <summary>
    /// Tries to get the entity holding an armorplate. This is not necessarily
    /// the wearer of the plate.
    /// </summary>
    /// <returns>True if the holder of the armorplate was found.</returns>
    public bool TryGetPlateHolder(Entity<TransformComponent?, MetaDataComponent?> plateEntity, [NotNullWhen(true)] out Entity<ArmorPlateHolderComponent>? holderEntity)
    {
        if (_container.TryGetContainingContainer(plateEntity, out var container) &&
            _plateHolderQuery.TryGetComponent(container.Owner, out var holderComponent))
        {
            holderEntity = (container.Owner, holderComponent);
            return true;
        }

        holderEntity = null;
        return false;
    }

    /// <summary>
    /// Tries to get the active plate from an armor holder.
    /// </summary>
    public bool TryGetActivePlate(Entity<ArmorPlateHolderComponent?> holder, out Entity<ArmorPlateItemComponent> plate)
    {
        plate = default;

        if (!_plateHolderQuery.Resolve(holder, ref holder.Comp, logMissing: false))
            return false;

        if (holder.Comp.ActivePlate == null)
            return false;

        if (!_plateItemQuery.TryGetComponent(holder.Comp.ActivePlate.Value, out var plateComp))
            return false;

        plate = (holder.Comp.ActivePlate.Value, plateComp);
        return true;
    }

    private void OnBeforeDamageChanged(Entity<InventoryComponent> wearerEntity, ref BeforeDamageChangedEvent args)
    {
        if (args.Cancelled || args.Damage.Empty)
            return;

        if (!_inventory.TryGetSlots(wearerEntity, out var slots))
            return;

        foreach (var slot in slots)
        {
            if (!_inventory.TryGetSlotEntity(wearerEntity, slot.Name, out var equipped, wearerEntity.Comp))
                continue;

            if (!TryGetActivePlate(equipped.Value, out var plate))
                continue;

            AbsorbDamage(wearerEntity, plate, args.Damage);

            args.Damage.DamageDict.Remove("Piercing");

            return;
        }
    }

    private void AbsorbDamage(
        EntityUid wearer,
        in Entity<ArmorPlateItemComponent> plate,
        in DamageSpecifier overallDamageSpecifier)
    {
        var plateDamageSpecifier = new DamageSpecifier();
        var staminaDamage = FixedPoint2.Zero;
        foreach (var (damageType, damageAmount) in overallDamageSpecifier.DamageDict)
        {
            if (!plate.Comp.AbsorbedDamageCoefficients.TryGetValue(damageType, out var absorbedDamageCoefficient) ||
                !plate.Comp.DealtDamageData.TryGetValue(damageType, out var dealtDamageTypeData))
                continue;

            var dealtPlateDamage = damageAmount * absorbedDamageCoefficient;

            // indexes twice but who cares
            plateDamageSpecifier.DamageDict[dealtDamageTypeData.Item1] = plateDamageSpecifier.DamageDict.GetValueOrDefault(dealtDamageTypeData.Item1) + dealtPlateDamage;
            staminaDamage += dealtPlateDamage * dealtDamageTypeData.Item2;
        }

        _damageable.TryChangeDamage(plate.Owner, plateDamageSpecifier, ignoreResistances: true);
        _stamina.TakeStaminaDamage(wearer, (float)staminaDamage);
    }

    private void OnPlateDestroyed(Entity<ArmorPlateItemComponent> ent, ref EntityTerminatingEvent args)
    {
        if (!_container.TryGetContainingContainer(ent.Owner, out var container))
            return;

        var holderUid = container.Owner;
        if (!_plateHolderQuery.TryGetComponent(holderUid, out var holder))
            return;

        if (holder.ActivePlate != ent.Owner)
            return;

        if (holder.ShowBreakPopup)
        {
            if (_inventory.TryGetContainingEntity(holderUid, out var wearer))
            {
                _popup.PopupEntity(
                    Loc.GetString("armor-plate-break", ("plateName", MetaData(ent).EntityName)),
                    wearer.Value,
                    wearer.Value,
                    PopupType.MediumCaution
                );
            }
        }
    }
}

