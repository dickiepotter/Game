namespace RP.Game.Mechanics.Items
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// What the engine needs to know about an item type. The seam between generic inventory and crafting
    /// machinery and a specific game's item list, exactly as <c>IVoxelPalette</c> is for blocks.
    /// </summary>
    public interface IItemPalette
    {
        /// <summary>How many of this item fit in one slot. Tools and anything carrying per-item state
        /// should return 1.</summary>
        int MaxStack(int itemId);

        /// <summary>
        /// Whether this item carries per-instance state — durability, upgrades, modifiers.
        /// </summary>
        /// <remarks>
        /// Stateful items never merge with each other, even when they look identical, because merging two
        /// half-worn picks into a stack of two would have to discard one item's history. This is the flag
        /// that stops it, and the reason tools occupy a slot each.
        /// </remarks>
        bool HasState(int itemId);

        /// <summary>How much durability a fresh instance of this item has; 0 for items that never wear.</summary>
        int MaxDurability(int itemId);

        /// <summary>A name for logs and error messages. Never used for logic.</summary>
        string NameOf(int itemId);
    }

    /// <summary>
    /// One modifier on an item: a stat, and how much of it. The building block of the upgrade system.
    /// </summary>
    /// <remarks>
    /// <para><b>The kind is an integer, not an enum.</b> Which stats exist is a game's decision — one game's
    /// "mining speed" is another's "harvest yield" — so the engine carries the number and the game supplies
    /// the meaning. That is the same choice made for block ids, and for the same reason: an enum here would
    /// fix the game's design at engine level forever.</para>
    /// <para><b>Additive and multiplicative are different modifiers, not a flag.</b> Mixing them into one
    /// value and a boolean makes every consumer branch, and makes the order of application ambiguous —
    /// which is how "+5 damage" and "+20% damage" end up producing different totals depending on the order
    /// they happened to be stored in. Keeping them as separate kinds pushes the decision to the game, where
    /// it belongs.</para>
    /// </remarks>
    public readonly struct ItemModifier : IEquatable<ItemModifier>
    {
        /// <summary>Which stat this affects. Meaning is the game's.</summary>
        public readonly int Kind;

        /// <summary>How much. Interpretation — flat, fractional, multiplier — is the game's.</summary>
        public readonly double Magnitude;

        /// <summary>Creates a modifier.</summary>
        public ItemModifier(int kind, double magnitude)
        {
            Kind = kind;
            Magnitude = magnitude;
        }

        /// <inheritdoc />
        public bool Equals(ItemModifier other) => Kind == other.Kind && Magnitude.Equals(other.Magnitude);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ItemModifier other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Kind, Magnitude);

        /// <inheritdoc />
        public override string ToString() => $"{Kind}{(Magnitude >= 0 ? "+" : string.Empty)}{Magnitude:0.##}";
    }

    /// <summary>
    /// The per-instance history of one item: how worn it is, how far it has been upgraded, and what has been
    /// worked into it.
    /// </summary>
    /// <remarks>
    /// <para><b>Immutable, and replaced rather than mutated.</b> Every operation returns a new state. That
    /// costs an allocation per swing, which sounds wasteful until you consider what mutation costs: an item
    /// state is referenced by an inventory slot, possibly by a network replication buffer, possibly by an
    /// undo record, and mutating it in place changes all of them at once. Immutability makes "this is the
    /// pick as it was when you started the swing" a thing that can exist.</para>
    /// <para><b>Upgrades are a level plus modifiers, not one or the other.</b> The level is the linear spine
    /// — spend materials, get uniformly better — and the modifiers are the interesting part, because they
    /// come from specific rare materials and pull in different directions. A player who has upgraded a pick
    /// five times has a better pick; a player who has worked aurethyst into it has a <i>different</i> pick.
    /// Only the second one produces a story.</para>
    /// </remarks>
    public sealed class ItemState
    {
        private static readonly ItemModifier[] NoModifiers = Array.Empty<ItemModifier>();

        private readonly ItemModifier[] _modifiers;

        /// <summary>Creates a state.</summary>
        public ItemState(int durability, int maxDurability, int upgradeLevel = 0, ItemModifier[]? modifiers = null)
        {
            MaxDurability = maxDurability < 0 ? 0 : maxDurability;
            Durability = durability < 0 ? 0 : (durability > MaxDurability ? MaxDurability : durability);
            UpgradeLevel = upgradeLevel < 0 ? 0 : upgradeLevel;
            _modifiers = modifiers ?? NoModifiers;
        }

        /// <summary>A fresh, unworn, un-upgraded instance.</summary>
        public static ItemState Fresh(int maxDurability) => new ItemState(maxDurability, maxDurability);

        /// <summary>How much use is left. Zero means broken.</summary>
        public int Durability { get; }

        /// <summary>How much use a fresh instance has. Zero means the item never wears out.</summary>
        public int MaxDurability { get; }

        /// <summary>How many times this item has been upgraded.</summary>
        public int UpgradeLevel { get; }

        /// <summary>The modifiers worked into this item.</summary>
        public IReadOnlyList<ItemModifier> Modifiers => _modifiers;

        /// <summary>Whether the item has worn out. An item with no durability at all never breaks.</summary>
        public bool IsBroken => MaxDurability > 0 && Durability <= 0;

        /// <summary>How worn the item is, from 1 (fresh) to 0 (broken). Always 1 for items that never wear.</summary>
        public double Condition => MaxDurability <= 0 ? 1.0 : Durability / (double)MaxDurability;

        /// <summary>The total magnitude of every modifier of one kind.</summary>
        /// <remarks>
        /// Summed rather than taking the best, so working the same material in twice genuinely stacks. A
        /// game wanting diminishing returns applies them at the point of use, where it can see the total.
        /// </remarks>
        public double ModifierTotal(int kind)
        {
            double total = 0.0;
            for (int i = 0; i < _modifiers.Length; i++)
            {
                if (_modifiers[i].Kind == kind) total += _modifiers[i].Magnitude;
            }

            return total;
        }

        /// <summary>Returns this item with some durability spent.</summary>
        public ItemState Wear(int amount)
        {
            if (amount <= 0 || MaxDurability <= 0) return this;
            return new ItemState(Durability - amount, MaxDurability, UpgradeLevel, _modifiers);
        }

        /// <summary>Returns this item with some durability restored.</summary>
        public ItemState Repair(int amount)
        {
            if (amount <= 0 || MaxDurability <= 0) return this;
            return new ItemState(Durability + amount, MaxDurability, UpgradeLevel, _modifiers);
        }

        /// <summary>
        /// Returns this item one upgrade level higher, with more maximum durability and fully repaired.
        /// </summary>
        /// <remarks>
        /// Repairing on upgrade is a deliberate kindness. A player who has just spent rare materials
        /// improving a tool should not immediately have to spend more repairing it, and the alternative —
        /// carrying the old wear proportionally — means an upgrade can leave a tool closer to breaking than
        /// it was before, which reads as a punishment for investing.
        /// </remarks>
        public ItemState Upgrade(int extraDurability, ItemModifier? added = null)
        {
            int newMax = MaxDurability + (extraDurability < 0 ? 0 : extraDurability);

            ItemModifier[] modifiers = _modifiers;
            if (added.HasValue)
            {
                modifiers = new ItemModifier[_modifiers.Length + 1];
                Array.Copy(_modifiers, modifiers, _modifiers.Length);
                modifiers[_modifiers.Length] = added.Value;
            }

            return new ItemState(newMax, newMax, UpgradeLevel + 1, modifiers);
        }

        /// <summary>Returns this item with a modifier worked into it, without changing its upgrade level.</summary>
        public ItemState WithModifier(ItemModifier modifier)
        {
            var modifiers = new ItemModifier[_modifiers.Length + 1];
            Array.Copy(_modifiers, modifiers, _modifiers.Length);
            modifiers[_modifiers.Length] = modifier;
            return new ItemState(Durability, MaxDurability, UpgradeLevel, modifiers);
        }

        /// <inheritdoc />
        public override string ToString()
            => $"{Durability}/{MaxDurability}" +
               (UpgradeLevel > 0 ? $" +{UpgradeLevel}" : string.Empty) +
               (_modifiers.Length > 0 ? $" [{_modifiers.Length} mod]" : string.Empty);
    }

    /// <summary>
    /// Some number of one item, optionally with per-instance state. The unit everything in an inventory,
    /// a recipe, a machine or a trade is counted in.
    /// </summary>
    /// <remarks>
    /// <para>A <see langword="readonly struct"/>, and every operation returns a new one. Inventories hold
    /// arrays of these, so they cost no allocation and no indirection; the optional
    /// <see cref="ItemState"/> is the only reference, and only stateful items carry one.</para>
    /// <para><b>Zero count is the empty stack, and the item id goes with it.</b> A slot holding "0 of item
    /// 7" is a bug waiting to happen — some code will read the id and act on it. <see cref="Empty"/> is the
    /// only representation of nothing, and every operation that reduces a stack to zero returns it.</para>
    /// </remarks>
    public readonly struct ItemStack : IEquatable<ItemStack>
    {
        /// <summary>Nothing at all.</summary>
        public static readonly ItemStack Empty = default;

        /// <summary>Which item. Zero only ever appears in <see cref="Empty"/>.</summary>
        public readonly int ItemId;

        /// <summary>How many. Zero only ever appears in <see cref="Empty"/>.</summary>
        public readonly int Count;

        /// <summary>Per-instance state, or null for a plain stackable.</summary>
        public readonly ItemState? State;

        /// <summary>Creates a stack. A count of zero or less collapses to <see cref="Empty"/>.</summary>
        public ItemStack(int itemId, int count = 1, ItemState? state = null)
        {
            if (itemId <= 0 || count <= 0)
            {
                ItemId = 0;
                Count = 0;
                State = null;
                return;
            }

            ItemId = itemId;
            Count = count;
            State = state;
        }

        /// <summary>Whether this stack holds nothing.</summary>
        public bool IsEmpty => Count <= 0 || ItemId <= 0;

        /// <summary>Returns the same stack with a different count.</summary>
        public ItemStack WithCount(int count) => new ItemStack(ItemId, count, State);

        /// <summary>Returns the same stack with different per-instance state.</summary>
        public ItemStack WithState(ItemState? state) => new ItemStack(ItemId, Count, state);

        /// <summary>
        /// Whether two stacks are the same thing, and so may merge.
        /// </summary>
        /// <remarks>
        /// Stateful items are never alike, even when their state is numerically identical. Two picks at
        /// full durability could in principle merge, and allowing it would mean the moment either was used
        /// the stack would have to split again — so the rule is simply that anything carrying a history
        /// occupies its own slot.
        /// </remarks>
        public bool IsSameKind(ItemStack other)
            => ItemId == other.ItemId && State is null && other.State is null;

        /// <inheritdoc />
        public bool Equals(ItemStack other)
            => ItemId == other.ItemId && Count == other.Count && ReferenceEquals(State, other.State);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ItemStack other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(ItemId, Count);

        /// <summary>Equality.</summary>
        public static bool operator ==(ItemStack a, ItemStack b) => a.Equals(b);

        /// <summary>Inequality.</summary>
        public static bool operator !=(ItemStack a, ItemStack b) => !a.Equals(b);

        /// <inheritdoc />
        public override string ToString()
            => IsEmpty ? "(empty)" : $"{Count}x#{ItemId}{(State is null ? string.Empty : $" ({State})")}";
    }
}
