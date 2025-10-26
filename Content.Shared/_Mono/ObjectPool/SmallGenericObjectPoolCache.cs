using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;

namespace Content.Shared._Mono.ObjectPool;
/*
/// <summary>
///     ObjectPool cache for generic types. This caches every individual
///         type you give it, so it really should not be used with that many
///         different types.
/// </summary>
public static class SmallGenericObjectPoolCache<T> where T : class, new()
{
    public static readonly ObjectPool<T> Shared =
        new DefaultObjectPool<T>(new DefaultPooledObjectPolicy<T>());

    /// <inheritdoc cref="ObjectPool{T}.Get()"/>
    public static T Get() => Shared.Get();

    /// <inheritdoc cref="ObjectPool{T}.Return(T)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(T obj)
    {
        Shared.Return(obj);
    }
}
*/
