namespace RP.Game.Mechanics.Power
{
    using System;
    using System.Collections.Generic;

    /// <summary>What a node does with energy.</summary>
    public enum EnergyRole
    {
        /// <summary>Carries energy but neither makes nor uses it.</summary>
        Conduit,

        /// <summary>Makes energy.</summary>
        Source,

        /// <summary>Uses energy.</summary>
        Load,

        /// <summary>Holds energy, absorbing surplus and covering shortfall.</summary>
        Store,
    }

    /// <summary>One thing attached to a power network.</summary>
    /// <remarks>
    /// Mutable and pooled by the network rather than allocated per tick: a large base has thousands of
    /// these and they are visited every tick, so churning them would dominate the simulation.
    /// </remarks>
    public sealed class EnergyNode
    {
        /// <summary>A stable identity, supplied by the game — usually a packed block position.</summary>
        public long Id { get; internal set; }

        /// <summary>What this node does.</summary>
        public EnergyRole Role { get; set; } = EnergyRole.Conduit;

        /// <summary>How much this node can supply per tick, if it is a source.</summary>
        public double SupplyRate { get; set; }

        /// <summary>How much this node wants per tick, if it is a load.</summary>
        public double DemandRate { get; set; }

        /// <summary>How much this node is holding, if it stores.</summary>
        public double Stored { get; set; }

        /// <summary>The most it can hold.</summary>
        public double Capacity { get; set; }

        /// <summary>How much it actually received this tick. Read by the game to decide whether to run.</summary>
        public double Received { get; internal set; }

        /// <summary>Whether it got everything it asked for this tick.</summary>
        public bool Satisfied { get; internal set; }

        /// <summary>Which network it currently belongs to, or -1.</summary>
        public int NetworkId { get; internal set; } = -1;

        internal void Reset()
        {
            Received = 0;
            Satisfied = false;
        }
    }

    /// <summary>
    /// The power grid: a set of nodes connected by adjacency, solved once per tick into "who got what".
    /// </summary>
    /// <remarks>
    /// <para><b>Fair-share, not first-come.</b> When supply falls short, every load is served the same
    /// fraction of what it asked for rather than the first few being served in full. First-come is simpler
    /// and produces the behaviour players hate most in a factory game: a base that works perfectly until it
    /// is one generator short, at which point some machines stop entirely and which ones depends on
    /// internal ordering nobody can see. Proportional starvation is visible, diagnosable, and degrades
    /// smoothly — everything slows down together, which is a signal rather than a mystery.</para>
    ///
    /// <para><b>Storage is the buffer, not the battery.</b> Stores absorb surplus after loads are served
    /// and cover shortfall before loads are starved. That single ordering is what makes a bank of
    /// accumulators smooth over a generator that burns fuel in bursts, which is the entire reason to build
    /// one.</para>
    ///
    /// <para><b>Connectivity is rebuilt lazily.</b> Networks are found by flood fill over adjacency, and
    /// only when something has changed. A base whose layout is static — which is most of the time — pays
    /// nothing for the graph and only for the per-tick balance.</para>
    /// </remarks>
    public sealed class EnergyNetwork
    {
        private readonly Dictionary<long, EnergyNode> _nodes = new Dictionary<long, EnergyNode>();
        private readonly Dictionary<long, List<long>> _links = new Dictionary<long, List<long>>();
        private readonly List<List<EnergyNode>> _networks = new List<List<EnergyNode>>();

        private bool _topologyDirty = true;

        /// <summary>How many nodes are attached.</summary>
        public int NodeCount => _nodes.Count;

        /// <summary>How many separate, unconnected grids exist. Rebuilt on demand.</summary>
        public int NetworkCount
        {
            get
            {
                RebuildIfNeeded();
                return _networks.Count;
            }
        }

        /// <summary>Total energy supplied across all grids on the last tick.</summary>
        public double LastSupplied { get; private set; }

        /// <summary>Total energy demanded across all grids on the last tick.</summary>
        public double LastDemanded { get; private set; }

        /// <summary>
        /// How much charge is banked across the one grid a node belongs to.
        /// </summary>
        /// <remarks>
        /// Per grid rather than across all of them, because two unconnected networks are two networks: a
        /// generator on the far side of the world must not quietly pay for something here. Anyone asking
        /// this is about to spend it, and needs to know what <i>this</i> grid can afford.
        /// </remarks>
        public double StoredIn(long nodeId)
        {
            RebuildIfNeeded();
            if (!_nodes.TryGetValue(nodeId, out EnergyNode? node) || node.NetworkId < 0) return 0.0;

            double total = 0;
            foreach (EnergyNode member in _networks[node.NetworkId]) total += member.Stored;
            return total;
        }

        /// <summary>
        /// Takes a lump of charge out of one grid's storage, all at once.
        /// </summary>
        /// <remarks>
        /// <para>The tick model is a rate model: supply and demand per second, fairly shared. That is right
        /// for a furnace and wrong for anything that happens in an instant and costs a lot -- a jump across
        /// a continent is not a machine that runs slowly, it is a bill. Drawn proportionally from every
        /// bank on the grid so no single battery is emptied first, which would make the behaviour depend on
        /// dictionary order.</para>
        ///
        /// <para>All or nothing. A partial draw would leave the caller having paid for something that did
        /// not happen, and every caller would have to write the refund.</para>
        /// </remarks>
        /// <returns>Whether the charge was there and has been taken.</returns>
        public bool DrawStored(long nodeId, double amount)
        {
            if (amount <= 0) return true;

            RebuildIfNeeded();
            if (!_nodes.TryGetValue(nodeId, out EnergyNode? node) || node.NetworkId < 0) return false;

            List<EnergyNode> grid = _networks[node.NetworkId];

            double available = 0;
            foreach (EnergyNode member in grid) available += member.Stored;
            if (available < amount) return false;

            double share = amount / available;
            foreach (EnergyNode member in grid) member.Stored -= member.Stored * share;

            return true;
        }

        /// <summary>Adds or replaces a node.</summary>
        public EnergyNode Attach(long id)
        {
            if (_nodes.TryGetValue(id, out EnergyNode? existing)) return existing;

            var node = new EnergyNode { Id = id };
            _nodes[id] = node;
            _topologyDirty = true;
            return node;
        }

        /// <summary>Removes a node and every link to it.</summary>
        public void Detach(long id)
        {
            if (!_nodes.Remove(id)) return;

            if (_links.TryGetValue(id, out List<long>? neighbours))
            {
                foreach (long other in neighbours)
                {
                    if (_links.TryGetValue(other, out List<long>? back)) back.Remove(id);
                }

                _links.Remove(id);
            }

            _topologyDirty = true;
        }

        /// <summary>The node with an id, or null.</summary>
        public EnergyNode? Get(long id) => _nodes.TryGetValue(id, out EnergyNode? node) ? node : null;

        /// <summary>Connects two nodes. Connection is symmetric and idempotent.</summary>
        public void Link(long a, long b)
        {
            if (a == b || !_nodes.ContainsKey(a) || !_nodes.ContainsKey(b)) return;

            AddLink(a, b);
            AddLink(b, a);
            _topologyDirty = true;
        }

        /// <summary>Disconnects two nodes.</summary>
        public void Unlink(long a, long b)
        {
            if (_links.TryGetValue(a, out List<long>? fromA)) fromA.Remove(b);
            if (_links.TryGetValue(b, out List<long>? fromB)) fromB.Remove(a);
            _topologyDirty = true;
        }

        /// <summary>Forces the connectivity graph to be rebuilt before the next tick.</summary>
        public void MarkTopologyChanged() => _topologyDirty = true;

        /// <summary>
        /// Balances every grid for one tick: sources supply, loads draw, stores take up the slack.
        /// </summary>
        /// <param name="dt">The timestep in seconds. Rates are per second.</param>
        public void Tick(double dt)
        {
            if (dt <= 0) return;

            RebuildIfNeeded();

            LastSupplied = 0;
            LastDemanded = 0;

            foreach (EnergyNode node in _nodes.Values) node.Reset();

            foreach (List<EnergyNode> grid in _networks) TickGrid(grid, dt);
        }

        private void TickGrid(List<EnergyNode> grid, double dt)
        {
            double available = 0;
            double demand = 0;

            for (int i = 0; i < grid.Count; i++)
            {
                EnergyNode node = grid[i];
                if (node.Role == EnergyRole.Source) available += node.SupplyRate * dt;
                else if (node.Role == EnergyRole.Load) demand += node.DemandRate * dt;
            }

            LastSupplied += available;
            LastDemanded += demand;

            // Shortfall comes out of storage before any load is starved -- that is what an accumulator is
            // for, and serving loads first would leave a full battery beside a stopped machine.
            if (available < demand)
            {
                double shortfall = demand - available;
                for (int i = 0; i < grid.Count && shortfall > 0; i++)
                {
                    EnergyNode node = grid[i];
                    if (node.Role != EnergyRole.Store || node.Stored <= 0) continue;

                    double drawn = System.Math.Min(node.Stored, shortfall);
                    node.Stored -= drawn;
                    available += drawn;
                    shortfall -= drawn;
                }
            }

            // Fair share: everyone gets the same fraction of what they asked for. See the type remarks for
            // why this is not first-come.
            double fraction = demand <= 0 ? 1.0 : System.Math.Min(1.0, available / demand);

            for (int i = 0; i < grid.Count; i++)
            {
                EnergyNode node = grid[i];
                if (node.Role != EnergyRole.Load) continue;

                double wanted = node.DemandRate * dt;
                node.Received = wanted * fraction;
                node.Satisfied = fraction >= 0.999;
                available -= node.Received;
            }

            // Whatever is left charges the stores, up to their capacity.
            for (int i = 0; i < grid.Count && available > 0; i++)
            {
                EnergyNode node = grid[i];
                if (node.Role != EnergyRole.Store) continue;

                double room = node.Capacity - node.Stored;
                if (room <= 0) continue;

                double put = System.Math.Min(room, available);
                node.Stored += put;
                available -= put;
            }
        }

        private void AddLink(long from, long to)
        {
            if (!_links.TryGetValue(from, out List<long>? list))
            {
                list = new List<long>(6);
                _links[from] = list;
            }

            if (!list.Contains(to)) list.Add(to);
        }

        /// <summary>Flood-fills the adjacency graph into separate grids.</summary>
        private void RebuildIfNeeded()
        {
            if (!_topologyDirty) return;
            _topologyDirty = false;

            _networks.Clear();
            foreach (EnergyNode node in _nodes.Values) node.NetworkId = -1;

            var stack = new Stack<long>();

            foreach (KeyValuePair<long, EnergyNode> entry in _nodes)
            {
                if (entry.Value.NetworkId >= 0) continue;

                int id = _networks.Count;
                var grid = new List<EnergyNode>();
                _networks.Add(grid);

                stack.Push(entry.Key);
                entry.Value.NetworkId = id;

                while (stack.Count > 0)
                {
                    long current = stack.Pop();
                    if (!_nodes.TryGetValue(current, out EnergyNode? node)) continue;

                    grid.Add(node);

                    if (!_links.TryGetValue(current, out List<long>? neighbours)) continue;
                    for (int i = 0; i < neighbours.Count; i++)
                    {
                        long next = neighbours[i];
                        if (!_nodes.TryGetValue(next, out EnergyNode? other) || other.NetworkId >= 0) continue;

                        other.NetworkId = id;
                        stack.Push(next);
                    }
                }
            }
        }
    }
}
