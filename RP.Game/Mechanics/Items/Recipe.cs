namespace RP.Game.Mechanics.Items
{
    using System;
    using System.Collections.Generic;

    /// <summary>How a recipe's inputs are arranged, and therefore how it is matched.</summary>
    public enum RecipeKind
    {
        /// <summary>The inputs matter, their arrangement does not. Most recipes.</summary>
        Shapeless,

        /// <summary>
        /// The arrangement matters. Used sparingly and on purpose: a shaped recipe is a small puzzle the
        /// player has to be taught, so it should be reserved for things whose shape <i>means</i> something —
        /// a pick is a bar across the top and a handle below it — rather than applied to everything.
        /// </summary>
        Shaped,

        /// <summary>
        /// One thing becomes another over time, in a machine: smelting, alloying, grinding, refining.
        /// Carries a duration and an energy cost rather than a grid.
        /// </summary>
        Process,
    }

    /// <summary>One input requirement: some number of an item.</summary>
    public readonly struct Ingredient
    {
        /// <summary>Which item.</summary>
        public readonly int ItemId;

        /// <summary>How many.</summary>
        public readonly int Count;

        /// <summary>Creates an ingredient.</summary>
        public Ingredient(int itemId, int count = 1)
        {
            ItemId = itemId;
            Count = count < 1 ? 1 : count;
        }

        /// <inheritdoc />
        public override string ToString() => $"{Count}x#{ItemId}";
    }

    /// <summary>
    /// One recipe: what goes in, what comes out, where it can be made, and how long it takes.
    /// </summary>
    /// <remarks>
    /// <para><b>Recipes have several outputs.</b> Not a convenience — a refining chain that only ever
    /// produces one thing has no texture. Smelting copper ore gives copper and slag; smelting it with the
    /// right flux gives more copper and less slag. Byproducts are what make a processing tier worth
    /// upgrading, and building them in from the start avoids retrofitting every machine later.</para>
    /// <para><b>The station is a string.</b> An enum would fix the machine list at engine level, and a game
    /// adds stations constantly. Matching is by ordinal comparison, so it costs a reference check in the
    /// common case where the same interned literal is on both sides.</para>
    /// </remarks>
    public sealed class Recipe
    {
        /// <summary>A stable identifier, for saves, logs and the recipe book's own index.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>How this recipe is matched.</summary>
        public RecipeKind Kind { get; init; } = RecipeKind.Shapeless;

        /// <summary>Where it can be made: a hand, a bench, a furnace, a forge.</summary>
        public string Station { get; init; } = "hand";

        /// <summary>What it consumes.</summary>
        public Ingredient[] Inputs { get; init; } = Array.Empty<Ingredient>();

        /// <summary>What it produces. The first is the primary output; the rest are byproducts.</summary>
        public ItemStack[] Outputs { get; init; } = Array.Empty<ItemStack>();

        /// <summary>
        /// For <see cref="RecipeKind.Shaped"/>: the arrangement, as rows of characters keyed by
        /// <see cref="Key"/>. A space means "this cell must be empty".
        /// </summary>
        public string[]? Pattern { get; init; }

        /// <summary>For <see cref="RecipeKind.Shaped"/>: what each character in the pattern means.</summary>
        public IReadOnlyDictionary<char, int>? Key { get; init; }

        /// <summary>For <see cref="RecipeKind.Process"/>: how long it takes, in seconds, at base speed.</summary>
        public double Duration { get; init; } = 1.0;

        /// <summary>For <see cref="RecipeKind.Process"/>: how much energy it consumes in total.</summary>
        public double Energy { get; init; }

        /// <summary>
        /// How advanced this recipe is. Used to sort a recipe list and to check that progression has no
        /// gaps, not to gate anything — gating is the station's job.
        /// </summary>
        public int Tier { get; init; }

        /// <summary>The primary output.</summary>
        public ItemStack PrimaryOutput => Outputs.Length > 0 ? Outputs[0] : ItemStack.Empty;

        /// <summary>The pattern's width, or 0 if unshaped.</summary>
        public int PatternWidth
        {
            get
            {
                if (Pattern is null || Pattern.Length == 0) return 0;

                int width = 0;
                foreach (string row in Pattern)
                {
                    if (row.Length > width) width = row.Length;
                }

                return width;
            }
        }

        /// <summary>The pattern's height, or 0 if unshaped.</summary>
        public int PatternHeight => Pattern?.Length ?? 0;

        /// <summary>
        /// The inputs as a flat item-to-count map, whatever kind the recipe is. This is what consumption
        /// and availability checks work from, so a shaped recipe and a shapeless one with the same
        /// ingredients cost exactly the same to make.
        /// </summary>
        public Dictionary<int, int> TotalInputs()
        {
            var totals = new Dictionary<int, int>();

            if (Kind == RecipeKind.Shaped && Pattern is not null && Key is not null)
            {
                foreach (string row in Pattern)
                {
                    foreach (char symbol in row)
                    {
                        if (symbol == ' ') continue;
                        if (!Key.TryGetValue(symbol, out int itemId)) continue;

                        totals.TryGetValue(itemId, out int existing);
                        totals[itemId] = existing + 1;
                    }
                }

                return totals;
            }

            foreach (Ingredient input in Inputs)
            {
                totals.TryGetValue(input.ItemId, out int existing);
                totals[input.ItemId] = existing + input.Count;
            }

            return totals;
        }

        /// <inheritdoc />
        public override string ToString() => $"{Name} @{Station}";
    }
}
