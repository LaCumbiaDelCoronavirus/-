// SPDX-FileCopyrightText: 2024 Aviu00
// SPDX-FileCopyrightText: 2025 Redrover1760
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._White.Blocking;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true)]
public sealed partial class RechargeableBlockingComponent : Component
{
    /// <summary>
    ///     Recharge rate of this entity's BatterySelfRechargerComponent when
    ///         <see cref="Discharged"/> is false. 
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float DischargedRechargeRate = 4f;

    /// <summary>
    ///     Recharge rate of this entity's BatterySelfRechargerComponent when
    ///         <see cref="Discharged"/> is true. 
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float ChargedRechargeRate = 5f;

    [ViewVariables, AutoNetworkedField]
    public bool Discharged;

    /// <summary>
    ///     Cached server-side percentage of the
    ///         battery's energy, to it's maximum capacity.
    ///         Goes from 0 (empty) to 100 (full).
    /// </summary>
    /// <remarks>
    ///     Should only be used for prediction.
    /// </remarks>
    [ViewVariables, AutoNetworkedField]
    public int CachedChargePercentage = 100;

    /// <summary>
    ///     Cached server-side TimeSpan at which
    ///         this entity's battery will fully
    ///         finish auto-recharging. If not
    ///         auto-recharging, then this is null.
    /// </summary>
    /// <remarks>
    ///     Should only be used for prediction.
    /// </remarks>
    [ViewVariables, AutoNetworkedField]
    public TimeSpan? CachedTimeOfRecharge = null;
}
