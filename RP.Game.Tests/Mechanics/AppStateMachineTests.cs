namespace RP.Game.Tests.Mechanics
{
    using FluentAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using RP.Game.Mechanics;

    [TestClass]
    public sealed class AppStateMachineTests
    {
        [TestMethod]
        public void Boot_CanGoToMainMenu_ButNotStraightToPlaying()
        {
            var sm = new AppStateMachine(AppState.Boot);
            sm.CanTransitionTo(AppState.MainMenu).Should().BeTrue();
            sm.CanTransitionTo(AppState.Playing).Should().BeFalse();
            sm.CanTransitionTo(AppState.Paused).Should().BeFalse();
        }

        [TestMethod]
        public void TypicalFlow_BootMenuPlayPauseResume()
        {
            var sm = new AppStateMachine(AppState.Boot);
            sm.TryTransitionTo(AppState.MainMenu).Should().BeTrue();
            sm.TryTransitionTo(AppState.Playing).Should().BeTrue();
            sm.IsSimulating.Should().BeTrue();
            sm.TryTransitionTo(AppState.Paused).Should().BeTrue();
            sm.IsSimulating.Should().BeFalse();
            sm.TryTransitionTo(AppState.Playing).Should().BeTrue(); // resume
            sm.Current.Should().Be(AppState.Playing);
        }

        [TestMethod]
        public void IllegalTransition_IsRejectedAndStateUnchanged()
        {
            var sm = new AppStateMachine(AppState.MainMenu);
            sm.TryTransitionTo(AppState.Paused).Should().BeFalse();
            sm.Current.Should().Be(AppState.MainMenu);
        }

        [TestMethod]
        public void Exiting_IsTerminal()
        {
            var sm = new AppStateMachine(AppState.Playing);
            sm.TryTransitionTo(AppState.Exiting).Should().BeTrue();
            sm.TryTransitionTo(AppState.MainMenu).Should().BeFalse();
        }

        [TestMethod]
        public void StateChanged_FiresWithFromAndTo()
        {
            var sm = new AppStateMachine(AppState.Boot);
            AppState? from = null, to = null;
            sm.StateChanged += (f, t) => { from = f; to = t; };

            sm.TryTransitionTo(AppState.MainMenu);

            from.Should().Be(AppState.Boot);
            to.Should().Be(AppState.MainMenu);
        }
    }
}
