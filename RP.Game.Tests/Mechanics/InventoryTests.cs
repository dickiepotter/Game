namespace RP.Game.Tests.Mechanics
{
    using System;
    using System.Collections.Generic;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Mechanics.Items;

    /// <summary>
    /// A test item list: 1-49 stack to 64, 50-99 stack to 16, 100+ are tools that carry state and never
    /// stack.
    /// </summary>
    internal sealed class TestItems : IItemPalette
    {
        public const int Stone = 1;
        public const int Wood = 2;
        public const int Iron = 3;
        public const int Ingot = 50;
        public const int Pick = 100;
        public const int Sword = 101;

        public int MaxStack(int itemId) => itemId >= 100 ? 1 : (itemId >= 50 ? 16 : 64);

        public bool HasState(int itemId) => itemId >= 100;

        public int MaxDurability(int itemId) => itemId >= 100 ? 250 : 0;

        public string NameOf(int itemId) => $"item{itemId}";
    }

    /// <summary>
    /// The inventory contract: what fits, what merges, what comes out, and the failure modes that make
    /// players give up on a game.
    /// </summary>
    [TestClass]
    public sealed class InventoryTests
    {
        private static readonly TestItems Items = new TestItems();

        private static Inventory New(int slots = 8) => new Inventory(Items, slots);

        [TestMethod]
        public void Add_FillsPartialStacksBeforeTakingFreeSlots()
        {
            // The most common inventory complaint there is, and it is entirely an insertion-order bug: drop
            // into the first free slot instead and a player ends up with a pack full of half-stacks,
            // convinced they are out of room while carrying a fraction of their capacity.
            Inventory inventory = New();
            inventory.SetSlot(0, new ItemStack(TestItems.Stone, 60));
            inventory.SetSlot(3, new ItemStack(TestItems.Stone, 50));

            inventory.Add(new ItemStack(TestItems.Stone, 10)).IsEmpty.Should().BeTrue();

            inventory[0].Count.Should().Be(64, "the first partial stack should top up first");
            inventory[3].Count.Should().Be(56);
            inventory.CountFreeSlots().Should().Be(6, "no free slot should have been touched");
        }

        [TestMethod]
        public void Add_SpillsIntoFreeSlotsOnceStacksAreFull()
        {
            Inventory inventory = New(3);
            inventory.Add(new ItemStack(TestItems.Stone, 150)).IsEmpty.Should().BeTrue();

            inventory[0].Count.Should().Be(64);
            inventory[1].Count.Should().Be(64);
            inventory[2].Count.Should().Be(22);
        }

        [TestMethod]
        public void Add_ReturnsWhatWouldNotFitRatherThanDestroyingIt()
        {
            // No operation silently eats items. A caller that only learns "it failed" has to re-derive how
            // much fitted, and the one that does not bother is how items vanish.
            Inventory inventory = New(2);
            ItemStack leftover = inventory.Add(new ItemStack(TestItems.Stone, 200));

            leftover.ItemId.Should().Be(TestItems.Stone);
            leftover.Count.Should().Be(200 - 128);
            inventory.CountOf(TestItems.Stone).Should().Be(128);
        }

        [TestMethod]
        public void Add_RespectsPerItemStackLimits()
        {
            Inventory inventory = New(4);
            inventory.Add(new ItemStack(TestItems.Ingot, 40));

            inventory[0].Count.Should().Be(16, "ingots stack to sixteen, not sixty-four");
            inventory[1].Count.Should().Be(16);
            inventory[2].Count.Should().Be(8);
        }

        [TestMethod]
        public void Add_StatefulItemsNeverMergeAndTakeOneSlotEach()
        {
            // Two half-worn picks cannot become a stack of two without discarding one item's history.
            Inventory inventory = New(4);

            var worn = new ItemStack(TestItems.Pick, 1, new ItemState(100, 250));
            var fresh = new ItemStack(TestItems.Pick, 1, ItemState.Fresh(250));

            inventory.Add(worn).IsEmpty.Should().BeTrue();
            inventory.Add(fresh).IsEmpty.Should().BeTrue();

            inventory[0].State!.Durability.Should().Be(100);
            inventory[1].State!.Durability.Should().Be(250);
            inventory.CountFreeSlots().Should().Be(2);
        }

        [TestMethod]
        public void Add_ManyToolsAtOnceTakeOneSlotEach()
        {
            Inventory inventory = New(3);
            ItemStack leftover = inventory.Add(new ItemStack(TestItems.Pick, 5, ItemState.Fresh(250)));

            leftover.Count.Should().Be(2, "only three slots were free");
            inventory.CountFreeSlots().Should().Be(0);
            for (int i = 0; i < 3; i++) inventory[i].Count.Should().Be(1);
        }

        [TestMethod]
        public void CanFit_AgreesWithWhatAddActuallyDoes()
        {
            // The two must never disagree, or a caller that checks first and then adds will either refuse a
            // valid action or destroy items.
            var rng = new Random(7);
            for (int trial = 0; trial < 300; trial++)
            {
                Inventory inventory = New(rng.Next(1, 6));
                for (int i = 0; i < inventory.SlotCount; i++)
                {
                    if (rng.NextDouble() < 0.5) continue;
                    inventory.SetSlot(i, new ItemStack(TestItems.Stone, rng.Next(1, 65)));
                }

                var candidate = new ItemStack(TestItems.Stone, rng.Next(1, 200));
                bool predicted = inventory.CanFit(candidate);
                ItemStack leftover = inventory.Add(candidate);

                leftover.IsEmpty.Should().Be(predicted, "trial {0}", trial);
            }
        }

        [TestMethod]
        public void Remove_TakesFromTheBackSoTheFrontOfThePackStaysArranged()
        {
            Inventory inventory = New();
            inventory.SetSlot(0, new ItemStack(TestItems.Stone, 10));
            inventory.SetSlot(5, new ItemStack(TestItems.Stone, 10));

            inventory.Remove(TestItems.Stone, 10).Should().Be(10);

            inventory[0].Count.Should().Be(10, "the arranged front of the pack should be untouched");
            inventory[5].IsEmpty.Should().BeTrue();
        }

        [TestMethod]
        public void Remove_SpansSlotsAndReportsWhatItActuallyTook()
        {
            Inventory inventory = New();
            inventory.SetSlot(1, new ItemStack(TestItems.Stone, 30));
            inventory.SetSlot(2, new ItemStack(TestItems.Stone, 30));

            inventory.Remove(TestItems.Stone, 100).Should().Be(60, "there were only sixty");
            inventory.CountOf(TestItems.Stone).Should().Be(0);
        }

        [TestMethod]
        public void Remove_LeavesStatefulItemsAlone()
        {
            // A bulk remove asking for "three picks" must not silently take the enchanted one.
            Inventory inventory = New();
            inventory.SetSlot(0, new ItemStack(TestItems.Pick, 1, ItemState.Fresh(250)));

            inventory.Remove(TestItems.Pick, 1).Should().Be(0);
            inventory[0].IsEmpty.Should().BeFalse();
        }

        [TestMethod]
        public void TakeFromSlot_SplitsAStack()
        {
            Inventory inventory = New();
            inventory.SetSlot(0, new ItemStack(TestItems.Stone, 40));

            ItemStack taken = inventory.TakeFromSlot(0, 15);

            taken.Count.Should().Be(15);
            inventory[0].Count.Should().Be(25);
        }

        [TestMethod]
        public void Transfer_MovesWhatItCanAndKeepsTheRest()
        {
            Inventory source = New(2);
            Inventory destination = New(1);

            source.SetSlot(0, new ItemStack(TestItems.Stone, 100));
            destination.SetSlot(0, new ItemStack(TestItems.Stone, 60));

            source.TransferSlot(0, destination).Should().Be(4, "only four more fit in the destination stack");
            source[0].Count.Should().Be(96);
            destination[0].Count.Should().Be(64);
        }

        [TestMethod]
        public void SlotChanged_FiresForEverySlotTouched()
        {
            Inventory inventory = New(4);
            var touched = new List<int>();
            inventory.SlotChanged += touched.Add;

            inventory.Add(new ItemStack(TestItems.Stone, 130));

            touched.Should().BeEquivalentTo(new[] { 0, 1, 2 });
        }

        [TestMethod]
        public void IsFull_AccountsForRoomInPartialStacks()
        {
            Inventory inventory = New(1);
            inventory.SetSlot(0, new ItemStack(TestItems.Stone, 63));

            inventory.IsFull.Should().BeFalse("there is still room for one more");
            inventory.Add(new ItemStack(TestItems.Stone, 1));
            inventory.IsFull.Should().BeTrue();
        }

        [TestMethod]
        public void Rejects_ANonPositiveSlotCount()
        {
            Action act = () => new Inventory(Items, 0);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        // ---- Item state ---------------------------------------------------------------------------------

        [TestMethod]
        public void ItemState_WearsDownAndBreaks()
        {
            ItemState state = ItemState.Fresh(100);

            state.IsBroken.Should().BeFalse();
            state.Condition.Should().Be(1.0);

            state = state.Wear(40);
            state.Durability.Should().Be(60);
            state.Condition.Should().BeApproximately(0.6, 1e-9);

            state = state.Wear(100);
            state.Durability.Should().Be(0);
            state.IsBroken.Should().BeTrue();
        }

        [TestMethod]
        public void ItemState_ItemsWithNoDurabilityNeverBreak()
        {
            ItemState state = ItemState.Fresh(0);

            state.Wear(1000).IsBroken.Should().BeFalse();
            state.Condition.Should().Be(1.0);
        }

        [TestMethod]
        public void ItemState_IsImmutableSoTheOriginalSurvives()
        {
            // An item state is referenced by a slot, possibly by a replication buffer, possibly by an undo
            // record. Mutating in place changes all of them at once.
            ItemState original = ItemState.Fresh(100);
            ItemState worn = original.Wear(30);

            original.Durability.Should().Be(100);
            worn.Durability.Should().Be(70);
        }

        [TestMethod]
        public void ItemState_UpgradeRaisesTheCeilingAndFullyRepairs()
        {
            // A player who has just spent rare materials improving a tool should not immediately have to
            // spend more repairing it.
            ItemState state = ItemState.Fresh(100).Wear(80);
            state.Durability.Should().Be(20);

            ItemState upgraded = state.Upgrade(extraDurability: 50);

            upgraded.UpgradeLevel.Should().Be(1);
            upgraded.MaxDurability.Should().Be(150);
            upgraded.Durability.Should().Be(150, "an upgrade must not leave a tool closer to breaking");
        }

        [TestMethod]
        public void ItemState_ModifiersOfTheSameKindStack()
        {
            ItemState state = ItemState.Fresh(100)
                .WithModifier(new ItemModifier(1, 0.25))
                .WithModifier(new ItemModifier(1, 0.10))
                .WithModifier(new ItemModifier(2, 5.0));

            state.ModifierTotal(1).Should().BeApproximately(0.35, 1e-9);
            state.ModifierTotal(2).Should().BeApproximately(5.0, 1e-9);
            state.ModifierTotal(3).Should().Be(0.0);
            state.Modifiers.Should().HaveCount(3);
        }

        [TestMethod]
        public void ItemStack_ZeroCountCollapsesToEmptyIncludingItsId()
        {
            // A slot holding "0 of item 7" is a bug waiting to happen: some code will read the id and act.
            var stack = new ItemStack(TestItems.Stone, 0);

            stack.IsEmpty.Should().BeTrue();
            stack.ItemId.Should().Be(0);
            stack.Should().Be(ItemStack.Empty);
        }
    }
}
