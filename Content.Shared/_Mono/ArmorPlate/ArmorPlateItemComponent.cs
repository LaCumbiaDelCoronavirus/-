// SPDX-FileCopyrightText: 2025 ark1368
//
// SPDX-License-Identifier: MPL-2.0

using Content.Shared.FixedPoint;
using Robust.Shared.GameStates;

namespace Content.Shared._Mono.ArmorPlate;

/// <summary>
/// Component for armor plates that can be inserted into compatible clothing.
/// </summary>
[RegisterComponent, NetworkedComponent]
[AutoGenerateComponentState(fieldDeltas: true)]
public sealed partial class ArmorPlateItemComponent : Component
{
    /// <summary>
    /// Most damage that this plate can take until being destroyed, assuming it is fully intact. Automatically generated to match the destruction threshold in DestructibleComponent.
    ///     This will be null if the component does not exist on the entity.
    /// </summary>
    [AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public FixedPoint2? MaxDurability = null;

    /// <summary>
    ///     Dictionary of different damage-types and
    ///         a scalar representing a coefficient of
    ///         how much of the specified damage is
    ///         taken by the armorplate, from 1 (affecting
    ///         all damage of the specified type) to
    ///         0 (affecting none).
    /// 
    ///     Damage-types not listed here will not be
    ///         affected by the armorplate.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<string, float> AbsorbedDamageCoefficients = new();

    /// <summary>
    ///     Dictionary of different damage-types, the
    ///         damage-type it's converted to when damaging
    ///         the armorplate itself with it, and
    ///         a coefficient of the amount of dealt
    ///         damage converted to stamina damage on
    ///         the wearer of the plate.
    /// </summary>
    // REMIND ME
    // TO
    // NEVER
    // DO
    // TUPLES
    // IN A DICTIONARY
    // IN YAML
    // EVER
    // AGAIN
    // PLS
    // THANKS
    // NVM
    [DataField(required: true)]
    public Dictionary<string, (string, float)> DealtDamageData = new();

    /// <summary>
    /// Walk speed modifier applied when this plate is active in worn clothing.
    /// </summary>
    [DataField]
    [AutoNetworkedField]
    public float WalkSpeedModifier = 1.0f;

    /// <summary>
    /// Sprint speed modifier applied when this plate is active in worn clothing.
    /// </summary>
    [DataField]
    [AutoNetworkedField]
    public float SprintSpeedModifier = 1.0f;
}
