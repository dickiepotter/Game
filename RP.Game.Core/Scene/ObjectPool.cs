namespace RP.Game.Scene
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A reuse pool for short-lived objects — projectiles, debris, effects — so a firefight doesn't allocate
    /// (and later garbage-collect) thousands of instances a second (build brief S2/S5: no per-frame churn).
    /// Returned objects are kept and handed back out by <see cref="Get"/> instead of being made afresh.
    /// </summary>
    /// <remarks>
    /// The pool only manages lifecycle; the caller decides what "reset" means via the optional
    /// <c>onReturn</c> hook (e.g. mark a projectile dead). <see cref="CreatedCount"/> counts how many real
    /// allocations have ever happened, which is how the tests prove reuse: churn many objects and it should
    /// not climb past the high-water mark of simultaneously-live ones.
    /// </remarks>
    public sealed class ObjectPool<T> where T : class
    {
        private readonly Func<T> _factory;
        private readonly Action<T>? _onReturn;
        private readonly Stack<T> _free = new();

        public ObjectPool(Func<T> factory, int prewarm = 0, Action<T>? onReturn = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onReturn = onReturn;
            for (int i = 0; i < prewarm; i++) _free.Push(Create());
        }

        /// <summary>Total instances ever allocated by the pool (does not fall when objects are returned).</summary>
        public int CreatedCount { get; private set; }

        /// <summary>How many instances are currently parked and ready to reuse.</summary>
        public int FreeCount => _free.Count;

        /// <summary>Hands out a pooled instance, allocating a new one only if none are free.</summary>
        public T Get() => _free.Count > 0 ? _free.Pop() : Create();

        /// <summary>Returns an instance for reuse, running the reset hook first.</summary>
        public void Return(T item)
        {
            if (item is null) throw new ArgumentNullException(nameof(item));
            _onReturn?.Invoke(item);
            _free.Push(item);
        }

        private T Create()
        {
            CreatedCount++;
            return _factory();
        }
    }
}
