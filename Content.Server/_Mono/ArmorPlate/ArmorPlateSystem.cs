// SPDX-FileCopyrightText: 2025 ark1368
//
// SPDX-License-Identifier: MPL-2.0

using Content.Server.Destructible;
using Content.Shared._Mono.ArmorPlate;

namespace Content.Server._Mono.ArmorPlate;

/// <inheritdoc cref="SharedArmorPlateSystem"/>
public sealed class ArmorPlateSystem : SharedArmorPlateSystem
{
    [Dependency] private readonly DestructibleSystem _destructibleSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ArmorPlateItemComponent, ComponentStartup>(OnPlateStartup);
    }

    private void OnPlateStartup(Entity<ArmorPlateItemComponent> entity, ref ComponentStartup args)
    {
        _destructibleSystem.TryGetDestroyedAt(entity.Owner, out var destroyedDamageValue);
        entity.Comp.MaxDurability = destroyedDamageValue;
    }
}

