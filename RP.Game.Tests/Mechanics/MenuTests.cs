namespace RP.Game.Tests.Mechanics
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Mechanics;

    /// <summary>
    /// The menu navigation model (build brief S21.2): a moving selection that wraps and skips disabled rows,
    /// and a controller that descends into submenus and backs out again. Pure model — no input or rendering.
    /// </summary>
    [TestClass]
    public sealed class MenuTests
    {
        [TestMethod]
        public void MoveDown_AdvancesAndWrapsAround()
        {
            var menu = new Menu("Main",
                new MenuItem("A", () => { }),
                new MenuItem("B", () => { }),
                new MenuItem("C", () => { }));

            menu.SelectedIndex.Should().Be(0);
            menu.MoveDown();
            menu.SelectedIndex.Should().Be(1);
            menu.MoveDown();
            menu.MoveDown(); // past the end -> wraps to 0
            menu.SelectedIndex.Should().Be(0);
        }

        [TestMethod]
        public void MoveUp_FromTop_WrapsToBottom()
        {
            var menu = new Menu("Main", new MenuItem("A", () => { }), new MenuItem("B", () => { }));
            menu.MoveUp();
            menu.SelectedIndex.Should().Be(1);
        }

        [TestMethod]
        public void Navigation_SkipsDisabledRows()
        {
            var menu = new Menu("Main",
                new MenuItem("New", () => { }),
                new MenuItem("Continue", () => { }) { Enabled = false }, // no save
                new MenuItem("Quit", () => { }));

            menu.SelectedIndex.Should().Be(0); // starts on the first enabled row
            menu.MoveDown();
            menu.Selected!.Label.Should().Be("Quit"); // skipped the disabled "Continue"
        }

        [TestMethod]
        public void Activate_RunsTheSelectedAction()
        {
            bool ran = false;
            var menu = new Menu("Main", new MenuItem("Go", () => ran = true));
            var controller = new MenuController(menu);

            controller.Activate();
            ran.Should().BeTrue();
        }

        [TestMethod]
        public void Activate_OnDisabledRow_DoesNothing()
        {
            bool ran = false;
            var menu = new Menu("Main", new MenuItem("Go", () => ran = true) { Enabled = false });
            new MenuController(menu).Activate();
            ran.Should().BeFalse();
        }

        [TestMethod]
        public void Submenu_PushesAndBackPops()
        {
            var settings = new Menu("Settings", new MenuItem("Master volume", () => { }));
            var main = new Menu("Main",
                new MenuItem("Settings", settings),
                new MenuItem("Quit", () => { }));
            var controller = new MenuController(main);

            controller.Activate(); // open Settings
            controller.Depth.Should().Be(2);
            controller.Current.Title.Should().Be("Settings");

            controller.Back().Should().BeTrue();
            controller.Depth.Should().Be(1);
            controller.Current.Title.Should().Be("Main");

            controller.Back().Should().BeFalse(); // already at root
        }

        [TestMethod]
        public void Reset_CollapsesToRoot()
        {
            var deep = new Menu("Deep", new MenuItem("x", () => { }));
            var mid = new Menu("Mid", new MenuItem("Deeper", deep));
            var root = new Menu("Root", new MenuItem("Down", mid));
            var controller = new MenuController(root);

            controller.Activate(); // -> Mid
            controller.Activate(); // -> Deep
            controller.Depth.Should().Be(3);

            controller.Reset();
            controller.Depth.Should().Be(1);
            controller.Current.Title.Should().Be("Root");
        }
    }
}
