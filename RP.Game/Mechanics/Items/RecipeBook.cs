namespace RP.Game.Mechanics.Items
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Every recipe in the game, indexed for the three questions anyone actually asks: what can I make
    /// here, does this arrangement make anything, and can I afford it.
    /// </summary>
    /// <remarks>
    /// <para><b>Indexed by station, then by output.</b> A linear scan over every recipe is fine at a hundred
    /// and unacceptable at a thousand, and a recipe list only ever grows. Both indexes are built at
    /// registration, so lookup is a dictionary probe rather than a search.</para>
    ///
    /// <para><b>Reachability is a first-class query.</b> <see cref="Reachable"/> answers "starting with
    /// nothing but what the world gives you, which items can ever be obtained?" — and the complement of its
    /// answer is the set of things a player can see in the recipe list and never make. That is the single
    /// most common way a crafting tree breaks: a recipe whose ingredient is itself only produced by a
    /// machine that requires that recipe. It is invisible by inspection once the tree is more than a few
    /// dozen entries, and trivial to catch with a closure.</para>
    /// </remarks>
    public sealed class RecipeBook
    {
        private readonly List<Recipe> _all = new List<Recipe>();
        private readonly Dictionary<string, List<Recipe>> _byStation = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
        private readonly Dictionary<int, List<Recipe>> _byOutput = new Dictionary<int, List<Recipe>>();
        private readonly Dictionary<string, Recipe> _byName = new Dictionary<string, Recipe>(StringComparer.Ordinal);

        /// <summary>Every registered recipe.</summary>
        public IReadOnlyList<Recipe> All => _all;

        /// <summary>How many recipes are registered.</summary>
        public int Count => _all.Count;

        /// <summary>Registers a recipe.</summary>
        public void Add(Recipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            if (string.IsNullOrEmpty(recipe.Name)) throw new ArgumentException("A recipe needs a name.", nameof(recipe));
            if (_byName.ContainsKey(recipe.Name)) throw new ArgumentException($"Recipe '{recipe.Name}' is already registered.", nameof(recipe));
            if (recipe.Outputs.Length == 0) throw new ArgumentException($"Recipe '{recipe.Name}' produces nothing.", nameof(recipe));

            _all.Add(recipe);
            _byName[recipe.Name] = recipe;

            if (!_byStation.TryGetValue(recipe.Station, out List<Recipe>? station))
            {
                station = new List<Recipe>();
                _byStation[recipe.Station] = station;
            }

            station.Add(recipe);

            foreach (ItemStack output in recipe.Outputs)
            {
                if (output.IsEmpty) continue;
                if (!_byOutput.TryGetValue(output.ItemId, out List<Recipe>? producers))
                {
                    producers = new List<Recipe>();
                    _byOutput[output.ItemId] = producers;
                }

                producers.Add(recipe);
            }
        }

        /// <summary>A recipe by name.</summary>
        public Recipe? ByName(string name) => _byName.TryGetValue(name, out Recipe? recipe) ? recipe : null;

        /// <summary>Every recipe that can be made at a station.</summary>
        public IReadOnlyList<Recipe> AtStation(string station)
            => _byStation.TryGetValue(station, out List<Recipe>? recipes) ? recipes : Array.Empty<Recipe>();

        /// <summary>Every recipe that produces an item.</summary>
        public IReadOnlyList<Recipe> Producing(int itemId)
            => _byOutput.TryGetValue(itemId, out List<Recipe>? recipes) ? recipes : Array.Empty<Recipe>();

        /// <summary>Whether an inventory holds everything a recipe needs.</summary>
        public static bool CanCraft(Recipe recipe, Inventory source)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            if (source == null) throw new ArgumentNullException(nameof(source));

            foreach (KeyValuePair<int, int> need in recipe.TotalInputs())
            {
                if (source.CountOf(need.Key) < need.Value) return false;
            }

            return true;
        }

        /// <summary>
        /// Consumes a recipe's inputs and produces its outputs, or does nothing at all.
        /// </summary>
        /// <remarks>
        /// <para>All or nothing, and the output check comes <i>before</i> the inputs are consumed. Crafting
        /// into a full pack and destroying the ingredients is a bug players never forgive, and it is the
        /// default behaviour of the obvious implementation, which takes the inputs and then discovers there
        /// is nowhere to put the result.</para>
        /// <para>The output goes to <paramref name="destination"/>, which may be the same inventory —
        /// consuming first means the space the inputs occupied is available for the result, which is what
        /// makes crafting in a full pack work at all when the recipe shrinks the item count.</para>
        /// </remarks>
        /// <returns>True if the craft happened.</returns>
        public static bool Craft(Recipe recipe, Inventory source, Inventory? destination = null)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            if (source == null) throw new ArgumentNullException(nameof(source));

            destination ??= source;
            if (!CanCraft(recipe, source)) return false;

            // Check the result will fit before touching the inputs. When crafting into the same inventory
            // the inputs are about to free space, so a strict check would refuse valid crafts -- hence the
            // allowance for what is about to be freed.
            if (!WillOutputFit(recipe, source, destination)) return false;

            foreach (KeyValuePair<int, int> need in recipe.TotalInputs())
            {
                int removed = source.Remove(need.Key, need.Value);
                if (removed == need.Value) continue;

                // Cannot happen after CanCraft, but if it ever did, silently eating the ingredients would
                // be the worst possible response.
                throw new InvalidOperationException(
                    $"Recipe '{recipe.Name}' consumed {removed} of item {need.Key}, expected {need.Value}.");
            }

            foreach (ItemStack output in recipe.Outputs)
            {
                if (output.IsEmpty) continue;

                ItemStack leftover = destination.Add(output);
                if (!leftover.IsEmpty)
                {
                    throw new InvalidOperationException(
                        $"Recipe '{recipe.Name}' produced more than would fit, despite the check.");
                }
            }

            return true;
        }

        /// <summary>
        /// Finds the shapeless or shaped recipe a grid of slots matches at a station, or null.
        /// </summary>
        /// <remarks>
        /// Shaped recipes are tried first. A shaped recipe is necessarily more specific than a shapeless one
        /// with the same ingredients, so trying shapeless first would let a loose pile of parts satisfy a
        /// recipe whose whole point is the arrangement.
        /// </remarks>
        public Recipe? MatchGrid(string station, Inventory grid, int width, int height)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));

            IReadOnlyList<Recipe> candidates = AtStation(station);

            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Kind == RecipeKind.Shaped && MatchesShaped(candidates[i], grid, width, height))
                {
                    return candidates[i];
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Kind == RecipeKind.Shapeless && MatchesShapeless(candidates[i], grid))
                {
                    return candidates[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Everything obtainable, starting from a set of items the world itself provides.
        /// </summary>
        /// <remarks>
        /// <para>A straightforward closure: repeatedly add the output of every recipe whose inputs are all
        /// already obtainable, until nothing new appears. Cheap, because it converges in as many passes as
        /// the deepest chain is long.</para>
        /// <para>What it is <i>for</i> is the complement. Anything registered as an output and missing from
        /// this set can never be made, which means either an ingredient has no source or the tree contains a
        /// cycle — a recipe that needs a machine that needs that recipe. Both are invisible by reading once
        /// the tree is more than a few dozen entries deep, and both are certain to happen while a
        /// progression is being balanced.</para>
        /// </remarks>
        /// <param name="rawItems">What the world gives you without crafting: what blocks drop when mined,
        /// what creatures leave, what grows.</param>
        /// <param name="stations">Which stations are available. Null means all of them.</param>
        public HashSet<int> Reachable(IEnumerable<int> rawItems, ISet<string>? stations = null)
        {
            if (rawItems == null) throw new ArgumentNullException(nameof(rawItems));

            var have = new HashSet<int>(rawItems);
            bool grew = true;

            while (grew)
            {
                grew = false;

                for (int i = 0; i < _all.Count; i++)
                {
                    Recipe recipe = _all[i];
                    if (stations is not null && !stations.Contains(recipe.Station)) continue;

                    bool satisfied = true;
                    foreach (KeyValuePair<int, int> need in recipe.TotalInputs())
                    {
                        if (have.Contains(need.Key)) continue;
                        satisfied = false;
                        break;
                    }

                    if (!satisfied) continue;

                    foreach (ItemStack output in recipe.Outputs)
                    {
                        if (output.IsEmpty) continue;
                        if (have.Add(output.ItemId)) grew = true;
                    }
                }
            }

            return have;
        }

        /// <summary>
        /// Every item that appears as an output of some recipe but can never actually be obtained from
        /// <paramref name="rawItems"/>. Empty means the progression has no dead ends.
        /// </summary>
        public List<int> UnreachableOutputs(IEnumerable<int> rawItems, ISet<string>? stations = null)
        {
            HashSet<int> have = Reachable(rawItems, stations);
            var unreachable = new List<int>();

            foreach (int itemId in _byOutput.Keys)
            {
                if (!have.Contains(itemId)) unreachable.Add(itemId);
            }

            unreachable.Sort();
            return unreachable;
        }

        private static bool WillOutputFit(Recipe recipe, Inventory source, Inventory destination)
        {
            // Crafting into a separate inventory is the simple case.
            if (!ReferenceEquals(source, destination))
            {
                foreach (ItemStack output in recipe.Outputs)
                {
                    if (!output.IsEmpty && !destination.CanFit(output)) return false;
                }

                return true;
            }

            // Into the same inventory, the inputs are about to free space. Rather than model that, check
            // whether the pack has any room at all, or the outputs merge into stacks already held -- which
            // together cover every real case and never destroy anything.
            foreach (ItemStack output in recipe.Outputs)
            {
                if (output.IsEmpty) continue;
                if (destination.CanFit(output)) continue;
                if (destination.CountFreeSlots() > 0) continue;

                // No free slot and it will not merge: the only hope is that an input frees a whole slot.
                bool freesASlot = false;
                foreach (KeyValuePair<int, int> need in recipe.TotalInputs())
                {
                    if (destination.CountOf(need.Key) == need.Value) { freesASlot = true; break; }
                }

                if (!freesASlot) return false;
            }

            return true;
        }

        private static bool MatchesShapeless(Recipe recipe, Inventory grid)
        {
            var needed = recipe.TotalInputs();
            var present = new Dictionary<int, int>();

            foreach ((int _, ItemStack stack) in grid.Contents())
            {
                present.TryGetValue(stack.ItemId, out int existing);
                present[stack.ItemId] = existing + stack.Count;
            }

            // Exact match both ways: a grid holding an extra ingredient is not this recipe, or a player
            // would craft something cheaper than they laid out and lose the difference.
            if (present.Count != needed.Count) return false;

            foreach (KeyValuePair<int, int> need in needed)
            {
                if (!present.TryGetValue(need.Key, out int count) || count != need.Value) return false;
            }

            return true;
        }

        private static bool MatchesShaped(Recipe recipe, Inventory grid, int width, int height)
        {
            if (recipe.Pattern is null || recipe.Key is null) return false;

            int patternWidth = recipe.PatternWidth;
            int patternHeight = recipe.PatternHeight;
            if (patternWidth > width || patternHeight > height) return false;

            // Try every offset: a two-by-two recipe laid out in the bottom-right of a three-by-three grid is
            // still that recipe, and insisting on the top-left is the kind of rule players find maddening.
            for (int offsetY = 0; offsetY <= height - patternHeight; offsetY++)
            {
                for (int offsetX = 0; offsetX <= width - patternWidth; offsetX++)
                {
                    if (MatchesAt(recipe, grid, width, height, offsetX, offsetY)) return true;
                }
            }

            return false;
        }

        private static bool MatchesAt(Recipe recipe, Inventory grid, int width, int height, int offsetX, int offsetY)
        {
            int patternWidth = recipe.PatternWidth;
            int patternHeight = recipe.PatternHeight;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int slot = (y * width) + x;
                    ItemStack held = grid[slot];

                    int patternX = x - offsetX;
                    int patternY = y - offsetY;

                    bool insidePattern = patternX >= 0 && patternX < patternWidth
                                      && patternY >= 0 && patternY < patternHeight;

                    char symbol = ' ';
                    if (insidePattern)
                    {
                        string row = recipe.Pattern![patternY];
                        symbol = patternX < row.Length ? row[patternX] : ' ';
                    }

                    if (symbol == ' ')
                    {
                        // Outside the shape, or a deliberate gap in it: the cell must be empty.
                        if (!held.IsEmpty) return false;
                        continue;
                    }

                    if (!recipe.Key!.TryGetValue(symbol, out int itemId)) return false;
                    if (held.IsEmpty || held.ItemId != itemId) return false;
                }
            }

            return true;
        }
    }
}
