namespace RP.Game.Mechanics
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// One row of a <see cref="Menu"/> (build brief S21.2): a label plus either an action to run or a submenu
    /// to open. Disabled items are shown but skipped by navigation (e.g. "Continue" with no save).
    /// </summary>
    public sealed class MenuItem
    {
        /// <summary>An action row: selecting it runs <paramref name="onActivate"/>.</summary>
        public MenuItem(string label, Action onActivate)
        {
            Label = label;
            OnActivate = onActivate;
        }

        /// <summary>A submenu row: selecting it opens <paramref name="submenu"/>.</summary>
        public MenuItem(string label, Menu submenu)
        {
            Label = label;
            Submenu = submenu;
        }

        public string Label { get; }
        public Action? OnActivate { get; }
        public Menu? Submenu { get; }

        /// <summary>Greyed-out items are visible but cannot be selected or activated.</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// A single menu screen: an ordered list of <see cref="MenuItem"/>s with a moving selection. Navigation
    /// wraps around and skips disabled rows so the cursor never lands somewhere it can't act. This is pure
    /// model — no input or rendering — so the UI layer maps keys/sticks onto <see cref="MoveUp"/>/<see cref="MoveDown"/>
    /// and draws <see cref="Items"/> with <see cref="SelectedIndex"/> highlighted.
    /// </summary>
    public sealed class Menu
    {
        private readonly List<MenuItem> _items;

        public Menu(string title, params MenuItem[] items)
        {
            Title = title;
            _items = items.ToList();
            SelectedIndex = FirstEnabledFrom(0, +1) ?? 0;
        }

        public string Title { get; }

        public IReadOnlyList<MenuItem> Items => _items;

        public int SelectedIndex { get; private set; }

        public MenuItem? Selected => _items.Count == 0 ? null : _items[SelectedIndex];

        /// <summary>Moves the cursor to the next enabled row below (wrapping).</summary>
        public void MoveDown() => Step(+1);

        /// <summary>Moves the cursor to the next enabled row above (wrapping).</summary>
        public void MoveUp() => Step(-1);

        private void Step(int direction)
        {
            int? next = FirstEnabledFrom(Wrap(SelectedIndex + direction), direction);
            if (next.HasValue) SelectedIndex = next.Value;
        }

        // Scans from index in the given direction (wrapping) for the first enabled item; null if none exist.
        private int? FirstEnabledFrom(int index, int direction)
        {
            if (_items.Count == 0) return null;
            for (int n = 0; n < _items.Count; n++)
            {
                int probe = Wrap(index + n * direction);
                if (_items[probe].Enabled) return probe;
            }

            return null;
        }

        private int Wrap(int index)
        {
            int count = _items.Count;
            return ((index % count) + count) % count;
        }
    }

    /// <summary>
    /// Drives navigation across a tree of <see cref="Menu"/>s: it keeps a stack of open menus, so opening a
    /// submenu pushes and <see cref="Back"/> pops back to where you were (the universal "Settings → … → back"
    /// flow). <see cref="Activate"/> either runs the selected action or descends into its submenu.
    /// </summary>
    public sealed class MenuController
    {
        private readonly Stack<Menu> _stack = new();

        public MenuController(Menu root)
        {
            _stack.Push(root);
        }

        /// <summary>The menu currently on screen.</summary>
        public Menu Current => _stack.Peek();

        /// <summary>How deep we are in the menu tree (1 = at the root).</summary>
        public int Depth => _stack.Count;

        public void MoveUp() => Current.MoveUp();

        public void MoveDown() => Current.MoveDown();

        /// <summary>Opens the selected submenu, or runs the selected action. Disabled/empty selections do nothing.</summary>
        public void Activate()
        {
            MenuItem? item = Current.Selected;
            if (item is null || !item.Enabled) return;

            if (item.Submenu is not null) _stack.Push(item.Submenu);
            else item.OnActivate?.Invoke();
        }

        /// <summary>Pops back to the parent menu; returns false if already at the root.</summary>
        public bool Back()
        {
            if (_stack.Count <= 1) return false;
            _stack.Pop();
            return true;
        }

        /// <summary>Collapses straight back to the root menu.</summary>
        public void Reset()
        {
            while (_stack.Count > 1) _stack.Pop();
        }
    }
}
