namespace RP.Game.Mechanics.Items
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A fixed set of slots holding item stacks: a player's pack, a chest, a machine's input buffer, a
    /// creature's loot table once it has been rolled.
    /// </summary>
    /// <remarks>
    /// <para><b>Slots, not a bag of counts.</b> Modelling an inventory as a dictionary of item to quantity
    /// is simpler and is the wrong shape: it cannot express a full pack, cannot hold two differently-worn
    /// tools, and gives the player no spatial sense of what they are carrying. Slots make capacity a real
    /// constraint, which is what makes decisions about what to carry interesting.</para>
    ///
    /// <para><b>Insertion fills partial stacks first.</b> Always. Dropping into the first free slot instead
    /// leaves a pack full of half-stacks and a player who believes they are out of room while carrying
    /// forty-one items across twenty slots — the single most common inventory complaint there is, and it is
    /// entirely an insertion-order bug.</para>
    ///
    /// <para><b>Everything returns what it could not do.</b> <see cref="Add"/> hands back the remainder
    /// rather than a bool, because "the pack was nearly full" is the interesting case and a caller that
    /// only learns "it failed" has to re-derive how much fitted. The same choice everywhere: no operation
    /// silently destroys items.</para>
    /// </remarks>
    public sealed class Inventory
    {
        private readonly ItemStack[] _slots;
        private readonly IItemPalette _palette;

        /// <summary>Creates an inventory with a fixed number of slots.</summary>
        public Inventory(IItemPalette palette, int slotCount)
        {
            if (slotCount <= 0) throw new ArgumentOutOfRangeException(nameof(slotCount), "An inventory needs at least one slot.");

            _palette = palette ?? throw new ArgumentNullException(nameof(palette));
            _slots = new ItemStack[slotCount];
        }

        /// <summary>How many slots there are.</summary>
        public int SlotCount => _slots.Length;

        /// <summary>Raised whenever the contents change, with the slot that changed.</summary>
        /// <remarks>
        /// A slot index rather than a "something changed" signal, so a user interface can redraw one slot
        /// instead of the whole pack. At the rate a machine moves items, redrawing everything is the
        /// difference between a smooth frame and a visible hitch.
        /// </remarks>
        public event Action<int>? SlotChanged;

        /// <summary>Reads a slot.</summary>
        public ItemStack this[int slot] => (uint)slot < (uint)_slots.Length ? _slots[slot] : ItemStack.Empty;

        /// <summary>Whether every slot is empty.</summary>
        public bool IsEmpty
        {
            get
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (!_slots[i].IsEmpty) return false;
                }

                return true;
            }
        }

        /// <summary>Whether no slot is free and no partial stack can take more.</summary>
        public bool IsFull
        {
            get
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    ItemStack slot = _slots[i];
                    if (slot.IsEmpty) return false;
                    if (slot.State is null && slot.Count < _palette.MaxStack(slot.ItemId)) return false;
                }

                return true;
            }
        }

        /// <summary>Writes a slot directly, without any merging. For loading a save or a network update.</summary>
        public void SetSlot(int slot, ItemStack stack)
        {
            if ((uint)slot >= (uint)_slots.Length) return;
            _slots[slot] = stack;
            SlotChanged?.Invoke(slot);
        }

        /// <summary>
        /// Puts a stack in, merging into partial stacks first and then taking free slots.
        /// </summary>
        /// <returns>Whatever would not fit, or <see cref="ItemStack.Empty"/> if all of it did.</returns>
        public ItemStack Add(ItemStack stack)
        {
            if (stack.IsEmpty) return ItemStack.Empty;

            // A stateful item cannot merge with anything, so it wants a free slot and nothing else.
            if (stack.State is not null || _palette.HasState(stack.ItemId))
            {
                return AddToFreeSlots(stack);
            }

            int maxStack = System.Math.Max(1, _palette.MaxStack(stack.ItemId));
            int remaining = stack.Count;

            // Pass one: top up partial stacks of the same item.
            for (int i = 0; i < _slots.Length && remaining > 0; i++)
            {
                ItemStack slot = _slots[i];
                if (slot.IsEmpty || slot.ItemId != stack.ItemId || slot.State is not null) continue;

                int room = maxStack - slot.Count;
                if (room <= 0) continue;

                int moved = System.Math.Min(room, remaining);
                _slots[i] = slot.WithCount(slot.Count + moved);
                remaining -= moved;
                SlotChanged?.Invoke(i);
            }

            // Pass two: whatever is left goes into free slots, a full stack at a time.
            for (int i = 0; i < _slots.Length && remaining > 0; i++)
            {
                if (!_slots[i].IsEmpty) continue;

                int moved = System.Math.Min(maxStack, remaining);
                _slots[i] = new ItemStack(stack.ItemId, moved);
                remaining -= moved;
                SlotChanged?.Invoke(i);
            }

            return remaining <= 0 ? ItemStack.Empty : stack.WithCount(remaining);
        }

        /// <summary>Whether a stack would fit entirely, without changing anything.</summary>
        public bool CanFit(ItemStack stack)
        {
            if (stack.IsEmpty) return true;

            if (stack.State is not null || _palette.HasState(stack.ItemId))
            {
                return CountFreeSlots() >= stack.Count;
            }

            int maxStack = System.Math.Max(1, _palette.MaxStack(stack.ItemId));
            long room = 0;

            for (int i = 0; i < _slots.Length; i++)
            {
                ItemStack slot = _slots[i];
                if (slot.IsEmpty) room += maxStack;
                else if (slot.ItemId == stack.ItemId && slot.State is null) room += maxStack - slot.Count;

                if (room >= stack.Count) return true;
            }

            return false;
        }

        /// <summary>
        /// Takes up to <paramref name="count"/> of an item out, from the <i>last</i> matching slot backward.
        /// </summary>
        /// <remarks>
        /// Backward on purpose. Recipes consume from the pack while the player is looking at it, and
        /// draining the first slot first makes the contents shuffle forward under their eyes every time
        /// they craft. Taking from the back leaves the front of the pack — the part they have arranged —
        /// alone.
        /// </remarks>
        /// <returns>How many were actually removed.</returns>
        public int Remove(int itemId, int count)
        {
            if (itemId <= 0 || count <= 0) return 0;

            int removed = 0;
            for (int i = _slots.Length - 1; i >= 0 && removed < count; i--)
            {
                ItemStack slot = _slots[i];
                if (slot.IsEmpty || slot.ItemId != itemId || slot.State is not null) continue;

                int taken = System.Math.Min(slot.Count, count - removed);
                int left = slot.Count - taken;
                _slots[i] = left > 0 ? slot.WithCount(left) : ItemStack.Empty;
                removed += taken;
                SlotChanged?.Invoke(i);
            }

            return removed;
        }

        /// <summary>Takes everything out of one slot.</summary>
        public ItemStack TakeSlot(int slot)
        {
            if ((uint)slot >= (uint)_slots.Length) return ItemStack.Empty;

            ItemStack stack = _slots[slot];
            if (stack.IsEmpty) return ItemStack.Empty;

            _slots[slot] = ItemStack.Empty;
            SlotChanged?.Invoke(slot);
            return stack;
        }

        /// <summary>Takes up to <paramref name="count"/> out of one slot.</summary>
        public ItemStack TakeFromSlot(int slot, int count)
        {
            if ((uint)slot >= (uint)_slots.Length || count <= 0) return ItemStack.Empty;

            ItemStack stack = _slots[slot];
            if (stack.IsEmpty) return ItemStack.Empty;

            int taken = System.Math.Min(stack.Count, count);
            int left = stack.Count - taken;
            _slots[slot] = left > 0 ? stack.WithCount(left) : ItemStack.Empty;
            SlotChanged?.Invoke(slot);
            return stack.WithCount(taken);
        }

        /// <summary>How many of an item are held, across every slot.</summary>
        public int CountOf(int itemId)
        {
            int total = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].ItemId == itemId) total += _slots[i].Count;
            }

            return total;
        }

        /// <summary>Whether the inventory holds at least this many of an item.</summary>
        public bool Has(int itemId, int count = 1) => CountOf(itemId) >= count;

        /// <summary>How many slots are empty.</summary>
        public int CountFreeSlots()
        {
            int free = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].IsEmpty) free++;
            }

            return free;
        }

        /// <summary>The first slot holding an item, or -1.</summary>
        public int FindSlot(int itemId)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].ItemId == itemId) return i;
            }

            return -1;
        }

        /// <summary>Exchanges the contents of two slots.</summary>
        public void Swap(int a, int b)
        {
            if ((uint)a >= (uint)_slots.Length || (uint)b >= (uint)_slots.Length || a == b) return;

            (_slots[a], _slots[b]) = (_slots[b], _slots[a]);
            SlotChanged?.Invoke(a);
            SlotChanged?.Invoke(b);
        }

        /// <summary>
        /// Moves as much as possible of one slot into another inventory — the primitive behind every
        /// hopper, chest transfer, shift-click and machine input.
        /// </summary>
        /// <returns>How many items moved.</returns>
        public int TransferSlot(int slot, Inventory destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if ((uint)slot >= (uint)_slots.Length) return 0;

            ItemStack stack = _slots[slot];
            if (stack.IsEmpty) return 0;

            ItemStack leftover = destination.Add(stack);
            int moved = stack.Count - leftover.Count;
            if (moved <= 0) return 0;

            _slots[slot] = leftover;
            SlotChanged?.Invoke(slot);
            return moved;
        }

        /// <summary>Empties every slot.</summary>
        public void Clear()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].IsEmpty) continue;
                _slots[i] = ItemStack.Empty;
                SlotChanged?.Invoke(i);
            }
        }

        /// <summary>Every non-empty stack, for saving, display and network replication.</summary>
        public IEnumerable<(int Slot, ItemStack Stack)> Contents()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (!_slots[i].IsEmpty) yield return (i, _slots[i]);
            }
        }

        private ItemStack AddToFreeSlots(ItemStack stack)
        {
            int remaining = stack.Count;

            for (int i = 0; i < _slots.Length && remaining > 0; i++)
            {
                if (!_slots[i].IsEmpty) continue;

                _slots[i] = stack.WithCount(1);
                remaining--;
                SlotChanged?.Invoke(i);
            }

            return remaining <= 0 ? ItemStack.Empty : stack.WithCount(remaining);
        }
    }
}
