namespace Content.Server._Mono.Projectiles.TargetSeeking;

/// <summary>
///     Designates an entity, or its grid if it's on one, as targetable by an entity with <see cref="TargetSeekingComponent"/>.
/// 
///     This can be added to either grids or non-grid entities.
/// </summary>
[RegisterComponent]
public sealed partial class TargetSeekableComponent : Component;