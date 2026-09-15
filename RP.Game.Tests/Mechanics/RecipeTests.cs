namespace RP.Game.Tests.Mechanics
{
    using System;
    using System.Collections.Generic;
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Mechanics.Items;

    /// <summary>
    /// Recipe matching, crafting, and the reachability closure that proves a progression has no dead ends.
    /// </summary>
    [TestClass]
    public sealed class RecipeTests
    {
        private static readonly TestItems Items = new TestItems();

        private const int Plank = 10;
        private const int Stick = 11;
        private const int Handle = 12;

        private static Inventory New(int slots = 9) => new Inventory(Items, slots);

        private static Recipe Shapeless(string name, int output, int outputCount, params Ingredient[] inputs)
            => new Recipe
            {
                Name = name,
                Kind = RecipeKind.Shapeless,
                Station = "bench",
                Inputs = inputs,
                Outputs = new[] { new ItemStack(output, outputCount) },
            };

        // ---- Matching -----------------------------------------------------------------------------------

        [TestMethod]
        public void Shapeless_MatchesRegardlessOfArrangement()
        {
            var book = new RecipeBook();
            book.Add(Shapeless("planks", Plank, 4, new Ingredient(TestItems.Wood, 1)));

            Inventory grid = New();
            grid.SetSlot(7, new ItemStack(TestItems.Wood, 1));

            book.MatchGrid("bench", grid, 3, 3)!.Name.Should().Be("planks");
        }

        [TestMethod]
        public void Shapeless_RefusesAGridHoldingAnythingExtra()
        {
            // An inexact match would craft something cheaper than the player laid out and quietly eat the
            // difference.
            var book = new RecipeBook();
            book.Add(Shapeless("planks", Plank, 4, new Ingredient(TestItems.Wood, 1)));

            Inventory grid = New();
            grid.SetSlot(0, new ItemStack(TestItems.Wood, 1));
            grid.SetSlot(1, new ItemStack(TestItems.Iron, 1));

            book.MatchGrid("bench", grid, 3, 3).Should().BeNull();
        }

        [TestMethod]
        public void Shaped_MatchesAtAnyOffsetInTheGrid()
        {
            // A two-by-two recipe laid out in the bottom-right of a three-by-three grid is still that
            // recipe. Insisting on the top-left is the kind of rule players find maddening.
            var book = new RecipeBook();
            book.Add(new Recipe
            {
                Name = "handle",
                Kind = RecipeKind.Shaped,
                Station = "bench",
                Pattern = new[] { "P", "P" },
                Key = new Dictionary<char, int> { ['P'] = Plank },
                Outputs = new[] { new ItemStack(Handle, 1) },
            });

            foreach ((int top, int bottom) in new[] { (0, 3), (1, 4), (2, 5), (3, 6), (4, 7), (5, 8) })
            {
                Inventory grid = New();
                grid.SetSlot(top, new ItemStack(Plank, 1));
                grid.SetSlot(bottom, new ItemStack(Plank, 1));

                book.MatchGrid("bench", grid, 3, 3)!.Name.Should().Be("handle", "at offset {0}", top);
            }
        }

        [TestMethod]
        public void Shaped_RejectsTheSameItemsInTheWrongArrangement()
        {
            var book = new RecipeBook();
            book.Add(new Recipe
            {
                Name = "handle",
                Kind = RecipeKind.Shaped,
                Station = "bench",
                Pattern = new[] { "P", "P" },
                Key = new Dictionary<char, int> { ['P'] = Plank },
                Outputs = new[] { new ItemStack(Handle, 1) },
            });

            Inventory grid = New();
            grid.SetSlot(0, new ItemStack(Plank, 1));
            grid.SetSlot(1, new ItemStack(Plank, 1)); // side by side, not stacked

            book.MatchGrid("bench", grid, 3, 3).Should().BeNull();
        }

        [TestMethod]
        public void Shaped_IsTriedBeforeShapelessWhenBothCouldMatch()
        {
            // A shaped recipe is necessarily more specific. Trying shapeless first would let a loose pile
            // satisfy a recipe whose entire point is the arrangement.
            var book = new RecipeBook();
            book.Add(Shapeless("loose", Stick, 1, new Ingredient(Plank, 2)));
            book.Add(new Recipe
            {
                Name = "shaped",
                Kind = RecipeKind.Shaped,
                Station = "bench",
                Pattern = new[] { "P", "P" },
                Key = new Dictionary<char, int> { ['P'] = Plank },
                Outputs = new[] { new ItemStack(Handle, 1) },
            });

            Inventory grid = New();
            grid.SetSlot(0, new ItemStack(Plank, 1));
            grid.SetSlot(3, new ItemStack(Plank, 1));

            book.MatchGrid("bench", grid, 3, 3)!.Name.Should().Be("shaped");
        }

        [TestMethod]
        public void Shaped_GapsInThePatternMustActuallyBeEmpty()
        {
            var book = new RecipeBook();
            book.Add(new Recipe
            {
                Name = "ring",
                Kind = RecipeKind.Shaped,
                Station = "bench",
                Pattern = new[] { "PPP", "P P", "PPP" },
                Key = new Dictionary<char, int> { ['P'] = Plank },
                Outputs = new[] { new ItemStack(Handle, 1) },
            });

            Inventory grid = New();
            for (int i = 0; i < 9; i++) grid.SetSlot(i, new ItemStack(Plank, 1));

            book.MatchGrid("bench", grid, 3, 3).Should().BeNull("the centre must be empty");

            grid.SetSlot(4, ItemStack.Empty);
            book.MatchGrid("bench", grid, 3, 3)!.Name.Should().Be("ring");
        }

        [TestMethod]
        public void Station_SeparatesRecipesThatWouldOtherwiseCollide()
        {
            var book = new RecipeBook();
            book.Add(Shapeless("bench_version", Plank, 4, new Ingredient(TestItems.Wood, 1)));
            book.Add(new Recipe
            {
                Name = "furnace_version",
                Kind = RecipeKind.Shapeless,
                Station = "furnace",
                Inputs = new[] { new Ingredient(TestItems.Wood, 1) },
                Outputs = new[] { new ItemStack(Stick, 1) },
            });

            Inventory grid = New();
            grid.SetSlot(0, new ItemStack(TestItems.Wood, 1));

            book.MatchGrid("bench", grid, 3, 3)!.Name.Should().Be("bench_version");
            book.MatchGrid("furnace", grid, 3, 3)!.Name.Should().Be("furnace_version");
            book.MatchGrid("anvil", grid, 3, 3).Should().BeNull();
        }

        // ---- Crafting -----------------------------------------------------------------------------------

        [TestMethod]
        public void Craft_ConsumesInputsAndProducesOutputs()
        {
            Recipe recipe = Shapeless("planks", Plank, 4, new Ingredient(TestItems.Wood, 2));

            Inventory pack = New();
            pack.Add(new ItemStack(TestItems.Wood, 5));

            RecipeBook.Craft(recipe, pack).Should().BeTrue();

            pack.CountOf(TestItems.Wood).Should().Be(3);
            pack.CountOf(Plank).Should().Be(4);
        }

        [TestMethod]
        public void Craft_ProducesEveryOutputIncludingByproducts()
        {
            // A refining chain that only ever produces one thing has no texture; byproducts are what make a
            // processing tier worth upgrading.
            var recipe = new Recipe
            {
                Name = "smelt",
                Kind = RecipeKind.Process,
                Station = "furnace",
                Inputs = new[] { new Ingredient(TestItems.Iron, 2) },
                Outputs = new[] { new ItemStack(TestItems.Ingot, 1), new ItemStack(TestItems.Stone, 1) },
            };

            Inventory pack = New();
            pack.Add(new ItemStack(TestItems.Iron, 2));

            RecipeBook.Craft(recipe, pack).Should().BeTrue();
            pack.CountOf(TestItems.Ingot).Should().Be(1);
            pack.CountOf(TestItems.Stone).Should().Be(1, "the slag is an output too");
        }

        [TestMethod]
        public void Craft_RefusesWhenIngredientsAreShort_AndChangesNothing()
        {
            Recipe recipe = Shapeless("planks", Plank, 4, new Ingredient(TestItems.Wood, 2));

            Inventory pack = New();
            pack.Add(new ItemStack(TestItems.Wood, 1));

            RecipeBook.Craft(recipe, pack).Should().BeFalse();
            pack.CountOf(TestItems.Wood).Should().Be(1, "a failed craft must not consume anything");
            pack.CountOf(Plank).Should().Be(0);
        }

        [TestMethod]
        public void Craft_RefusesRatherThanDestroyingIngredientsWhenTheOutputWillNotFit()
        {
            // The default behaviour of the obvious implementation is to take the inputs and then discover
            // there is nowhere to put the result. Players never forgive it.
            var recipe = new Recipe
            {
                Name = "expand",
                Kind = RecipeKind.Shapeless,
                Station = "bench",
                Inputs = new[] { new Ingredient(TestItems.Wood, 1) },
                Outputs = new[] { new ItemStack(TestItems.Iron, 1) },
            };

            Inventory pack = New(2);
            pack.SetSlot(0, new ItemStack(TestItems.Wood, 64));
            pack.SetSlot(1, new ItemStack(TestItems.Stone, 64));

            var destination = new Inventory(Items, 1);
            destination.SetSlot(0, new ItemStack(TestItems.Ingot, 16));

            RecipeBook.Craft(recipe, pack, destination).Should().BeFalse();
            pack.CountOf(TestItems.Wood).Should().Be(64, "nothing should have been consumed");
        }

        [TestMethod]
        public void Craft_IntoAFullPackWorksWhenAnInputFreesTheSlot()
        {
            // Consuming an entire stack frees the slot the output needs. Refusing here would be technically
            // safe and would read as a bug.
            var recipe = new Recipe
            {
                Name = "convert",
                Kind = RecipeKind.Shapeless,
                Station = "bench",
                Inputs = new[] { new Ingredient(TestItems.Wood, 3) },
                Outputs = new[] { new ItemStack(TestItems.Iron, 1) },
            };

            Inventory pack = New(1);
            pack.SetSlot(0, new ItemStack(TestItems.Wood, 3));

            RecipeBook.Craft(recipe, pack).Should().BeTrue();
            pack.CountOf(TestItems.Iron).Should().Be(1);
            pack.CountOf(TestItems.Wood).Should().Be(0);
        }

        [TestMethod]
        public void Craft_ShapedCostsExactlyWhatItsPatternShows()
        {
            var recipe = new Recipe
            {
                Name = "handle",
                Kind = RecipeKind.Shaped,
                Station = "bench",
                Pattern = new[] { "P", "P" },
                Key = new Dictionary<char, int> { ['P'] = Plank },
                Outputs = new[] { new ItemStack(Handle, 1) },
            };

            recipe.TotalInputs()[Plank].Should().Be(2);

            Inventory pack = New();
            pack.Add(new ItemStack(Plank, 5));

            RecipeBook.Craft(recipe, pack).Should().BeTrue();
            pack.CountOf(Plank).Should().Be(3);
        }

        // ---- The book ------------------------------------------------------------------------------------

        [TestMethod]
        public void Book_IndexesByStationAndByOutput()
        {
            var book = new RecipeBook();
            book.Add(Shapeless("a", Plank, 4, new Ingredient(TestItems.Wood, 1)));
            book.Add(Shapeless("b", Plank, 2, new Ingredient(TestItems.Iron, 1)));

            book.AtStation("bench").Should().HaveCount(2);
            book.Producing(Plank).Should().HaveCount(2, "two different recipes make planks");
            book.ByName("a")!.Name.Should().Be("a");
            book.ByName("nope").Should().BeNull();
        }

        [TestMethod]
        public void Book_RejectsDuplicateNamesAndOutputlessRecipes()
        {
            var book = new RecipeBook();
            book.Add(Shapeless("a", Plank, 4, new Ingredient(TestItems.Wood, 1)));

            Action duplicate = () => book.Add(Shapeless("a", Stick, 1, new Ingredient(TestItems.Wood, 1)));
            duplicate.Should().Throw<ArgumentException>();

            Action empty = () => book.Add(new Recipe { Name = "nothing", Station = "bench" });
            empty.Should().Throw<ArgumentException>();
        }

        // ---- Reachability: the thing that catches a broken progression -------------------------------------

        [TestMethod]
        public void Reachable_FollowsAChainAllTheWayDown()
        {
            var book = new RecipeBook();
            book.Add(Shapeless("planks", Plank, 4, new Ingredient(TestItems.Wood, 1)));
            book.Add(Shapeless("sticks", Stick, 4, new Ingredient(Plank, 2)));
            book.Add(Shapeless("handle", Handle, 1, new Ingredient(Stick, 2)));

            HashSet<int> reachable = book.Reachable(new[] { TestItems.Wood });

            reachable.Should().Contain(new[] { TestItems.Wood, Plank, Stick, Handle });
            book.UnreachableOutputs(new[] { TestItems.Wood }).Should().BeEmpty();
        }

        [TestMethod]
        public void Reachable_FindsAnIngredientWithNoSource()
        {
            // The most common way a crafting tree breaks, and invisible by inspection past a few dozen
            // entries: a recipe whose ingredient nothing produces.
            var book = new RecipeBook();
            book.Add(Shapeless("planks", Plank, 4, new Ingredient(TestItems.Wood, 1)));
            book.Add(Shapeless("impossible", Handle, 1, new Ingredient(TestItems.Ingot, 1)));

            book.UnreachableOutputs(new[] { TestItems.Wood })
                .Should().BeEquivalentTo(new[] { Handle }, "nothing produces ingots");
        }

        [TestMethod]
        public void Reachable_FindsACircularDependency()
        {
            // A needs B, B needs A: both look perfectly reasonable on their own.
            var book = new RecipeBook();
            book.Add(Shapeless("a_from_b", Plank, 1, new Ingredient(Stick, 1)));
            book.Add(Shapeless("b_from_a", Stick, 1, new Ingredient(Plank, 1)));

            book.UnreachableOutputs(new[] { TestItems.Wood })
                .Should().BeEquivalentTo(new[] { Plank, Stick });
        }

        [TestMethod]
        public void Reachable_RespectsWhichStationsAreAvailable()
        {
            // Progression is gated by the stations a player has built, so the reachability query has to be
            // able to ask "what can I make with only a bench?"
            var book = new RecipeBook();
            book.Add(Shapeless("planks", Plank, 4, new Ingredient(TestItems.Wood, 1)));
            book.Add(new Recipe
            {
                Name = "smelted",
                Kind = RecipeKind.Process,
                Station = "furnace",
                Inputs = new[] { new Ingredient(Plank, 1) },
                Outputs = new[] { new ItemStack(TestItems.Ingot, 1) },
            });

            var benchOnly = new HashSet<string> { "bench" };
            book.Reachable(new[] { TestItems.Wood }, benchOnly).Should().NotContain(TestItems.Ingot);
            book.Reachable(new[] { TestItems.Wood }).Should().Contain(TestItems.Ingot);
        }
    }
}
