using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class NavigationTests
{
    [Theory]
    [InlineData(0d, 1d, 0f)]
    [InlineData(1d, 0d, 90f)]
    [InlineData(0d, -1d, 180f)]
    [InlineData(-1d, 0d, 270f)]
    public void DesiredHeadingUsesVtankCompassConvention(
        double eastWest,
        double northSouth,
        float expected)
    {
        PluginNavigationPosition origin = Position(0d, 0d);
        PluginNavigationPosition target = Position(eastWest, northSouth);

        Assert.Equal(expected, NavigationController.DesiredHeading(origin, target));
    }

    [Theory]
    [InlineData(350f, 10f, 20f)]
    [InlineData(10f, 350f, -20f)]
    [InlineData(90f, 270f, 180f)]
    public void SignedHeadingDeltaChoosesShortestAuthenticTurn(
        float current,
        float desired,
        float expected) =>
        Assert.Equal(expected, NavigationController.SignedHeadingDelta(current, desired));

    /// <summary>
    /// Well outside the alignment band and far from the goal, the mover holds a
    /// turn key and does NOT walk: the far relaxation lets it walk while
    /// turning only once it is within 45 degrees. It never re-faces in the open
    /// world - that is the typing branch.
    /// </summary>
    [Fact]
    public void PointSteeringHoldsATurnKeyAndWaitsBeyondTheFarRelaxation()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.True(intent.TurnRight);
        Assert.False(intent.TurnLeft);
        Assert.False(intent.Forward);
        // Turning in place keeps the run key held, so the turn plays at its
        // fast rate; a route never turns as slowly as a walk. Mutation: tying
        // the run key to the move decision again turns this red.
        Assert.True(intent.Run);
        Assert.Empty(automation.FacedHeadings);
    }

    /// <summary>
    /// Far from the goal the mover walks while it turns as soon as the heading
    /// is within 45 degrees, so a long leg does not stop and start.
    /// </summary>
    [Theory]
    [InlineData(40f, true, false)]
    [InlineData(44f, true, false)]
    [InlineData(46f, true, true)]
    [InlineData(135f, false, true)]
    [InlineData(136f, false, false)]
    public void PointSteeringRelaxesToFortyFiveDegreesBeyondThreeMetres(
        float heading,
        bool turnRight,
        bool moves)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: heading)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.Equal(turnRight, intent.TurnRight);
        Assert.Equal(!turnRight, intent.TurnLeft);
        Assert.Equal(moves, intent.Forward);
    }

    /// <summary>
    /// Inside three metres the relaxation tightens to 15 degrees, so the last
    /// stretch is aimed before it is walked.
    /// </summary>
    [Theory]
    [InlineData(70f, false)]
    [InlineData(78f, true)]
    public void PointSteeringTightensToFifteenDegreesInsideThreeMetres(
        float heading,
        bool moves)
    {
        // 2.5 m east of the origin, inside the near tier and outside both the
        // creep band and the 2 m arrival radius.
        PluginNavigationPosition goal = Position(2.5d / 240d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: heading)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, goal));

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.True(intent.TurnRight);
        Assert.Equal(moves, intent.Forward);
    }

    /// <summary>
    /// Inside the creep band the mover walks rather than runs, which is the
    /// run flag off, not a separate modifier.
    /// </summary>
    [Fact]
    public void PointSteeringWalksInsideTheCreepBand()
    {
        // 1.0 m east: inside the 1.5 m creep band, outside the 0.5 m arrival.
        PluginNavigationPosition goal = Position(1d / 240d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 0.5d,
            Waypoint(RouteWaypointType.Point, goal));

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.True(intent.Forward);
        Assert.False(intent.Run);
    }

    /// <summary>
    /// An arrival radius below the old 0.5 m floor is honoured down to one
    /// steering step at a walk (about 0.15 m): 0.3 m from a point with a
    /// 0.15 m radius the mover still walks on, while a radius below one
    /// step is raised to it. Mutation: the 0.5 m floor stops at 0.3 m.
    /// </summary>
    [Theory]
    [InlineData(0.30d, 0.15d, true)]
    [InlineData(0.20d, 0.15d, true)]
    [InlineData(0.14d, 0.15d, false)]
    [InlineData(0.14d, 0.01d, false)]
    public void ANarrowArrivalRadiusIsHonouredDownToOneWalkingStep(
        double distanceMeters,
        double minimumDistanceMeters,
        bool walksOn)
    {
        PluginNavigationPosition goal = Position(distanceMeters / 240d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters,
            Waypoint(RouteWaypointType.Point, goal),
            Waypoint(RouteWaypointType.Point, Position(0d, 5d / 240d)));

        controller.Tick(0.05d, canAct: true);

        Assert.Equal(walksOn, automation.Intents.Any(static intent =>
            intent.Forward && !intent.Run));
    }

    /// <summary>
    /// Outside the creep band the mover runs.
    /// </summary>
    [Fact]
    public void PointSteeringRunsOutsideTheCreepBand()
    {
        PluginNavigationPosition goal = Position(2.5d / 240d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 0.5d,
            Waypoint(RouteWaypointType.Point, goal));

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.True(intent.Forward);
        Assert.True(intent.Run);
    }

    /// <summary>
    /// The mover runs on the host's frame, but only between the rule pass that
    /// armed it and the one that takes the turn away. Nothing else may make it
    /// steer, and losing the turn must stop it on the spot.
    /// </summary>
    [Fact]
    public void TheMoverStepsOnFramesOnlyWhileTheRuleHoldsItsTurn()
    {
        PluginNavigationPosition goal = Position(12d / 240d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, goal));

        controller.StepArmedMover(0.1d, navigationSlotsAreClear: true);
        Assert.Empty(automation.Intents);

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        int armed = automation.Intents.Count;
        Assert.Equal(1, armed);

        controller.StepArmedMover(0.1d, navigationSlotsAreClear: true);
        Assert.Equal(armed + 1, automation.Intents.Count);

        controller.StopForLostTurn();
        controller.StepArmedMover(0.1d, navigationSlotsAreClear: true);
        controller.StepArmedMover(0.1d, navigationSlotsAreClear: true);
        Assert.Equal(armed + 1, automation.Intents.Count);
    }

    /// <summary>
    /// The mover's own interval, not the host's frame rate, is what paces it:
    /// frames shorter than the interval accumulate rather than each producing
    /// a steer.
    /// </summary>
    [Fact]
    public void TheMoverStepsNoFasterThanItsOwnInterval()
    {
        PluginNavigationPosition goal = Position(12d / 240d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, goal));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        int armed = automation.Intents.Count;

        // Four frames of a hundredth of a second: three short of the interval,
        // then one that carries it past.
        for (int frame = 0; frame < 4; frame++)
            controller.StepArmedMover(0.01d, navigationSlotsAreClear: true);
        Assert.Equal(armed, automation.Intents.Count);

        controller.StepArmedMover(0.01d, navigationSlotsAreClear: true);
        Assert.Equal(armed + 1, automation.Intents.Count);
    }

    /// <summary>
    /// Half a turn is the one heading where the two arcs are equal. The original
    /// resolves it left.
    /// </summary>
    [Theory]
    [InlineData(0f, 180f, true)]
    [InlineData(0f, 90f, false)]
    [InlineData(0f, 270f, true)]
    [InlineData(350f, 10f, false)]
    [InlineData(10f, 350f, true)]
    public void TurnDirectionFollowsTheAuthenticAlignmentTest(
        float current,
        float desired,
        bool expectLeft) =>
        Assert.Equal(
            expectLeft,
            NavigationController.PrefersLeftTurn(current, desired));

    [Fact]
    public void PointSteeringFacesTheHeadingWhileThePlayerIsTyping()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
            ChatInputActive = true,
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Empty(automation.Intents);
        // A stop only releases what was being held, and nothing was: the very
        // first tick of a route was never moving.
        Assert.Equal(0, automation.ClearCount);
        Assert.Equal(90f, Assert.Single(automation.FacedHeadings));
    }

    /// <summary>
    /// The typing branch is the mover's other steering shape, not a different
    /// mover. Once it is aimed, it makes the same stop decision as any other
    /// branch, so a goal inside the creep band is walked to, not run at.
    /// </summary>
    [Fact]
    public void ThePlayerTypingStillWalksTheLastMetreInsteadOfRunningIt()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
            ChatInputActive = true,
        };
        // A metre east: aimed already, and inside the creep band.
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            0.5d,
            Waypoint(RouteWaypointType.Point, Position(1d / 240d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.True(intent.Forward);
        Assert.False(intent.Run);
        Assert.Empty(automation.FacedHeadings);
    }

    [Fact]
    public void PointSteeringDoesNotReissueFaceHeadingInsideSevenTenthsOfASecond()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
            ChatInputActive = true,
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(Frame(controller, 0.293d));
        Assert.True(Frame(controller, 0.293d));

        Assert.Equal(90f, Assert.Single(automation.FacedHeadings));

        Assert.True(Frame(controller, 0.293d));
        Assert.Single(automation.FacedHeadings);
        Assert.True(Frame(controller, 0.293d));
        Assert.Equal(2, automation.FacedHeadings.Count);
    }

    [Fact]
    public void PointSteeringRunsForwardInsideTheFourDegreeBand()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 88f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.True(intent.Forward);
        Assert.True(intent.Run);
        Assert.False(intent.TurnLeft);
        Assert.False(intent.TurnRight);
        Assert.Empty(automation.FacedHeadings);
    }

    /// <summary>
    /// A macro stop keeps the round's place, but it must not leave the
    /// character running: whatever the route was holding down is released,
    /// the same as on a lost turn. The three teardowns are layered so this
    /// cannot be forgotten in one of them.
    /// </summary>
    [Fact]
    public void AMacroStopReleasesTheMovementTheRouteWasHolding()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 88f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Single(automation.Intents);
        int releasedBefore = automation.ClearCount;

        controller.StopForMacroStop();

        Assert.True(automation.ClearCount > releasedBefore);
    }

    /// <summary>
    /// The reference's navigate rule releases the keys when the scheduler
    /// hands the pass to somebody else, and that is the only thing a lost
    /// turn does to it: the rule is not asked again until it could win.
    /// </summary>
    [Fact]
    public void ANavigateTierThatLosesTheTurnClearsTheMovementIntent()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var rule = new ControllerMacroRule(
            "NavigateRouteIdle",
            context => controller.Tick(context.ElapsedSeconds, context.CanAct),
            gate: () => true,
            onLostTurn: controller.StopForLostTurn);

        Assert.True(rule.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        rule.Running = true;
        Assert.NotEmpty(automation.Intents);

        rule.Running = false;
        Assert.Equal(1, automation.ClearCount);
    }

    /// <summary>
    /// A navigate rule inside a pre-chain can win the scheduler's scan and
    /// still never be run, because a fallback runs in its place. The mover it
    /// armed while being asked must stop all the same when another rule then
    /// takes the turn. Mutation: guard the switch-off on the rule having been
    /// run and the mover keeps steering for the attacker.
    /// </summary>
    [Fact]
    public void ANavigateRuleThatOnlyClaimedTheTurnStopsTheMoverWhenAnotherRuleWinsIt()
    {
        PluginNavigationPosition goal = Position(12d / 240d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, goal));
        var rule = new ControllerMacroRule(
            "NavigateRouteIdle",
            context =>
            {
                Assert.True(controller.ClaimFromRulePass(context.CanAct));
                return true;
            },
            onLostTurn: controller.StopForLostTurn);

        Assert.True(rule.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        controller.StepArmedMover(0.1d, navigationSlotsAreClear: true);
        int steered = automation.Intents.Count;
        Assert.True(steered > 0);

        rule.Running = false;
        controller.StepArmedMover(0.1d, navigationSlotsAreClear: true);
        controller.StepArmedMover(0.1d, navigationSlotsAreClear: true);

        Assert.Equal(steered, automation.Intents.Count);
        Assert.Equal(1, automation.ClearCount);
    }

    /// <summary>
    /// Skipping moves the cursor as arriving does: a circular route wraps, a
    /// once route that runs out is complete, and a follow route has nothing to
    /// skip. Mutation: skip without wrapping and the circular cursor runs off
    /// the end; skip a follow route and it reports a skip it cannot make.
    /// </summary>
    [Fact]
    public void SkippingWaypointsMovesTheCursorTheWayArrivingDoes()
    {
        PluginNavigationPosition far = Position(5d, 5d);
        RouteWaypoint[] points =
        [
            Waypoint(RouteWaypointType.Point, Position(0d, 0d)),
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)),
            Waypoint(RouteWaypointType.Point, Position(2d, 0d)),
        ];
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(far) };

        NavigationController circular = Controller(automation, RouteMode.Circular, points);
        Assert.Equal(2, circular.SkipWaypoints(2));
        Assert.Equal(2, circular.CurrentWaypointIndex);
        Assert.Equal(1, circular.SkipWaypoints(1));
        Assert.Equal(0, circular.CurrentWaypointIndex);

        NavigationController once = Controller(automation, RouteMode.Once, points);
        Assert.Equal(1, once.SkipWaypoints(1));
        Assert.Equal(1, once.CurrentWaypointIndex);
        Assert.Equal(2, once.SkipWaypoints(5));
        Assert.True(once.HasNothingLeftToWalk);
        Assert.Equal(0, once.SkipWaypoints(1));

        NavigationController follow = Controller(automation, RouteMode.Target, points);
        Assert.Equal(0, follow.SkipWaypoints(1));
    }

    /// <summary>
    /// Stepping back on a route walked forward moves the cursor one point
    /// down the list and stops at the first point; a circular route does not
    /// wrap round to its last point.
    /// Mutation: let the circular cursor wrap and the last step lands on the
    /// last point instead of staying on the first.
    /// </summary>
    [Theory]
    [InlineData("Circular")]
    [InlineData("Linear")]
    public void SteppingBackOnAForwardRouteGoesDownTheListAndStopsAtTheFirst(string modeName)
    {
        RouteMode mode = Enum.Parse<RouteMode>(modeName);
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Position(5d, 5d)) };
        NavigationController route = Controller(automation, mode, StepBackPoints());
        Assert.Equal(3, route.SkipWaypoints(3));
        Assert.Equal(3, route.CurrentWaypointIndex);

        Assert.Equal(1, route.StepBackWaypoints(1));
        Assert.Equal(2, route.CurrentWaypointIndex);
        Assert.Equal(2, route.StepBackWaypoints(9));
        Assert.Equal(0, route.CurrentWaypointIndex);
        Assert.Equal(0, route.StepBackWaypoints(1));
        Assert.Equal(0, route.CurrentWaypointIndex);
    }

    /// <summary>
    /// A linear route on its way back walks down the list, so the waypoint
    /// before the one it is heading for is the next one up: stepping back
    /// moves the cursor up the list, and stops at the last point.
    /// Mutation: step down the list whatever the direction and the cursor
    /// jumps ahead along the way back, to the first point.
    /// </summary>
    [Fact]
    public void SteppingBackOnALinearRouteOnItsWayBackGoesUpTheList()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Position(5d, 5d)) };
        NavigationController route = Controller(automation, RouteMode.Linear, StepBackPoints());
        // Out to the far end and two points back.
        route.SkipWaypoints(6);
        Assert.True(route.Reversing);
        Assert.Equal(1, route.CurrentWaypointIndex);

        Assert.Equal(1, route.StepBackWaypoints(1));
        Assert.Equal(2, route.CurrentWaypointIndex);
        Assert.Equal(1, route.StepBackWaypoints(9));
        Assert.Equal(3, route.CurrentWaypointIndex);
        Assert.Equal(0, route.StepBackWaypoints(1));
        Assert.Equal(3, route.CurrentWaypointIndex);
    }

    /// <summary>
    /// A reversed route is walked from the last point toward the first, so
    /// stepping back moves the cursor up the list, and stops at the last
    /// point rather than wrapping.
    /// Mutation: step down the list whatever the direction and the cursor
    /// moves on along the reversed walk instead of back.
    /// </summary>
    [Fact]
    public void SteppingBackOnAReversedRouteGoesUpTheList()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Position(5d, 5d)) };
        NavigationController route = Controller(automation, RouteMode.Circular, StepBackPoints());
        route.ToggleReverse();
        // Reversed from the first point: the last, then the one before it.
        route.SkipWaypoints(2);
        Assert.Equal(2, route.CurrentWaypointIndex);

        Assert.Equal(1, route.StepBackWaypoints(1));
        Assert.Equal(3, route.CurrentWaypointIndex);
        Assert.Equal(0, route.StepBackWaypoints(1));
        Assert.Equal(3, route.CurrentWaypointIndex);
    }

    private static RouteWaypoint[] StepBackPoints() =>
    [
        Waypoint(RouteWaypointType.Point, Position(0d, 0d)),
        Waypoint(RouteWaypointType.Point, Position(1d, 0d)),
        Waypoint(RouteWaypointType.Point, Position(2d, 0d)),
        Waypoint(RouteWaypointType.Point, Position(3d, 0d)),
    ];

    [Fact]
    public void CircularRouteWrapsAndOnceRouteStops()
    {
        RouteWaypoint first = Waypoint(RouteWaypointType.Point, Position(0d, 0d));
        RouteWaypoint second = Waypoint(RouteWaypointType.Point, Position(1d, 0d));
        var circularAutomation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(first.Position),
        };
        NavigationController circular = Controller(
            circularAutomation,
            RouteMode.Circular,
            first,
            second);

        Assert.True(circular.Tick(0.05d, canAct: true));
        Assert.Equal(1, circular.CurrentWaypointIndex);
        circularAutomation.NavigationSnapshot = Snapshot(second.Position);
        Assert.True(circular.Tick(0.05d, canAct: true));
        Assert.Equal(0, circular.CurrentWaypointIndex);

        var onceAutomation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(first.Position),
        };
        NavigationController once = Controller(
            onceAutomation,
            RouteMode.Once,
            first);

        Assert.True(once.Tick(0.05d, canAct: true));
        Assert.False(once.Tick(0.05d, canAct: true));
        Assert.Equal("Once route complete.", once.Status);
    }

    /// <summary>
    /// Running a once-through route to its end consumes it in memory only.
    /// The route itself is what the profile on disk is written from, so a run
    /// that took its points out of that list wrote an empty route over the
    /// author's file the next time anything saved the profile.
    ///
    /// Mutation: put the point removal back in the once branch of the
    /// waypoint advance and the point count drops to zero here.
    /// </summary>
    [Fact]
    public void AOnceRouteRunToItsEndStillHoldsEveryPointItStartedWith()
    {
        PluginNavigationPosition here = Position(0d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(here),
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Once,
        };
        for (int i = 0; i < 3; i++)
            settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, here));
        var controller = new NavigationController(new FakeHost(automation), settings);

        // A meta's "navigation route empty" is false while points remain.
        Assert.False(controller.HasNothingLeftToWalk);

        // Three arrivals walk the cursor off the end of the route.
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(1, controller.CurrentWaypointIndex);
        Assert.Equal(3, settings.Waypoints.Count);
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(2, controller.CurrentWaypointIndex);
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal("Once route complete.", controller.Status);

        // Done, idle, and the route is whole.
        Assert.False(controller.Tick(0.05d, canAct: true));
        Assert.Equal("Once route complete.", controller.Status);
        Assert.Equal(3, settings.Waypoints.Count);
        // Seen live: a meta waiting on "navigation route empty" never moved
        // on, because the finished route still held its points. Mutation:
        // the point count alone as the answer turns this red.
        Assert.True(controller.HasNothingLeftToWalk);
    }

    /// <summary>
    /// A finished once route does nothing until the route is loaded again.
    /// Starting the round is one of those moments, and it puts the cursor
    /// back on the first point.
    ///
    /// Mutation: drop the once-complete reset from the round anchor and the
    /// reloaded route stays finished.
    /// </summary>
    [Fact]
    public void AFinishedOnceRouteWalksAgainOnceTheRoundIsStartedAfresh()
    {
        PluginNavigationPosition here = Position(0d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(here),
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Once,
        };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, here));
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, here));
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.False(controller.Tick(0.05d, canAct: true));
        Assert.Equal("Once route complete.", controller.Status);

        controller.AnchorRoundToStart();

        Assert.Equal(0, controller.CurrentWaypointIndex);
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(1, controller.CurrentWaypointIndex);
    }

    [Fact]
    public void FollowReadsMovingTargetAndHoldsAtMinimumDistance()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.Objects[7u] = new PluginNavigationObject(
            7u,
            "Leader",
            Position(1d, 0d));
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Target,
            MinimumDistanceMeters = 2d,
            FollowTargetObjectId = 7u,
        };
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(Assert.Single(automation.Intents).Forward);

        automation.Objects[7u] = new PluginNavigationObject(
            7u,
            "Leader",
            Position(0.005d, 0d));
        Assert.False(controller.Tick(0.05d, canAct: true));
        Assert.Equal(1, automation.ClearCount);
        Assert.Contains("holding", controller.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FollowAroundCornersUsesOldestUnreachedBreadcrumb()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.Objects[7u] = new PluginNavigationObject(
            7u,
            "Leader",
            Position(0.1d, 0d));
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Target,
            MinimumDistanceMeters = 2d,
            FollowTargetObjectId = 7u,
            FollowAroundCorners = true,
        };
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.Objects[7u] = new PluginNavigationObject(
            7u,
            "Leader",
            Position(0.1d, 0.1d));
        automation.Intents.Clear();

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.True(intent.Forward);
        Assert.False(intent.TurnLeft);
        Assert.False(intent.TurnRight);
    }

    [Fact]
    public void CheckpointWaitsForServerAcceptedPosition()
    {
        PluginNavigationPosition point = Position(0d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(point) with
            {
                ConfirmedPosition = Position(0.1d, 0d),
                ConfirmedPositionRevision = 4UL,
            },
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Once,
            Waypoint(RouteWaypointType.Checkpoint, point));

        Assert.True(controller.Tick(1d, canAct: true));
        Assert.Contains("waiting for server", controller.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(controller.Tick(14d, canAct: true));
        Assert.True(Assert.Single(automation.Intents).Forward);

        automation.NavigationSnapshot = Snapshot(point) with
        {
            ConfirmedPosition = point,
            ConfirmedPositionRevision = 5UL,
        };
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.False(controller.Tick(0.05d, canAct: true));
    }

    [Fact]
    public void ClosedDoorPausesRouteAndUsesCanonicalItemAction()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.WorldObjects.Add(new PluginNavigationObject(
            55u,
            "Dungeon Door",
            Position(0.01d, 0d))
        {
            IsDoor = true,
            IsOpen = false,
            HasLockState = true,
        });
        var settings = new NavigationSettings
        {
            Enabled = true,
            OpenDoors = true,
            Mode = RouteMode.Circular,
        };
        settings.Waypoints.Add(Waypoint(
            RouteWaypointType.Point,
            Position(1d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);
        // The door takes its own turn, ahead of the route.
        var door = new OpenDoorRule(controller, () => true);

        Assert.True(door.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        Assert.Equal([55u], automation.UsedObjects);
        Assert.Empty(automation.Intents);

        automation.WorldObjects[0] = automation.WorldObjects[0] with
        {
            IsOpen = true,
        };
        Assert.False(door.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(Assert.Single(automation.Intents).Forward);
    }

    /// <summary>
    /// The navigation clock is wall time, and a turn is not a unit of it. On a
    /// frame where the door rule and the route rule are both consulted — the
    /// ordinary case — the clock must move by the frame, once.
    /// </summary>
    [Fact]
    public void TheNavigationClockMovesOnceAFrameHoweverManyTurnsAreTakenInIt()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
            // The typing branch is the one that measures against the clock.
            ChatInputActive = true,
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            OpenDoors = true,
            Mode = RouteMode.Circular,
        };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);

        void PassFrame()
        {
            controller.AdvanceClock(0.293d);
            Assert.False(controller.TickDoorRule(0.293d, canAct: true));
            Assert.True(controller.Tick(0.293d, canAct: true));
        }

        // The first frame faces and stamps the clock. Two more frames are
        // 0.586 s of real time after that stamp — inside the 0.7 s re-face
        // interval, but past it if each turn were allowed to move the clock
        // itself. The fourth frame is past it either way.
        PassFrame();
        Assert.Equal(90f, Assert.Single(automation.FacedHeadings));
        PassFrame();
        PassFrame();
        Assert.Single(automation.FacedHeadings);
        PassFrame();
        Assert.Equal(2, automation.FacedHeadings.Count);
    }

    /// <summary>
    /// Losing a pass to a rule ahead of the door rule declines the turn. It
    /// does not throw away the door already being worked on: an open sequence
    /// that restarted every time anything else took a turn could never finish.
    /// </summary>
    [Fact]
    public void TheDoorRuleKeepsItsDoorAcrossAPassItDoesNotWin()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.WorldObjects.Add(new PluginNavigationObject(
            55u,
            "Dungeon Door",
            Position(0.01d, 0d))
        {
            IsDoor = true,
            IsOpen = false,
            HasLockState = true,
        });
        var settings = new NavigationSettings
        {
            Enabled = true,
            OpenDoors = true,
            Mode = RouteMode.Circular,
        };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);

        // The door rule claims a pass and sends the first use.
        Assert.True(controller.TickDoorRule(0.05d, canAct: true));
        Assert.Equal([55u], automation.UsedObjects);

        // Something ahead of it takes the next pass.
        Assert.False(controller.TickDoorRule(0.05d, canAct: false));

        // The retry interval has not elapsed, so the pass it wins back must
        // still be working on the SAME door and must not re-send.
        Assert.True(controller.TickDoorRule(0.05d, canAct: true));
        Assert.Equal([55u], automation.UsedObjects);
    }

    /// <summary>
    /// The door is used once and then left alone for the retry interval. A
    /// door re-used on every pass is a use command every fraction of a second
    /// for as long as the door takes to swing.
    /// </summary>
    [Fact]
    public void TheDoorIsNotUsedAgainInsideItsRetryInterval()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.WorldObjects.Add(new PluginNavigationObject(
            55u,
            "Dungeon Door",
            Position(0.01d, 0d))
        {
            IsDoor = true,
            IsOpen = false,
            HasLockState = true,
        });
        var settings = new NavigationSettings
        {
            Enabled = true,
            OpenDoors = true,
            Mode = RouteMode.Circular,
        };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);

        for (int pass = 0; pass < 4; pass++)
            Assert.True(controller.TickDoorRule(0.293d, canAct: true));

        Assert.Equal([55u], automation.UsedObjects);

        // Past the interval, it tries again.
        Assert.True(controller.TickDoorRule(1.5d, canAct: true));
        Assert.Equal([55u, 55u], automation.UsedObjects);
    }

    /// <summary>
    /// A closed door is a wall, and everything downstream of it wants to be on
    /// the other side, so the door takes its turn before looting a corpse,
    /// approaching one, or attacking.
    /// </summary>
    [Fact]
    public void TheDoorRuleTakesItsTurnAheadOfLootingAndAttacking()
    {
        int door = SlotPosition(MacroRuleSlot.OpenDoor);
        Assert.InRange(door, 0, int.MaxValue);
        Assert.True(
            door < SlotPosition(MacroRuleSlot.LootCorpsePriority),
            "the door must be claimed before priority looting.");
        Assert.True(
            door < SlotPosition(MacroRuleSlot.NavigateCorpsePriority),
            "the door must be claimed before the priority corpse approach.");
        Assert.True(
            door < SlotPosition(MacroRuleSlot.Attack),
            "the door must be claimed before attacking.");

        static int SlotPosition(MacroRuleSlot slot)
        {
            for (int index = 0; index < MacroRuleTable.Entries.Count; index++)
            {
                if (MacroRuleTable.Entries[index].Slot == slot)
                    return index;
            }
            return -1;
        }
    }

    /// <summary>
    /// The door rule stands down while another rule holds a lock it waits on,
    /// and takes its own locks on the pass it claims. The named lock table is
    /// not built yet, so the pin drives the seam the table will be wired to.
    /// </summary>
    [Fact]
    public void TheDoorRuleStandsDownWhileALockIsHeld()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.WorldObjects.Add(new PluginNavigationObject(
            55u,
            "Dungeon Door",
            Position(0.01d, 0d))
        {
            IsDoor = true,
            IsOpen = false,
            HasLockState = true,
        });
        var settings = new NavigationSettings
        {
            Enabled = true,
            OpenDoors = true,
            Mode = RouteMode.Circular,
        };
        var controller = new NavigationController(new FakeHost(automation), settings);
        bool locked = true;
        int armed = 0;
        var rule = new OpenDoorRule(
            controller,
            () => true,
            isLocked: () => locked,
            arm: () => armed++);

        // Held: the rule declines and does not so much as look at the door.
        Assert.True(rule.IsLocked);
        Assert.False(rule.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        Assert.Equal(
            "another rule holds a lock this one waits on",
            rule.DeclineReason);
        Assert.Empty(automation.UsedObjects);
        Assert.Equal(0, armed);

        // Released: it claims the pass, uses the door, and takes its own locks.
        locked = false;
        Assert.False(rule.IsLocked);
        Assert.True(rule.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        Assert.Equal([55u], automation.UsedObjects);
        Assert.Equal(1, armed);
    }

    /// <summary>
    /// The door rule says why the DOOR declined. It used to fall through to
    /// the route's status line, which is a sentence about a waypoint and
    /// carries a live distance in it — so it never matched the previous one,
    /// the scheduler's duplicate-decline suppression could never fire, and a
    /// run with no door in it at all printed a decline on every walking pass.
    ///
    /// Mutation: return the route's status from <c>DeclineReason</c> again and
    /// the two reasons below become waypoint lines that differ every pass.
    /// </summary>
    [Fact]
    public void TheDoorRuleGivesItsOwnStableDeclineReason()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            OpenDoors = true,
            Mode = RouteMode.Circular,
        };
        // A route under way, so the route's status is a live waypoint line.
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);
        var rule = new OpenDoorRule(controller, () => true);

        Assert.False(rule.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        string first = Assert.IsType<string>(rule.DeclineReason);
        Assert.Equal("no door in reach", first);

        // The character has walked on; the reason must not have moved with it.
        automation.NavigationSnapshot = Snapshot(Position(0.05d, 0d, heading: 90f));
        Assert.False(rule.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        Assert.Equal(first, rule.DeclineReason);
        Assert.DoesNotContain("Waypoint", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pass the door wins says what the door is doing, and the route's
    /// own line is left describing the route.
    /// </summary>
    [Fact]
    public void TheDoorRuleReportsItsOwnWorkOnThePassItWins()
    {
        (NavigationController controller, _, _) = DoorFixture();
        var rule = new OpenDoorRule(controller, () => true);

        Assert.True(rule.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        Assert.Equal("opening door: Dungeon Door", rule.RunningDetail);
        Assert.DoesNotContain("door", controller.Status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every other refusal the door makes is its own sentence too, and none of
    /// them is the route's.
    /// </summary>
    [Fact]
    public void TheDoorRuleNamesTheDoorItIsWaitingOn()
    {
        (NavigationController controller, FakeAutomation automation, _) =
            DoorFixture(hasLockState: false);
        var rule = new OpenDoorRule(controller, () => true);

        Assert.False(rule.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        Assert.Equal("identifying door: Dungeon Door", rule.DeclineReason);
        Assert.NotEqual(controller.Status, rule.DeclineReason);
    }

    private static (NavigationController controller, FakeAutomation automation, ActionLockTable locks) DoorFixture(
        bool hasLockState = true)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.WorldObjects.Add(new PluginNavigationObject(
            55u,
            "Dungeon Door",
            Position(0.01d, 0d))
        {
            IsDoor = true,
            IsOpen = false,
            HasLockState = hasLockState,
        });
        var settings = new NavigationSettings
        {
            Enabled = true,
            OpenDoors = true,
            Mode = RouteMode.Circular,
        };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);
        var locks = new ActionLockTable();
        controller.BindActionLocks(locks);
        return (controller, automation, locks);
    }

    /// <summary>
    /// Opening a door is an item use, and a door takes a while to swing: the
    /// use holds the item slot for half a second and the navigation and door
    /// slots for five, so nothing walks into the doorway or uses another item
    /// while it moves. Mutation: drop the arms before the use and every slot
    /// reads free.
    /// </summary>
    [Fact]
    public void ADoorUseHoldsTheItemNavigationAndDoorSlots()
    {
        (NavigationController controller, FakeAutomation automation, ActionLockTable locks) = DoorFixture();

        Assert.True(controller.TickDoorRule(0.05d, canAct: true));
        Assert.Equal([55u], automation.UsedObjects);
        Assert.True(locks.IsLocked(ActionLockKind.ItemUse));
        Assert.True(locks.IsLocked(ActionLockKind.Navigation));
        Assert.True(locks.IsLocked(ActionLockKind.DoorOpening));

        locks.Advance(0.6d);
        Assert.False(locks.IsLocked(ActionLockKind.ItemUse));
        Assert.True(locks.IsLocked(ActionLockKind.Navigation));
        Assert.True(locks.IsLocked(ActionLockKind.DoorOpening));

        locks.Advance(4.5d);
        Assert.False(locks.IsLocked(ActionLockKind.Navigation));
        Assert.False(locks.IsLocked(ActionLockKind.DoorOpening));
    }

    /// <summary>
    /// A door whose lock state is not known yet is identified first, and while
    /// that answer is on its way the navigation slot is held for half a second
    /// and the pass is declined — the route stands still without the door
    /// owning the turn. Mutation: claim the pass instead of declining, or drop
    /// the arm, and this fails.
    /// </summary>
    [Fact]
    public void AnUnidentifiedDoorInReachHoldsNavigationAndDeclines()
    {
        (NavigationController controller, FakeAutomation automation, ActionLockTable locks) = DoorFixture(hasLockState: false);

        Assert.False(controller.TickDoorRule(0.05d, canAct: true));
        Assert.Empty(automation.UsedObjects);
        Assert.True(locks.IsLocked(ActionLockKind.Navigation));
        Assert.False(locks.IsLocked(ActionLockKind.ItemUse));
        Assert.False(locks.IsLocked(ActionLockKind.DoorOpening));
        locks.Advance(0.6d);
        Assert.False(locks.IsLocked(ActionLockKind.Navigation));
    }

    /// <summary>
    /// When the door the rule is working on reports itself open, the item and
    /// door slots go down at once; the navigation slot runs out on its own.
    /// Mutation: drop the release on open and both slots stay up for the
    /// rest of their windows.
    /// </summary>
    [Fact]
    public void AnOpenedDoorReleasesTheItemAndDoorSlotsEarly()
    {
        (NavigationController controller, FakeAutomation automation, ActionLockTable locks) = DoorFixture();
        Assert.True(controller.TickDoorRule(0.05d, canAct: true));

        automation.WorldObjects[0] = automation.WorldObjects[0] with { IsOpen = true };
        locks.Advance(0.1d);
        Assert.False(controller.TickDoorRule(0.05d, canAct: true));
        Assert.False(locks.IsLocked(ActionLockKind.ItemUse));
        Assert.False(locks.IsLocked(ActionLockKind.DoorOpening));
        Assert.True(locks.IsLocked(ActionLockKind.Navigation));
    }

    /// <summary>
    /// The scheduler marks every non-winner not running after it has asked
    /// every rule, so the pass after a door finishes is one where the route
    /// rule arms the mover and the door rule loses its turn in the same
    /// breath. The door's lost turn must leave that mover armed. Mutation:
    /// stop the navigation controller from the door rule's setter and the
    /// frame after produces no movement.
    /// </summary>
    [Fact]
    public void TheDoorRuleLosingItsTurnLeavesTheRouteMoverArmed()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            OpenDoors = true,
            Mode = RouteMode.Circular,
        };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(10d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);
        var door = new OpenDoorRule(controller, () => true) { Running = true };

        // Phase one of the pass: the route rule claims and arms the mover.
        Assert.True(controller.ClaimFromRulePass(canAct: true));
        Assert.Contains(automation.Intents, static intent => intent.Forward);
        // Phase two: the door rule, which did not win, is marked not running.
        door.Running = false;

        // The intent the route rule set is still standing, and the next frame
        // still belongs to an armed mover.
        Assert.Equal(0, automation.ClearCount);
        controller.StepArmedMover(0.05d, navigationSlotsAreClear: true);
        Assert.Equal(0, automation.ClearCount);
    }

    /// <summary>
    /// Stopping the macro puts the mover down as well as the movement: the
    /// mover keeps stepping on every host frame whether the macro runs or
    /// not, so a stop that only cleared the intent would leave a stopped
    /// macro steering. Mutation: drop the lost-turn layer from the macro
    /// stop and the frame after the stop steers again.
    /// </summary>
    [Fact]
    public void AMacroStopDisarmsTheMoverTheRouteHadArmed()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Circular,
        };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(10d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);
        Assert.True(controller.ClaimFromRulePass(canAct: true));
        Assert.Contains(automation.Intents, static intent => intent.Forward);

        controller.StopForMacroStop();
        Assert.Equal(1, automation.ClearCount);
        int intentsAfterStop = automation.Intents.Count;
        controller.StepArmedMover(0.05d, navigationSlotsAreClear: true);
        controller.StepArmedMover(0.05d, navigationSlotsAreClear: true);
        Assert.Equal(intentsAfterStop, automation.Intents.Count);
    }

    /// <summary>
    /// A start says which point it anchored the round to, on the rule-info
    /// channel, before the mover has taken a step. Mutation: name the index
    /// the round had instead of the one the anchor chose and this fails.
    /// </summary>
    [Fact]
    public void AStartSaysWhichPointItAnchoredTheRoundTo()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 15d, heading: 0f)),
        };
        var settings = new NavigationSettings { Enabled = true, Mode = RouteMode.Circular };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(0d, 0d)));
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(0d, 8d)));
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(0d, 16d)));
        var controller = new NavigationController(new FakeHost(automation), settings);
        var lines = new List<string>();
        controller.Log = (_, line) => lines.Add(line);

        controller.AnchorRoundToStart();

        string said = Assert.Single(lines);
        Assert.Contains("Waypoint 3/3", said, StringComparison.Ordinal);
    }

    [Fact]
    public void PauseAndChatActionsObserveOfficialInitialDelay()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
        };
        RouteWaypoint pause = Waypoint(RouteWaypointType.Pause, Position(0d, 0d));
        pause.DurationMilliseconds = 100;
        RouteWaypoint chat = Waypoint(RouteWaypointType.ChatCommand, Position(0d, 0d));
        chat.Text = "/say route";
        NavigationController controller = Controller(
            automation,
            RouteMode.Once,
            pause,
            chat);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(controller.Tick(0.05d, canAct: true));
        // The pause is done and the chat waypoint is the current one. A once
        // route keeps its whole list, so that is row 1 rather than row 0.
        Assert.Equal(1, controller.CurrentWaypointIndex);
        Assert.True(controller.Tick(0.19d, canAct: true));
        Assert.Empty(automation.SubmittedChat);
        Assert.True(controller.Tick(0.01d, canAct: true));
        Assert.Equal(["/say route"], automation.SubmittedChat);
        Assert.False(controller.Tick(0.01d, canAct: true));
    }

    [Fact]
    public void PortalWaypointWaitsForPortalExitRatherThanUseDispatch()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            ItemCompletion = new PluginItemUseCompletion(4, 10u, 0u, 0u),
        };
        RouteWaypoint use = Waypoint(RouteWaypointType.Portal, Position(0d, 0d));
        use.ObjectId = 77u;
        use.ObjectName = "Town Crier";
        NavigationController controller = Controller(automation, RouteMode.Once, use);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([77u], automation.UsedObjects);
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.ItemCompletion = new PluginItemUseCompletion(5, 77u, 0u, 0u);
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.NavigationSnapshot = automation.NavigationSnapshot with
        {
            IsPortalSpace = true,
        };
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.NavigationSnapshot = Snapshot(Position(0.1d, 0d));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.False(controller.Tick(0.05d, canAct: true));
    }

    // ── Arriving through a portal ────────────────────────────────────────

    /// <summary>
    /// A route that uses a portal, with the portal still standing 1 m east of
    /// the start and a point beyond the destination.
    /// </summary>
    private static (NavigationController Controller, FakeAutomation Automation)
        PortalRoute(RouteWaypointType kind)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
        };
        automation.Objects[77u] = new PluginNavigationObject(
            77u,
            "Portal",
            Position(1d / 240d, 0d));
        RouteWaypoint portal = Waypoint(kind, Position(0d, 0d));
        portal.ObjectId = 77u;
        portal.ObjectName = "Portal";
        portal.ReferencePosition = Position(1d / 240d, 0d);
        NavigationController controller = Controller(
            automation,
            RouteMode.Once,
            portal,
            Waypoint(RouteWaypointType.Point, Position(5d, 0d)));
        return (controller, automation);
    }

    private static void PassThroughPortalSpace(
        NavigationController controller,
        FakeAutomation automation,
        PluginNavigationPosition landing)
    {
        automation.NavigationSnapshot = automation.NavigationSnapshot with
        {
            IsPortalSpace = true,
        };
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.NavigationSnapshot = Snapshot(landing);
    }

    /// <summary>
    /// Coming out of portal space far from the portal finishes the portal
    /// waypoint on the first tick, even though the portal it used is still in
    /// the object table: the route moves on to the next point and never walks
    /// back towards where the portal stood.
    ///
    /// Mutation: put the portal-exit check back below the approach and the
    /// first tick after arrival steers at the old portal (forward held, the
    /// waypoint still current) until the object leaves the table.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APortalWaypointIsDoneOnTheFirstTickAfterArrival(bool byName)
    {
        (NavigationController controller, FakeAutomation automation) = PortalRoute(
            byName ? RouteWaypointType.PortalByName : RouteWaypointType.Portal);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([77u], automation.UsedObjects);

        PassThroughPortalSpace(controller, automation, Position(4d, 0d));
        automation.Intents.Clear();
        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(1, controller.CurrentWaypointIndex);
        Assert.DoesNotContain(automation.Intents, static intent => intent.Forward);
        Assert.Equal([77u], automation.UsedObjects);
    }

    /// <summary>
    /// A portal-by-name waypoint whose portal object has gone from the table by
    /// the time the character lands is done on arrival too, instead of
    /// searching for the object until the use times out.
    ///
    /// Mutation: put the portal-exit check back below the object search and
    /// the waypoint sits on "Finding Portal." for the rest of its thirty
    /// seconds.
    /// </summary>
    [Fact]
    public void APortalByNameWaypointIsDoneOnArrivalWhenItsPortalIsGone()
    {
        (NavigationController controller, FakeAutomation automation) =
            PortalRoute(RouteWaypointType.PortalByName);

        Assert.True(controller.Tick(0.05d, canAct: true));
        PassThroughPortalSpace(controller, automation, Position(4d, 0d));
        automation.Objects.Clear();

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(1, controller.CurrentWaypointIndex);
    }

    /// <summary>
    /// A portal-by-name exit within 15 m of where the use was sent is not an
    /// arrival: the waypoint stays current, says so, and uses the portal again
    /// once the character is back in reach of it.
    ///
    /// Mutation: put the portal-exit check back below the approach and the
    /// first tick after the near exit only walks back to the portal, so the
    /// second use waits a tick longer than it should.
    /// </summary>
    [Fact]
    public void APortalByNameExitNearItsOriginRetriesTheUse()
    {
        (NavigationController controller, FakeAutomation automation) =
            PortalRoute(RouteWaypointType.PortalByName);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([77u], automation.UsedObjects);

        // Out 10 m west of the start: 11 m from the portal, inside 15 m of
        // the origin.
        PassThroughPortalSpace(controller, automation, Position(-10d / 240d, 0d));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(0, controller.CurrentWaypointIndex);

        automation.NavigationSnapshot = Snapshot(Position(0d, 0d));
        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(0, controller.CurrentWaypointIndex);
        Assert.Equal([77u, 77u], automation.UsedObjects);
    }

    /// <summary>
    /// A recall is done on the first tick after the character comes out of
    /// portal space somewhere else. The position jump is the teleport itself,
    /// not the character failing to stand still.
    ///
    /// Mutation: put the portal-exit check back below the standing-still check
    /// and the first tick after arrival reads the jump as movement and waits.
    /// </summary>
    [Fact]
    public void ARecallWaypointIsDoneOnTheFirstTickAfterArrival()
    {
        (NavigationController controller, FakeAutomation automation,
            FakeMagic magic) = RecallReadyToCast();
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([1635u], magic.CastSpellIds);

        PassThroughPortalSpace(controller, automation, Position(4d, 0d));
        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(1, controller.CurrentWaypointIndex);
    }

    [Fact]
    public void UseNpcRepeatsUntilTheNpcRespondsInChat()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            FoundObject = new PluginNavigationObject(
                91u,
                "Town Crier",
                Position(0.01d, 0d)),
        };
        RouteWaypoint use = Waypoint(RouteWaypointType.UseNpc, Position(0d, 0d));
        use.ObjectName = "Town Crier";
        NavigationController controller = Controller(automation, RouteMode.Once, use);

        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.ChatMessages.Add(new PluginChatMessage(
            1UL,
            91u,
            3,
            "Town Crier",
            "Welcome.",
            string.Empty)
            { LogTextType = 3, DisplayText = "Town Crier tells you, \"Welcome.\"" });
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.False(controller.Tick(0.05d, canAct: true));
    }

    [Fact]
    public void NamedNpcWaypointReacquiresChangedObjectId()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            FoundObject = new PluginNavigationObject(
                91u,
                "Town Crier",
                Position(0.01d, 0d)),
        };
        RouteWaypoint use = Waypoint(RouteWaypointType.UseNpc, Position(0d, 0d));
        use.ObjectId = 77u;
        use.ObjectName = "Town Crier";
        NavigationController controller = Controller(automation, RouteMode.Once, use);

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(91u, use.ObjectId);
        Assert.Equal([91u], automation.UsedObjects);
        Assert.Equal("Town Crier", automation.FindName);
    }

    [Fact]
    public void JumpAlignsBeforeChargingAndWaitsForLanding()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 100;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Empty(automation.Intents);
        Assert.Equal(90f, Assert.Single(automation.FacedHeadings));

        automation.NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([0.1f], automation.Jumps);
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Single(automation.Jumps);

        automation.NavigationSnapshot = Snapshot(
            Position(0d, 0d, heading: 90f),
            airborne: true);
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f));
        Assert.True(controller.Tick(0.25d, canAct: true));
        Assert.False(controller.Tick(0.01d, canAct: true));
    }

    /// <summary>
    /// The reference raises its busy count for the whole of a route jump,
    /// from the turn to its heading until it has landed, and so holds its
    /// pass, meta included. The armed mover drives the jump on the frame
    /// meanwhile. Mutation: not reporting the jump leaves the pass running
    /// under it; reporting it after the landing holds the pass for good.
    /// </summary>
    [Fact]
    public void AJumpWaypointHoldsThePassFromItsTurnUntilItLands()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 100;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);
        Assert.False(controller.HoldsPass);

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        Assert.True(controller.HoldsPass);

        // Only frames from here: the pass is held.
        automation.NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f));
        controller.StepArmedMover(0.05d, navigationSlotsAreClear: true);
        controller.StepArmedMover(0.1d, navigationSlotsAreClear: true);
        Assert.Equal([0.1f], automation.Jumps);
        Assert.True(controller.HoldsPass);

        automation.NavigationSnapshot = Snapshot(
            Position(0d, 0d, heading: 90f),
            airborne: true);
        controller.StepArmedMover(0.05d, navigationSlotsAreClear: true);
        Assert.True(controller.HoldsPass);
        automation.NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f));
        controller.StepArmedMover(0.3d, navigationSlotsAreClear: true);

        Assert.False(controller.HoldsPass);
    }

    /// <summary>
    /// A route that has lost its turn holds nothing, even mid-jump.
    /// Mutation: dropping the armed check keeps holding the pass.
    /// </summary>
    [Fact]
    public void AJumpThatLostItsTurnHoldsNothing()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        controller.StepArmedMover(0.05d, navigationSlotsAreClear: false);

        Assert.False(controller.HoldsPass);
    }

    /// <summary>
    /// A route jump's shift flag holds the game's walk key: set, the jump
    /// leaves at a walk; clear, at the default run. Mutation: reading the
    /// flag as run inverts both.
    /// </summary>
    [Theory]
    [InlineData(true, PluginMovePace.Walk)]
    [InlineData(false, PluginMovePace.Run)]
    public void ARouteJumpsShiftFlagWalksAndItsAbsenceRuns(bool holdShift, PluginMovePace pace)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 300;
        jump.JumpHoldShift = holdShift;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(
            [(PluginMoveDirection.Forward, pace, 0f, PluginMoveUnit.MetersOrDegrees)],
            automation.Moves);
    }

    /// <summary>
    /// A route jump can go backwards. Mutation: leaving the backward case out
    /// of the intent makes the character charge a jump going nowhere, with
    /// every direction flag false.
    /// </summary>
    [Fact]
    public void JumpChargesBackwardWhenTheWaypointAsksForIt()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 100;
        jump.JumpDirection = RouteJumpDirection.Backward;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal([0.1f], automation.Jumps);
        Assert.Equal(
            [(PluginMoveDirection.Backward, PluginMovePace.Run, 0f, PluginMoveUnit.MetersOrDegrees)],
            automation.Moves);
    }

    /// <summary>
    /// A route jump leaves with exactly the power its charge time asks for,
    /// with its direction and pace held by the client through the charge,
    /// and lets go of that key once it has landed. Mutation: holding the
    /// jump key and letting go on a later tick leaves with whatever the
    /// frames added up to; leaving the key held walks on after the landing.
    /// </summary>
    [Fact]
    public void ARouteJumpLeavesWithExactlyThePowerItsChargeTimeAsks()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 450;
        jump.JumpHoldShift = true;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([0.45f], automation.Jumps);
        Assert.Equal(
            [(PluginMoveDirection.Forward, PluginMovePace.Walk, 0f, PluginMoveUnit.MetersOrDegrees)],
            automation.Moves);
        Assert.DoesNotContain(automation.Intents, static intent => intent.Jump);

        Assert.True(controller.Tick(0.45d, canAct: true));
        automation.NavigationSnapshot = Snapshot(
            Position(0d, 0d, heading: 90f),
            airborne: true);
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Empty(automation.StoppedMoves);
        automation.NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f));
        Assert.True(controller.Tick(0.25d, canAct: true));

        Assert.Equal([0.45f], automation.Jumps);
        Assert.Equal([PluginMoveChannel.Travel], automation.StoppedMoves);
    }

    /// <summary>
    /// A charge time past the two-second ceiling is the ceiling, and a
    /// full charge takes one second, so anything from a second up leaves
    /// with the full power and the charge is over after that second.
    /// Mutation: dropping the clamp asks the client for a power it refuses.
    /// </summary>
    [Fact]
    public void JumpChargeExecutionClampsAtAuthenticTwoThousandMillisecondCeiling()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 5000;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);

        Assert.True(controller.Tick(0.9d, canAct: true));
        Assert.Equal([1f], automation.Jumps);
        Assert.Equal("Charging jump: 2000ms.", controller.Status);

        Assert.True(controller.Tick(0.2d, canAct: true));
        Assert.Equal("Jump released.", controller.Status);
        Assert.Single(automation.Jumps);
    }

    /// <summary>
    /// A charge time of nothing is a tap: the least power the client takes,
    /// since it takes no jump of no power at all. Mutation: passing the zero
    /// on asks for a jump the client refuses.
    /// </summary>
    [Fact]
    public void AZeroChargeRouteJumpTapsWithTheLeastPowerTheClientTakes()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 0;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal([float.Epsilon], automation.Jumps);
    }

    [Fact]
    public void RouteProfilesRoundTripWaypointFieldsButLeaveSettingsOwnedFieldsAlone()
    {
        var storage = new MemoryStorage();
        var host = new FakeHost(new FakeAutomation(), storage);
        var source = new NavigationSettings
        {
            Mode = RouteMode.Linear,
        };
        source.Waypoints.Add(new RouteWaypoint
        {
            Type = RouteWaypointType.Jump,
            Position = new PluginNavigationPosition(0x7F7F0001u, 1.2d, -3.4d, 5.6d, 78f, true),
            ObjectId = 88u,
            ObjectName = "Portal",
            Text = "/say hello",
            DurationMilliseconds = 1234,
            Recall = RouteRecallKind.SecondaryPortalRecall,
            JumpHeadingDegrees = 271.5f,
            JumpHoldShift = true,
            JumpChargeMilliseconds = 875,
            JumpDirection = RouteJumpDirection.StrafeRight,
        });
        var first = new MossTankRouteProfileStore(host);
        Assert.True(first.BindCharacter("Test Character"));
        first.SaveCurrent(source);

        // Pre-seed values a Settings-profile load would already have set —
        // loading the route must leave every one of them untouched.
        var target = new NavigationSettings
        {
            Enabled = false,
            Priority = false,
            MinimumDistanceMeters = 9d,
            FollowAroundCorners = true,
            OpenDoors = false,
            DoorIdentifyRangeMeters = 11d,
            DoorOpenRangeMeters = 1d,
            DoorLockpickExcessThreshold = -3,
        };
        var second = new MossTankRouteProfileStore(host);
        Assert.True(second.BindCharacter("Test Character"));
        Assert.Equal(
            MossTankProfileLoad.Loaded,
            _ = second.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        Assert.Equal(RouteMode.Linear, target.Mode);
        RouteWaypoint waypoint = Assert.Single(target.Waypoints);
        Assert.Equal(RouteWaypointType.Jump, waypoint.Type);
        Assert.Equal(271.5f, waypoint.JumpHeadingDegrees);
        Assert.True(waypoint.JumpHoldShift);
        Assert.Equal(875, waypoint.JumpChargeMilliseconds);
        // The direction survives the save: it rides the charge field.
        Assert.Equal(RouteJumpDirection.StrafeRight, waypoint.JumpDirection);

        Assert.False(target.Enabled);
        Assert.False(target.Priority);
        Assert.Equal(9d, target.MinimumDistanceMeters);
        Assert.True(target.FollowAroundCorners);
        Assert.False(target.OpenDoors);
        Assert.Equal(11d, target.DoorIdentifyRangeMeters);
        Assert.Equal(1d, target.DoorOpenRangeMeters);
        Assert.Equal(-3, target.DoorLockpickExcessThreshold);
    }

    private static string LegacyRouteByCharacterKey(string characterName)
    {
        string identity = "char:" + characterName.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)));
        return $"profiles/route/{hash}.json";
    }

    [Fact]
    public void RouteStoreMigratesLegacyJsonProfileToAfAndDeletesTheJsonKey()
    {
        var storage = new MemoryStorage();
        string legacyKey = LegacyRouteByCharacterKey("Barris");
        storage.Text[legacyKey] = """
            {
              "Mode": 1,
              "Waypoints": [
                { "Type": 0, "EastWest": 5.0, "NorthSouth": 6.0 }
              ]
            }
            """;

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        Assert.Equal(
            MossTankProfileLoad.Loaded,
            _ = store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        Assert.False(storage.Text.ContainsKey(legacyKey));
        Assert.Equal(RouteMode.Linear, target.Mode);
        RouteWaypoint waypoint = Assert.Single(target.Waypoints);
        Assert.Equal(5.0d, waypoint.Position.EastWest, precision: 3);
        Assert.Equal(6.0d, waypoint.Position.NorthSouth, precision: 3);

        // Idempotent second run: nothing left to migrate.
        var reopened = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(reopened.BindCharacter("Barris"));
        var reloaded = new NavigationSettings();
        Assert.Equal(
            MossTankProfileLoad.Loaded,
            _ = reopened.LoadCurrent(reloaded, MetafSerializer.NoOpSpells.Instance));
        Assert.Single(reloaded.Waypoints);
    }

    [Fact]
    public void RouteStoreLeavesLegacyJsonUntouchedWhenAfCounterpartExists()
    {
        var storage = new MemoryStorage();
        string legacyKey = LegacyRouteByCharacterKey("Barris");
        storage.Text[legacyKey] = """{ "Mode": 1, "Waypoints": [] }""";
        string realKey = "mosstank/navs/" + VtankProfileDirectory.AutoCharacterFileName(
            "Barris", string.Empty, "af");
        var real = new NavigationSettings { Mode = RouteMode.Circular };
        real.Waypoints.Add(new RouteWaypoint
        {
            Type = RouteWaypointType.Point,
            Position = new PluginNavigationPosition(0x00010001u, 1d, 2d, 0d, 0f, true),
        });
        storage.Text[realKey] = MetafSerializer.SaveNav(real);

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        Assert.Equal(
            MossTankProfileLoad.Loaded,
            _ = store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        Assert.True(storage.Text.ContainsKey(legacyKey));
        Assert.Equal(RouteMode.Circular, target.Mode);
        Assert.Single(target.Waypoints);
    }

    [Theory]
    [InlineData(0, (int)RouteRecallKind.LifestoneRecall)]        // old Lifestone
    [InlineData(1, (int)RouteRecallKind.Marketplace)]              // old Marketplace
    [InlineData(2, (int)RouteRecallKind.PrimaryPortalRecall)]      // old PrimaryPortal
    [InlineData(3, (int)RouteRecallKind.SecondaryPortalRecall)]    // old SecondaryPortal
    public void LegacyJsonRouteMigratesOldRecallOrdinalToTheRightNewKind(
        int legacyOrdinal,
        int expectedKindOrdinal)
    {
        var expectedKind = (RouteRecallKind)expectedKindOrdinal;
        var storage = new MemoryStorage();
        string legacyKey = LegacyRouteByCharacterKey("Barris");
        storage.Text[legacyKey] = $$"""
            {
              "Mode": 1,
              "Waypoints": [
                { "Type": 2, "EastWest": 1.0, "NorthSouth": 2.0, "Recall": {{legacyOrdinal}} }
              ]
            }
            """;

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        Assert.Equal(
            MossTankProfileLoad.Loaded,
            _ = store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        RouteWaypoint waypoint = Assert.Single(target.Waypoints);
        Assert.Equal(RouteWaypointType.Recall, waypoint.Type);

        string fileName = "mosstank/navs/" + VtankProfileDirectory.AutoCharacterFileName(
            "Barris", string.Empty, "af");
        Assert.True(storage.Text.TryGetValue(fileName, out string? af));
        Assert.Contains($"{{{RouteWaypoint.RecallDisplayName(expectedKind)}}}", af);
    }

    private static string LegacyRouteNamedKey(string name)
    {
        string identity = "named:" + name.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)));
        return $"profiles/route/{hash}.json";
    }

    [Fact]
    public void RouteRosterSweepConvertsEveryNamedLegacyProfileOnce()
    {
        var storage = new MemoryStorage();
        storage.Text["profiles/route/index.json"] = """{ "Names": ["Farming", "Buffing"] }""";
        storage.Text[LegacyRouteNamedKey("Farming")] = """
            { "Mode": 1, "Waypoints": [ { "Type": 0, "EastWest": 1.0, "NorthSouth": 2.0 } ] }
            """;
        storage.Text[LegacyRouteNamedKey("Buffing")] = """
            { "Mode": 1, "Waypoints": [ { "Type": 0, "EastWest": 3.0, "NorthSouth": 4.0 } ] }
            """;

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        _ = store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance);

        Assert.False(storage.Text.ContainsKey(LegacyRouteNamedKey("Farming")));
        Assert.False(storage.Text.ContainsKey(LegacyRouteNamedKey("Buffing")));
        Assert.False(storage.Text.ContainsKey("profiles/route/index.json"));
        var farming = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(
            storage.Text["mosstank/navs/Farming.af"], farming, MetafSerializer.NoOpSpells.Instance, out _));
        Assert.Equal(1.0d, Assert.Single(farming.Waypoints).Position.EastWest, precision: 3);
        var buffing = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(
            storage.Text["mosstank/navs/Buffing.af"], buffing, MetafSerializer.NoOpSpells.Instance, out _));
        Assert.Equal(3.0d, Assert.Single(buffing.Waypoints).Position.EastWest, precision: 3);

        // Idempotent: a fresh store against the same storage sweeps nothing
        // more (there is no roster key left to read).
        var reopened = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(reopened.BindCharacter("Barris"));
        var reloadTarget = new NavigationSettings();
        _ = reopened.LoadCurrent(reloadTarget, MetafSerializer.NoOpSpells.Instance);
        Assert.True(storage.Text.ContainsKey("mosstank/navs/Farming.af"));
        Assert.True(storage.Text.ContainsKey("mosstank/navs/Buffing.af"));
    }


    [Fact]
    public void RouteStoreLeavesNonMarkedFlatAfFilesForTheMetaStore()
    {
        var storage = new MemoryStorage();
        storage.Text["SharedMeta.af"] = "1\r\n";

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        _ = store.LoadCurrent(new NavigationSettings(), MetafSerializer.NoOpSpells.Instance);

        Assert.True(storage.Text.ContainsKey("SharedMeta.af"));
        Assert.False(storage.Text.ContainsKey("mosstank/navs/SharedMeta.af"));
    }


    [Fact]
    public void ANavDroppedIntoTheNavsFolderLoadsAndSavesBackIntoItself()
    {
        var storage = new MemoryStorage();
        storage.Text["mosstank/navs/Dropped.nav"] = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "vtank", "nav", "nav_ab.nav"));
        var store = new MossTankRouteProfileStore(
            new FakeHost(new FakeAutomation(), storage));
        store.BindCharacter("Barris");

        Assert.Contains("Dropped", store.AvailableNames);
        Assert.True(store.Select("Dropped"));

        var route = new NavigationSettings();
        Assert.Equal(
            MossTankProfileLoad.Loaded,
            store.LoadCurrent(route, MetafSerializer.NoOpSpells.Instance));
        Assert.NotEmpty(route.Waypoints);

        string dropped = storage.Text["mosstank/navs/Dropped.nav"];
        Assert.True(store.SaveCurrent(route));

        // A route saves back to the file it came from, in that file's own
        // form, and one saved unchanged is the same bytes; nothing is
        // written beside it.
        Assert.Equal(dropped, storage.Text["mosstank/navs/Dropped.nav"]);
        Assert.False(storage.Text.ContainsKey("mosstank/navs/Dropped.af"));
    }

    [Fact]
    public void RouteStoreRefusesToLoadAMetaOnlyFileWithNoticeNamingMetasFolder()
    {
        string metaOnlyContent = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vtank", "af", "bella.af"));
        var storage = new MemoryStorage();
        storage.Text["mosstank/navs/Misplaced.af"] = metaOnlyContent;

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        Assert.True(store.Select("Misplaced"));

        var target = new NavigationSettings();
        MossTankProfileLoad loaded =
            _ = store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance);

        Assert.Equal(MossTankProfileLoad.Failed, loaded);
        Assert.NotNull(store.RecoveryNotice);
        Assert.Contains("mosstank/metas/", store.RecoveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowModeRouteRoundTripsTheFollowTargetThroughAf()
    {
        var storage = new MemoryStorage();
        var host = new FakeHost(new FakeAutomation(), storage);
        var source = new NavigationSettings
        {
            Mode = RouteMode.Target,
            FollowTargetObjectId = 99u,
            FollowTargetName = "Leader",
        };
        var first = new MossTankRouteProfileStore(host);
        Assert.True(first.BindCharacter("Test Character"));
        first.SaveCurrent(source);

        var target = new NavigationSettings();
        var second = new MossTankRouteProfileStore(host);
        Assert.True(second.BindCharacter("Test Character"));
        Assert.Equal(
            MossTankProfileLoad.Loaded,
            _ = second.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        Assert.Equal(RouteMode.Target, target.Mode);
        Assert.Equal(99u, target.FollowTargetObjectId);
        Assert.Equal("Leader", target.FollowTargetName);
    }


    [Fact]
    public void RouteRecallKindListsVTanksTwentySixRecallsInOrderPlusMarketplaceLast()
    {
        Assert.Equal(
            [
                "PrimaryPortalRecall", "SecondaryPortalRecall", "LifestoneRecall",
                "LifestoneSending", "PortalRecall", "RecallAphusLassel",
                "RecallTheSanctuary", "RecallToTheSingularityCaul", "GlendenWoodRecall",
                "AerlintheRecall", "MountLetheRecall", "UlgrimsRecall", "BurRecall",
                "ParadoxTouchedOlthoiInfestedAreaRecall", "CallOfTheMhoireForge",
                "ColosseumRecall", "FacilityHubRecall", "GearKnightInvasionAreaCampRecall",
                "LostCityOfNeftetRecall", "ReturnToTheKeep", "RynthidRecall",
                "ViridianRiseRecall", "ViridianRiseGreatTreeRecall",
                "CelestialHandStrongholdRecall", "RadiantBloodStrongholdRecall",
                "EldrytchWebStrongholdRecall", "Marketplace",
            ],
            Enum.GetNames<RouteRecallKind>());
    }

    /// <summary>
    /// The name<->id table both ways: every non-Marketplace kind's display
    /// name (RecallDisplayName) round-trips back to the SAME kind via its
    /// spell id (RecallSpellId is exposed nowhere to parse by name, so
    /// this proves the two lookups agree with each other rather than one
    /// silently drifting).
    /// </summary>
    [Theory]
    [InlineData((int)RouteRecallKind.PrimaryPortalRecall, "Primary Portal Recall", 48u)]
    [InlineData((int)RouteRecallKind.SecondaryPortalRecall, "Secondary Portal Recall", 2647u)]
    [InlineData((int)RouteRecallKind.LifestoneRecall, "Lifestone Recall", 1635u)]
    [InlineData((int)RouteRecallKind.LifestoneSending, "Lifestone Sending", 1636u)]
    [InlineData((int)RouteRecallKind.PortalRecall, "Portal Recall", 2645u)]
    [InlineData((int)RouteRecallKind.RecallAphusLassel, "Recall Aphus Lassel", 2931u)]
    [InlineData((int)RouteRecallKind.RecallTheSanctuary, "Recall the Sanctuary", 2023u)]
    [InlineData((int)RouteRecallKind.RecallToTheSingularityCaul, "Recall to the Singularity Caul", 2943u)]
    [InlineData((int)RouteRecallKind.GlendenWoodRecall, "Glenden Wood Recall", 3865u)]
    [InlineData((int)RouteRecallKind.AerlintheRecall, "Aerlinthe Recall", 2041u)]
    [InlineData((int)RouteRecallKind.MountLetheRecall, "Mount Lethe Recall", 2813u)]
    [InlineData((int)RouteRecallKind.UlgrimsRecall, "Ulgrim's Recall", 2941u)]
    [InlineData((int)RouteRecallKind.BurRecall, "Bur Recall", 4084u)]
    [InlineData((int)RouteRecallKind.ParadoxTouchedOlthoiInfestedAreaRecall,
        "Paradox-touched Olthoi Infested Area Recall", 4198u)]
    [InlineData((int)RouteRecallKind.CallOfTheMhoireForge, "Call of the Mhoire Forge", 4128u)]
    [InlineData((int)RouteRecallKind.ColosseumRecall, "Colosseum Recall", 4213u)]
    [InlineData((int)RouteRecallKind.FacilityHubRecall, "Facility Hub Recall", 5175u)]
    [InlineData((int)RouteRecallKind.GearKnightInvasionAreaCampRecall,
        "Gear Knight Invasion Area Camp Recall", 5330u)]
    [InlineData((int)RouteRecallKind.LostCityOfNeftetRecall, "Lost City of Neftet Recall", 5541u)]
    [InlineData((int)RouteRecallKind.ReturnToTheKeep, "Return to the Keep", 4214u)]
    [InlineData((int)RouteRecallKind.RynthidRecall, "Rynthid Recall", 6150u)]
    [InlineData((int)RouteRecallKind.ViridianRiseRecall, "Viridian Rise Recall", 6321u)]
    [InlineData((int)RouteRecallKind.ViridianRiseGreatTreeRecall, "Viridian Rise Great Tree Recall", 6322u)]
    [InlineData((int)RouteRecallKind.CelestialHandStrongholdRecall, "Celestial Hand Stronghold Recall", 6325u)]
    [InlineData((int)RouteRecallKind.RadiantBloodStrongholdRecall, "Radiant Blood Stronghold Recall", 6327u)]
    [InlineData((int)RouteRecallKind.EldrytchWebStrongholdRecall, "Eldrytch Web Stronghold Recall", 6326u)]
    [InlineData((int)RouteRecallKind.Marketplace, "Marketplace Recall", 0u)]
    public void RecallNameAndSpellIdTablesAgree(int kindOrdinal, string name, uint spellId)
    {
        var kind = (RouteRecallKind)kindOrdinal;
        Assert.Equal(name, RouteWaypoint.RecallDisplayName(kind));
        Assert.Equal(spellId, RouteWaypoint.SpellIdForRecall(kind));
    }

    [Fact]
    public void RecallWaypointWithNonZeroSpellIdCastsThatSpell()
    {
        var magic = new FakeMagic();
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            Magic = magic,
        };
        var waypoint = new RouteWaypoint
        {
            Type = RouteWaypointType.Recall,
            Recall = RouteRecallKind.AerlintheRecall,
            RecallSpellId = RouteWaypoint.SpellIdForRecall(RouteRecallKind.AerlintheRecall),
            RecallSpellName = RouteWaypoint.RecallDisplayName(RouteRecallKind.AerlintheRecall),
            Position = Position(0d, 0d),
        };
        NavigationController controller = Controller(automation, RouteMode.Once, waypoint);

        Assert.True(controller.Tick(0.1d, canAct: true));

        Assert.Contains(2041u, magic.CastSpellIds);
    }

    [Fact]
    public void RecallWaypointForMarketplaceSubmitsTheSlashCommandNotACast()
    {
        var magic = new FakeMagic();
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            Magic = magic,
        };
        var waypoint = new RouteWaypoint
        {
            Type = RouteWaypointType.Recall,
            Recall = RouteRecallKind.Marketplace,
            RecallSpellId = RouteWaypoint.SpellIdForRecall(RouteRecallKind.Marketplace),
            RecallSpellName = RouteWaypoint.RecallDisplayName(RouteRecallKind.Marketplace),
            Position = Position(0d, 0d),
        };
        NavigationController controller = Controller(automation, RouteMode.Once, waypoint);

        Assert.True(controller.Tick(0.1d, canAct: true));

        Assert.Empty(magic.CastSpellIds);
        Assert.Contains("/marketplace", automation.SubmittedChat);
    }

    [Fact]
    public void RecallWaypointWithUnresolvedSpellNameRefusesAndSkipsWithoutCasting()
    {
        var magic = new FakeMagic();
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            Magic = magic,
        };
        var waypoint = new RouteWaypoint
        {
            Type = RouteWaypointType.Recall,
            RecallSpellId = 0u,
            RecallSpellName = "NotARealSpell",
            Position = Position(0d, 0d),
        };
        NavigationController controller = Controller(automation, RouteMode.Once, waypoint);

        Assert.True(controller.Tick(0.1d, canAct: true));

        Assert.Empty(magic.CastSpellIds);
        Assert.Empty(automation.SubmittedChat);
        Assert.Contains("NotARealSpell", controller.Status);

        Assert.False(controller.Tick(0.1d, canAct: true));
        Assert.Equal("Once route complete.", controller.Status);
    }

    private sealed class FakeMagic : IMagicCommands
    {
        public List<uint> CastSpellIds { get; } = [];
        public List<uint> RequestedSpellIds { get; } = [];

        /// <summary>What the host answers a cast request with.</summary>
        public PluginCastRequestResult RequestAnswer { get; set; } =
            PluginCastRequestResult.Sent;

        /// <summary>Whether the host says a cast is in the air right now.</summary>
        public bool IsCasting { get; set; }

        /// <summary>The latched outcome of the last cast that finished.</summary>
        public PluginCastCompletion LastCompletion { get; set; }

        public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Refused;
        public bool Cast(uint spellId)
        {
            CastSpellIds.Add(spellId);
            return true;
        }

        public PluginCastRequestResult RequestCast(uint spellId)
        {
            RequestedSpellIds.Add(spellId);
            if (RequestAnswer != PluginCastRequestResult.Sent)
                return RequestAnswer;
            CastSpellIds.Add(spellId);
            return PluginCastRequestResult.Sent;
        }
    }

    // ── A recall casts from magic mode ───────────────────────────────────

    /// <summary>
    /// The recall pins share one setup: a lifestone-recall waypoint, a
    /// character standing still in peace mode, and the macro's own combat-mode
    /// gate bound to the controller.
    /// </summary>
    private static (NavigationController Controller, FakeAutomation Automation,
        FakeMagic Magic) RecallInPeace()
    {
        var magic = new FakeMagic();
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            CombatSnapshot = new PluginCombatSnapshot
            {
                Mode = PluginCombatMode.Peace,
                ServerMode = PluginCombatMode.Peace,
            },
            Magic = magic,
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Circular,
            MinimumDistanceMeters = 2d,
        };
        RouteWaypoint recall = Waypoint(RouteWaypointType.Recall, Position(0d, 0d));
        recall.Recall = RouteRecallKind.LifestoneRecall;
        recall.RecallSpellId = 1635u;
        recall.RecallSpellName = "Lifestone Recall";
        settings.Waypoints.Add(recall);
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var host = new FakeHost(automation);
        var controller = new NavigationController(host, settings);
        var combat = new CombatSettings();
        controller.BindCombatModeGate(
            new CombatModeGate(host, combat, new VitalSettings(), _ => { }),
            combat);
        return (controller, automation, magic);
    }

    /// <summary>
    /// The server drops a cast made outside magic mode, so a recall waypoint
    /// standing in peace asks the gate for magic first and sends nothing.
    ///
    /// Mutation: drop the IsReadyToCastRecall check from TickRecall and the
    /// spell goes out in peace mode, exactly the cast the server throws away.
    /// </summary>
    [Fact]
    public void ARecallInPeaceModeAsksForMagicModeAndCastsNothing()
    {
        (NavigationController controller, FakeAutomation automation,
            FakeMagic magic) = RecallInPeace();

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Contains("EnterMode:Magic", automation.ModeRequests);
        Assert.Empty(magic.RequestedSpellIds);
        Assert.Empty(magic.CastSpellIds);
    }

    /// <summary>
    /// Once the character is actually standing in magic mode the recall goes
    /// out.
    ///
    /// Mutation: have IsReadyToCastRecall answer a bare true and this turns
    /// red on the first tick (the recall goes out while the character is
    /// still in peace); answer a bare false and it turns red on the second
    /// (nothing ever goes out).
    /// </summary>
    [Fact]
    public void ARecallCastsOnceTheCharacterIsStandingInMagicMode()
    {
        (NavigationController controller, FakeAutomation automation,
            FakeMagic magic) = RecallInPeace();

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Empty(magic.CastSpellIds);

        automation.CombatSnapshot = new PluginCombatSnapshot
        {
            Mode = PluginCombatMode.Magic,
            ServerMode = PluginCombatMode.Magic,
        };

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal([1635u], magic.RequestedSpellIds);
        Assert.Equal([1635u], magic.CastSpellIds);
    }

    /// <summary>
    /// A refused cast says why in chat, once for the waypoint however many
    /// retries the timeout allows, instead of going quiet until the route
    /// moves on.
    ///
    /// Mutation: go back to Magic.Cast (which reports only "it did not go
    /// out") or drop the WarnOnce, and the chat log is empty here.
    /// </summary>
    [Fact]
    public void ARefusedRecallSaysWhyInChatOnceAcrossItsRetries()
    {
        var magic = new FakeMagic
        {
            RequestAnswer = PluginCastRequestResult.MissingComponents,
        };
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            Magic = magic,
        };
        RouteWaypoint recall = Waypoint(RouteWaypointType.Recall, Position(0d, 0d));
        recall.Recall = RouteRecallKind.LifestoneRecall;
        recall.RecallSpellId = 1635u;
        recall.RecallSpellName = "Lifestone Recall";
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            recall,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        // The first attempt and two retries, all refused.
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(controller.Tick(2.5d, canAct: true));
        Assert.True(controller.Tick(2.5d, canAct: true));

        Assert.Equal(3, magic.RequestedSpellIds.Count);
        Assert.Empty(magic.CastSpellIds);
        string posted = Assert.Single(automation.PostedSystemMessages);
        Assert.Equal(
            "[MossTank] Recall 'Lifestone Recall' refused: MissingComponents.",
            posted);
    }

    // ── A recall in flight is left alone ─────────────────────────────────

    /// <summary>
    /// A recall waypoint standing on a character who can cast: no combat-mode
    /// gate is bound, so the mode check passes and the spell goes out on the
    /// first tick.
    /// </summary>
    private static (NavigationController Controller, FakeAutomation Automation,
        FakeMagic Magic) RecallReadyToCast(
            PluginCastRequestResult answer = PluginCastRequestResult.Sent)
    {
        var magic = new FakeMagic { RequestAnswer = answer };
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            Magic = magic,
        };
        RouteWaypoint recall = Waypoint(RouteWaypointType.Recall, Position(0d, 0d));
        recall.Recall = RouteRecallKind.LifestoneRecall;
        recall.RecallSpellId = 1635u;
        recall.RecallSpellName = "Lifestone Recall";
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            recall,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        return (controller, automation, magic);
    }

    /// <summary>
    /// While the recall is still being cast the waypoint sends nothing more.
    /// The host turns away a cast asked for on top of a pending one, and that
    /// refusal used to reach the player as "Recall refused: Unavailable." on
    /// every recall that actually worked.
    ///
    /// Mutation: drop the IsCasting early return from TickRecall and the two
    /// long ticks below re-send the recall twice and post the refusal line.
    /// </summary>
    [Fact]
    public void ARecallStillInTheAirIsNotResentAndSaysNothing()
    {
        (NavigationController controller, FakeAutomation automation,
            FakeMagic magic) = RecallReadyToCast();

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([1635u], magic.RequestedSpellIds);

        // The cast is in the air, and the host would answer anything sent on
        // top of it with Unavailable.
        magic.IsCasting = true;
        magic.RequestAnswer = PluginCastRequestResult.Unavailable;

        Assert.True(controller.Tick(2.5d, canAct: true));
        Assert.True(controller.Tick(2.5d, canAct: true));

        Assert.Equal([1635u], magic.RequestedSpellIds);
        Assert.Empty(automation.PostedSystemMessages);
    }

    /// <summary>
    /// A cast that completed without taking the character into portal space
    /// is a fizzle or a server refusal, and the next attempt goes out at once
    /// rather than waiting out the two-second retry clock.
    ///
    /// Mutation: drop the LastCompletion revision check from TickRecall and
    /// the short tick after the completion sends nothing (the clock is only
    /// 0.05 s old, because the casting ticks kept winding it back).
    /// </summary>
    [Fact]
    public void ARecallThatCompletedWithoutPortalSpaceRecastsAtOnce()
    {
        (NavigationController controller, _, FakeMagic magic) =
            RecallReadyToCast();

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([1635u], magic.RequestedSpellIds);

        magic.IsCasting = true;
        Assert.True(controller.Tick(0.5d, canAct: true));
        Assert.Single(magic.RequestedSpellIds);

        // The cast ends with a server error and no portal space.
        magic.IsCasting = false;
        magic.LastCompletion = new PluginCastCompletion(1L, 1635u, 0u, 0x1Du);

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal([1635u, 1635u], magic.RequestedSpellIds);
    }

    /// <summary>
    /// Seen live: a recall that landed was cast twice. A cast that completed
    /// WITHOUT an error is a teleport on its way, and the teleport takes a
    /// moment to begin; the spell must not go out again while the route
    /// waits for portal space, however long the retry clock says.
    ///
    /// Mutation: dropping the landed guard from the send branch sends the
    /// spell a second time on the tick the retry clock runs out.
    /// </summary>
    [Fact]
    public void ARecallThatLandedIsNotCastAgainWhileTheTeleportStarts()
    {
        (NavigationController controller, _, FakeMagic magic) =
            RecallReadyToCast();

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([1635u], magic.RequestedSpellIds);

        magic.IsCasting = true;
        Assert.True(controller.Tick(0.5d, canAct: true));
        magic.IsCasting = false;
        // The cast ends cleanly; portal space has not come yet.
        magic.LastCompletion = new PluginCastCompletion(1L, 1635u, 0u, 0u);

        for (int tick = 0; tick < 100; tick++)
            Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Single(magic.RequestedSpellIds);
    }

    /// <summary>
    /// The quiet treatment is for Unavailable-while-casting alone: a real
    /// refusal still gets its one line.
    ///
    /// Mutation: widen the silent branch in SubmitRecall to every refusal, or
    /// drop its IsCasting condition, and no line is posted here.
    /// </summary>
    [Fact]
    public void AGenuineRecallRefusalOfMissingComponentsStillPostsItsOneLine()
    {
        (NavigationController controller, FakeAutomation automation,
            FakeMagic magic) = RecallReadyToCast(
                PluginCastRequestResult.MissingComponents);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(controller.Tick(2.5d, canAct: true));

        Assert.Empty(magic.CastSpellIds);
        Assert.Equal(
            "[MossTank] Recall 'Lifestone Recall' refused: MissingComponents.",
            Assert.Single(automation.PostedSystemMessages));
    }

    // ── The nav-minimum-distance idle-peace override ─────────────────────

    private static (NavigationController Controller, FakeAutomation Automation)
        ArrivalOverride(double minimumDistanceMeters, bool idlePeaceMode)
    {
        // 1.0 m ahead: inside the 1.5 m creep band, so the mover walks and asks
        // for magic mode on every tick it wants to walk.
        PluginNavigationPosition point = Position(0d, 1d / 240d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
            CombatSnapshot = new PluginCombatSnapshot
            {
                Mode = PluginCombatMode.Peace,
            },
            EquipmentItems =
            [
                new PluginEquipmentItem(
                    ObjectId: 800u,
                    Name: "Recovery Wand",
                    ItemType: 0x00008000u,
                    ValidLocations: 0x00100000u,
                    EquippedLocation: 0x00100000u,
                    ContainerObjectId: 0u,
                    WielderObjectId: 1u,
                    CombatUse: 0,
                    DamageType: 0,
                    WeaponSkill: 0,
                    Damage: 0,
                    DamageVariance: 0d),
            ],
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Circular,
            MinimumDistanceMeters = minimumDistanceMeters,
        };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, point));
        settings.Waypoints.Add(
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var combat = new CombatSettings { IdlePeaceMode = idlePeaceMode };
        var host = new FakeHost(automation);
        var controller = new NavigationController(host, settings);
        controller.BindCombatModeGate(
            new CombatModeGate(host, combat, new VitalSettings(), _ => { }),
            combat);
        return (controller, automation);
    }

    [Fact]
    public void LowWaypointDistanceInPeaceWarnsOnceAndForcesMagicMode()
    {
        (NavigationController controller, FakeAutomation automation) =
            ArrivalOverride(minimumDistanceMeters: 0.5d, idlePeaceMode: true);

        Assert.True(controller.Tick(1d, canAct: true));

        // Not advanced: the arrival was refused this tick.
        Assert.Equal(0, controller.CurrentWaypointIndex);
        Assert.Equal(
            NavigationController.LowWaypointDistanceWarning,
            Assert.Single(automation.PostedSystemMessages)
                .Replace("[MossTank] ", string.Empty, StringComparison.Ordinal));
        Assert.Equal(["EnterMode:Magic"], automation.ModeRequests);

        // The warning is posted once per approach, not once per pass.
        automation.ModeRequests.Clear();
        Assert.True(controller.Tick(1d, canAct: true));
        Assert.Single(automation.PostedSystemMessages);

        controller.ResetOncePerRunWarnings();
        Assert.True(controller.Tick(1d, canAct: true));
        Assert.Equal(2, automation.PostedSystemMessages.Count);
    }

    /// <summary>
    /// The SETTING gates only the warning. With Idle Peace off,
    /// the forced push into Magic mode still happens.
    /// </summary>
    [Fact]
    public void LowWaypointDistanceForcesMagicEvenWithIdlePeaceOff()
    {
        (NavigationController controller, FakeAutomation automation) =
            ArrivalOverride(minimumDistanceMeters: 0.5d, idlePeaceMode: false);

        Assert.True(controller.Tick(1d, canAct: true));

        Assert.Empty(automation.PostedSystemMessages);
        Assert.Equal(["EnterMode:Magic"], automation.ModeRequests);
        Assert.Equal(0, controller.CurrentWaypointIndex);
    }

    [Fact]
    public void OrdinaryWaypointDistanceArrivesWithoutTouchingCombatMode()
    {
        (NavigationController controller, FakeAutomation automation) =
            ArrivalOverride(minimumDistanceMeters: 2d, idlePeaceMode: true);

        Assert.True(controller.Tick(1d, canAct: true));

        Assert.Equal(1, controller.CurrentWaypointIndex);
        Assert.Empty(automation.PostedSystemMessages);
        Assert.Empty(automation.ModeRequests);
    }

    /// <summary>
    /// A jump is aimed near-exactly. Four degrees is fine for a walk and is a
    /// missed ledge for a jump, so the jump keeps aligning where the walk would
    /// already be charging.
    /// </summary>
    [Theory]
    [InlineData(3.9f, false)]
    [InlineData(0.05f, false)]
    [InlineData(0.005f, true)]
    public void JumpAlignsFarMoreTightlyThanTheWalk(float offset, bool charges)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f - offset)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 500;
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            jump);

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(
            charges,
            automation.Jumps.Count > 0);
        Assert.Equal(charges ? 0 : 1, automation.FacedHeadings.Count);
    }

    /// <summary>
    /// The jump waits two seconds between re-faces, not the walk's 0.7.
    /// </summary>
    [Fact]
    public void JumpDoesNotReissueFaceHeadingInsideTwoSeconds()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            jump);

        Assert.True(Frame(controller, 0.05d));
        Assert.Single(automation.FacedHeadings);

        // Past the walk's re-face interval and well short of the jump's.
        Assert.True(Frame(controller, 1.0d));
        Assert.Single(automation.FacedHeadings);

        Assert.True(Frame(controller, 1.2d));
        Assert.Equal(2, automation.FacedHeadings.Count);
    }

    /// <summary>
    /// Exactly the interval is not past it. The jump's re-face wants strictly
    /// more than two seconds, where the walk's wants at least seven tenths.
    /// </summary>
    [Fact]
    public void TheJumpReFaceWantsStrictlyMoreThanItsInterval()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            jump);

        // Both figures are exact in binary, so the second frame lands the
        // elapsed gap on the interval itself rather than a hair either side.
        Assert.True(Frame(controller, 0.25d));
        Assert.Single(automation.FacedHeadings);
        Assert.True(Frame(controller, 2d));
        Assert.Single(automation.FacedHeadings);
    }

    /// <summary>
    /// Only two lines complete a Use NPC waypoint, and each only with its own
    /// log-text type. Anything else is somebody else's conversation.
    /// </summary>
    /// <remarks>
    /// The cases are the shapes the plugin surface really delivers, pinned in
    /// <c>PluginChatLogTextTypeTests</c>: a tell arrives with its sender apart
    /// from a BARE message, a server line arrives whole with no sender. Rows
    /// three and four are the same two lines wearing each other's log-text
    /// type; rows five and six are an ordinary player tell and a chat-channel
    /// line that reads like the answer. Row seven is a player who shares the
    /// NPC's name: the host prints a player's name as a tell link, so the line
    /// does not open with the bare name, and only reading the host's wording
    /// (rather than rebuilding the line from its parts) keeps it out.
    /// </remarks>
    [Theory]
    // log-text type, sender object id, sender, text, completes
    [InlineData(3u, 500u, "Aun Tanua", "Greetings.", true)]
    [InlineData(0u, 0u, "", "Aun Tanua gives you a Token.", true)]
    [InlineData(0u, 500u, "Aun Tanua", "Greetings.", false)]
    [InlineData(3u, 0u, "", "Aun Tanua gives you a Token.", false)]
    [InlineData(3u, 700u, "Someone Else", "Greetings.", false)]
    [InlineData(8u, 0u, "", "Aun Tanua gives you a Token.", false)]
    [InlineData(3u, 0x5000_0003u, "Aun Tanua", "Greetings.", false)]
    public void UseNpcCompletesOnlyOnItsOwnChannels(
        uint logTextType,
        uint senderObjectId,
        string sender,
        string text,
        bool completes)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            FoundObject = new PluginNavigationObject(
                500u,
                "Aun Tanua",
                Position(0.001d, 0d)),
        };
        RouteWaypoint npc = Waypoint(RouteWaypointType.UseNpc, Position(0d, 0d));
        npc.ObjectName = "Aun Tanua";
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            npc,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.ChatMessages.Add(new PluginChatMessage(
            Sequence: 1UL,
            SenderObjectId: senderObjectId,
            // A tell's kind and its log-text type happen to share the number
            // three, which is exactly the confusion this pin exists to hold
            // apart: the kind here is the one the surface really reports for
            // that shape, and it is not what decides the answer.
            Kind: senderObjectId != 0u ? 3 : 4,
            Sender: sender,
            Text: text,
            ChannelName: string.Empty)
        {
            LogTextType = (int)logTextType,
            // The host words the whole line as the chat window prints it: a
            // tell under its sender, a player's name as a tell link, a server
            // line as it stands.
            DisplayText = senderObjectId == 0u
                ? text
                : senderObjectId >= 0x5000_0001u && senderObjectId <= 0x6FFF_FFFFu
                    ? $"<Tell:IIDString:{senderObjectId}:{sender}>{sender}<\\Tell> tells you, \"{text}\""
                    : $"{sender} tells you, \"{text}\"",
        });

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(completes ? 1 : 0, controller.CurrentWaypointIndex);
    }

    /// <summary>
    /// A vendor waypoint sends the use and then HOLDS: the host's use on
    /// something out of reach only starts a walk to it, so completing on the
    /// same tick moved the route on and cancelled that walk — the vendor
    /// never opened. The index moves only once the window is up.
    /// Mutation: completing on the tick the use goes out advances the index
    /// to 1 while the fake still reports no vendor window.
    /// </summary>
    [Fact]
    public void AVendorWaypointHoldsUntilTheWindowOpens()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
        };
        automation.Objects[404u] = new PluginNavigationObject(
            404u,
            "Shopkeeper",
            Position(0.001d, 0d));
        RouteWaypoint vendor = Waypoint(RouteWaypointType.OpenVendor, Position(0d, 0d));
        vendor.ObjectId = 404u;
        vendor.ObjectName = "Shopkeeper";
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            vendor,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        // The use went out, and the route stayed on the waypoint so the walk
        // the host started can finish.
        Assert.Equal([404u], automation.UsedObjects);
        Assert.Equal(0, controller.CurrentWaypointIndex);

        // Under two seconds: no second use.
        Assert.True(controller.Tick(1.0d, canAct: true));
        Assert.Equal([404u], automation.UsedObjects);
        Assert.Equal(0, controller.CurrentWaypointIndex);

        // Past two seconds with no window: the use is sent again.
        Assert.True(controller.Tick(1.5d, canAct: true));
        Assert.Equal([404u, 404u], automation.UsedObjects);
        Assert.Equal(0, controller.CurrentWaypointIndex);

        // The window opens: the waypoint is done.
        automation.ActiveVendorObjectId = 404u;
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(1, controller.CurrentWaypointIndex);
        Assert.Empty(automation.PostedSystemMessages);
    }

    /// <summary>
    /// Thirty seconds without a window is the ceiling: the route says so
    /// exactly once and carries on rather than parking an unattended bot on
    /// one waypoint forever.
    /// Mutation: dropping the timeout branch holds for ever and posts
    /// nothing; posting without WarnOnce posts on every later tick.
    /// </summary>
    [Fact]
    public void AVendorThatNeverOpensSaysSoOnceAndContinues()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
        };
        automation.Objects[404u] = new PluginNavigationObject(
            404u,
            "Shopkeeper",
            Position(0.001d, 0d));
        RouteWaypoint vendor = Waypoint(RouteWaypointType.OpenVendor, Position(0d, 0d));
        vendor.ObjectId = 404u;
        vendor.ObjectName = "Shopkeeper";
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            vendor,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        for (int tick = 0; tick < 6; tick++)
            Assert.True(controller.Tick(5d, canAct: true));

        Assert.Equal(1, controller.CurrentWaypointIndex);
        Assert.Single(automation.PostedSystemMessages);
        Assert.Equal(
            "[MossTank] Vendor 'Shopkeeper' did not open; continuing route.",
            automation.PostedSystemMessages[0]);
    }

    /// <summary>
    /// A window that is already open leaves this waypoint nothing to do, so
    /// it finishes at once and sends no use. The pause while the shop is
    /// worked is the vendor run's navigation lock, not the waypoint's.
    /// Mutation: holding here instead of completing leaves the index at 0.
    /// </summary>
    [Fact]
    public void AVendorWaypointCompletesWhenItsWindowIsAlreadyOpen()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            ActiveVendorObjectId = 404u,
        };
        automation.Objects[404u] = new PluginNavigationObject(
            404u,
            "Shopkeeper",
            Position(0.001d, 0d));
        RouteWaypoint vendor = Waypoint(RouteWaypointType.OpenVendor, Position(0d, 0d));
        vendor.ObjectId = 404u;
        vendor.ObjectName = "Shopkeeper";
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            vendor,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Empty(automation.UsedObjects);
        Assert.Equal(1, controller.CurrentWaypointIndex);
    }

    /// <summary>
    /// A vendor id that resolves to nothing holds too, and says so exactly
    /// once however many ticks it is asked.
    /// </summary>
    [Fact]
    public void AMissingVendorSaysSoOnceAndHolds()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
        };
        RouteWaypoint vendor = Waypoint(RouteWaypointType.OpenVendor, Position(0d, 0d));
        vendor.ObjectId = 404u;
        vendor.ObjectName = "Shopkeeper";
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            vendor,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        for (int tick = 0; tick < 5; tick++)
            Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Empty(automation.UsedObjects);
        Assert.Equal(0, controller.CurrentWaypointIndex);
        Assert.Single(automation.PostedSystemMessages);
        Assert.Contains("not found", automation.PostedSystemMessages[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A recall is cast from a standstill. While the character is still
    /// drifting the waypoint waits and casts nothing.
    /// </summary>
    [Fact]
    public void RecallWaitsUntilTheCharacterHasStopped()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
        };
        var magic = new FakeMagic();
        automation.Magic = magic;
        RouteWaypoint recall = Waypoint(RouteWaypointType.Recall, Position(0d, 0d));
        recall.RecallSpellId = 48u;
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            recall,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        // Standing still from the first tick, so it casts.
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([48u], magic.CastSpellIds);
        magic.CastSpellIds.Clear();

        // A retry is due, but the character has moved more than the tolerance
        // since the last tick, so the recall waits instead of casting.
        automation.NavigationSnapshot = Snapshot(Position(0d, 3d / 240d));
        Assert.True(controller.Tick(2.5d, canAct: true));
        Assert.Empty(magic.CastSpellIds);

        // Stopped again: the retry goes out.
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([48u], magic.CastSpellIds);
    }


    /// <summary>
    /// The stuck hand-off: a straight walk that holds the forward key for
    /// three seconds without covering ground lets go of the keys and asks
    /// the client to walk the leg, to the waypoint, within the arrival
    /// radius. Mutation: skip the mover's progress tracking in its steer and
    /// the request never comes.
    /// </summary>
    [Fact]
    public void AStraightWalkStuckForThreeSecondsHandsTheLegToTheClient()
    {
        PluginNavigationPosition goal = Position(0d, 12d / 240d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, goal));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        StepFrames(controller, 2.8d);
        Assert.Empty(automation.GoToRequests);
        Assert.Contains(automation.Intents, static intent => intent.Forward);

        StepFrames(controller, 0.4d);

        (PluginNavigationPosition where, float arrival) = Assert.Single(automation.GoToRequests);
        Assert.Equal(goal, where);
        Assert.Equal(2f, arrival);
        Assert.Contains("walked by the client", controller.Status, StringComparison.Ordinal);
        // The walk is the route's own, so the route does not hold it.
        Assert.True(controller.IsClientWalking);
        Assert.False(MossTankPanel.RouteHoldsClientWalks(routeEnabled: true, controller.IsClientWalking));
        int intentsAtHandOff = automation.Intents.Count;
        StepFrames(controller, 1d);
        Assert.Equal(intentsAtHandOff, automation.Intents.Count);
    }

    /// <summary>A character that covers ground is not stuck, however long the leg takes.</summary>
    [Fact]
    public void CoveringGroundKeepsTheStuckClockFromRunning()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, Position(0d, 200d / 240d)));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        for (int frame = 0; frame < 120; frame++)
        {
            PluginNavigationPosition here = automation.NavigationSnapshot.Position;
            automation.NavigationSnapshot = automation.NavigationSnapshot with
            {
                Position = here with { NorthSouth = here.NorthSouth + 0.05d / 240d },
            };
            controller.StepArmedMover(0.05d, navigationSlotsAreClear: true);
        }

        Assert.Empty(automation.GoToRequests);
        Assert.Empty(automation.PostedSystemMessages);
    }

    /// <summary>
    /// The clock belongs to the armed mover: losing the pass (a fight, a
    /// corpse) drops the keys and the clock, and the next turn starts it over.
    /// </summary>
    [Fact]
    public void LosingThePassResetsTheStuckClock()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, Position(0d, 12d / 240d)));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        StepFrames(controller, 2d);
        controller.StopForLostTurn();
        StepFrames(controller, 2d);
        Assert.True(controller.ClaimFromRulePass(canAct: true));
        StepFrames(controller, 2d);

        Assert.Empty(automation.GoToRequests);

        StepFrames(controller, 1.3d);

        Assert.Single(automation.GoToRequests);
    }

    /// <summary>The client arriving is the leg done: the route advances and the keys come back for the next one.</summary>
    [Fact]
    public void TheClientArrivingAdvancesTheRouteAndReturnsTheKeys()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, Position(0d, 12d / 240d)),
            Waypoint(RouteWaypointType.Point, Position(12d / 240d, 0d)));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        StepFrames(controller, 3.3d);
        Assert.Single(automation.GoToRequests);
        int intentsAtHandOff = automation.Intents.Count;

        automation.EndGoTo(PluginGoToState.Arrived, "arrived");
        StepFrames(controller, 0.2d);

        Assert.StartsWith("Waypoint 2/2", controller.Status, StringComparison.Ordinal);
        Assert.True(automation.Intents.Count > intentsAtHandOff);
        Assert.Single(automation.GoToRequests);
    }

    /// <summary>
    /// A client walk that ends without arriving hands the keys back to the
    /// straight walk, once per waypoint: a second stall there is said once
    /// in chat and walked through, not handed off again.
    /// </summary>
    [Theory]
    [InlineData(PluginGoToState.NoRoute)]
    [InlineData(PluginGoToState.Blocked)]
    [InlineData(PluginGoToState.Interrupted)]
    [InlineData(PluginGoToState.Lost)]
    public void AFailedClientWalkReturnsTheKeysAndTheWaypointGetsNoSecondHandOff(PluginGoToState ending)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, Position(0d, 12d / 240d)));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        StepFrames(controller, 3.3d);
        Assert.Single(automation.GoToRequests);
        int intentsAtHandOff = automation.Intents.Count;

        automation.EndGoTo(ending, "no way");
        StepFrames(controller, 0.2d);
        Assert.True(automation.Intents.Count > intentsAtHandOff);
        Assert.Contains(automation.Intents.Skip(intentsAtHandOff), static intent => intent.Forward);

        StepFrames(controller, 3.5d);
        Assert.Single(automation.GoToRequests);
        string said = Assert.Single(automation.PostedSystemMessages);
        Assert.Contains("not covered ground", said, StringComparison.Ordinal);

        StepFrames(controller, 3.5d);
        Assert.Single(automation.PostedSystemMessages);
    }

    /// <summary>Losing the pass while the client walks stops that walk with the rest of the movement.</summary>
    [Fact]
    public void LosingThePassStopsTheClientsWalk()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, Position(0d, 12d / 240d)));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        StepFrames(controller, 3.3d);
        Assert.Single(automation.GoToRequests);

        controller.StopForLostTurn();

        Assert.Equal(1, automation.StopGoToCalls);
        Assert.Equal(PluginGoToState.Stopped, automation.GoToReport.State);
    }

    /// <summary>A refused request (the player or another plugin has the character) is not a hand-off: the straight walk carries on.</summary>
    [Fact]
    public void ARefusedHandOffLeavesTheStraightWalkWalking()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
            GoToAnswer = PluginNavigationCommandStatus.Held,
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            Waypoint(RouteWaypointType.Point, Position(0d, 12d / 240d)));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        StepFrames(controller, 3.3d);
        Assert.Single(automation.GoToRequests);
        int intentsAfterRefusal = automation.Intents.Count;

        StepFrames(controller, 0.2d);

        Assert.True(automation.Intents.Count > intentsAfterRefusal);
        Assert.Equal(0, automation.StopGoToCalls);
    }

    /// <summary>Never means never: the straight walk keeps the keys and only says in chat that it stalled.</summary>
    [Fact]
    public void NeverKeepsTheKeysAndOnlySaysItStalled()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            ClientPathing.Never,
            Waypoint(RouteWaypointType.Point, Position(0d, 12d / 240d)));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        StepFrames(controller, 7d);

        Assert.Empty(automation.GoToRequests);
        Assert.Single(automation.PostedSystemMessages);
        Assert.Contains(automation.Intents.TakeLast(3), static intent => intent.Forward);
    }

    /// <summary>
    /// Always sends every leg to the client at once, and a leg the client
    /// cannot walk pauses the route where it stands, said once, until a reset.
    /// </summary>
    [Fact]
    public void AlwaysSendsEveryLegAndPausesOnALegTheClientCannotWalk()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            minimumDistanceMeters: 2d,
            ClientPathing.Always,
            Waypoint(RouteWaypointType.Point, Position(0d, 12d / 240d)));

        Assert.True(controller.ClaimFromRulePass(canAct: true));
        Assert.Single(automation.GoToRequests);
        Assert.Empty(automation.Intents);

        automation.EndGoTo(PluginGoToState.NoRoute, "no route");
        StepFrames(controller, 0.2d);
        Assert.False(controller.ClaimFromRulePass(canAct: true));
        Assert.Contains("paused", controller.Status, StringComparison.Ordinal);
        string said = Assert.Single(automation.PostedSystemMessages);
        Assert.Contains("paused", said, StringComparison.Ordinal);
        Assert.False(controller.ClaimFromRulePass(canAct: true));
        Assert.Single(automation.GoToRequests);
        Assert.Empty(automation.Intents);

        controller.Reset();
        Assert.True(controller.ClaimFromRulePass(canAct: true));
        Assert.Equal(2, automation.GoToRequests.Count);
    }

    /// <summary>
    /// The far stop range is the outer bound on what counts as a goal at all,
    /// not a second arrival radius: a waypoint further away than it is not
    /// walked to, so the route rule declines and nothing is asked of the
    /// character. Inside it the same waypoint is walked to normally.
    ///
    /// Mutation: drop the far bound from the leg test, and the waypoint twenty
    /// metres out is walked to as well.
    /// </summary>
    [Theory]
    [InlineData(8d, true)]
    [InlineData(20d, false)]
    public void TheFarStopRangeBoundsWhichWaypointIsAGoalAtAll(
        double waypointMetres,
        bool walks)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Circular,
            MinimumDistanceMeters = 2d,
            MaximumDistanceMeters = 10d,
        };
        settings.Waypoints.Add(Waypoint(
            RouteWaypointType.Point,
            Position(waypointMetres / 240d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.Equal(walks, controller.Tick(0.05d, canAct: true));
        Assert.Equal(walks, automation.Intents.Count != 0);
    }

    /// <summary>
    /// The same bound holds a follow target: further away than the far stop
    /// range the follow is not a goal, and the character is not sent after it.
    ///
    /// Mutation: take the far bound out of the follow test and the character
    /// chases a target the profile put out of bounds.
    /// </summary>
    [Theory]
    [InlineData(8d, true)]
    [InlineData(20d, false)]
    public void TheFarStopRangeAlsoBoundsAFollowTarget(
        double targetMetres,
        bool follows)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.Objects[42u] = new PluginNavigationObject(
            42u,
            "Partner",
            Position(targetMetres / 240d, 0d));
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Target,
            MinimumDistanceMeters = 2d,
            MaximumDistanceMeters = 10d,
            FollowTargetObjectId = 42u,
        };
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.Equal(follows, controller.Tick(0.05d, canAct: true));
        Assert.Equal(follows, automation.Intents.Count != 0);
    }

    /// <summary>
    /// The portal-use distance decides between walking at a portal and pulling
    /// it: outside the profile's distance the waypoint steers and no use goes
    /// out, inside it the use is dispatched and the character stands still.
    ///
    /// Mutation: ignore the setting and the portal is used from wherever the
    /// character happens to be standing.
    /// </summary>
    [Theory]
    [InlineData(10d, false)]
    [InlineData(2d, true)]
    public void ThePortalUseDistanceDecidesBetweenWalkingAndPulling(
        double portalMetres,
        bool uses)
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.Objects[77u] = new PluginNavigationObject(
            77u,
            "Portal",
            Position(portalMetres / 240d, 0d));
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Once,
            PortalUseDistanceMeters = 4d,
        };
        RouteWaypoint portal = Waypoint(RouteWaypointType.Portal, Position(0d, 0d));
        portal.ObjectId = 77u;
        portal.ObjectName = "Portal";
        settings.Waypoints.Add(portal);
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(uses, automation.UsedObjects.Count != 0);
        Assert.Equal(!uses, automation.Intents.Count != 0);
    }

    /// <summary>
    /// The portal-use distance is a profile number, so a profile that widens it
    /// pulls the same portal from further out.
    ///
    /// Mutation: replace the setting with a constant and the wide profile walks
    /// instead of pulling.
    /// </summary>
    [Fact]
    public void AWiderPortalUseDistancePullsTheSamePortalFromFurtherOut()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.Objects[78u] = new PluginNavigationObject(
            78u,
            "Portal",
            Position(10d / 240d, 0d));
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Once,
            PortalUseDistanceMeters = 15d,
        };
        RouteWaypoint portal = Waypoint(RouteWaypointType.Portal, Position(0d, 0d));
        portal.ObjectId = 78u;
        portal.ObjectName = "Portal";
        settings.Waypoints.Add(portal);
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([78u], automation.UsedObjects);
        Assert.Empty(automation.Intents);
    }

    /// <summary>
    /// A route read before the character's spell tables were available carries
    /// the recall's name and no id; the waypoint resolves it against the recall
    /// table when it runs and casts. Mutation: dropping the run-time resolve in
    /// TickRecall turns this red — nothing is cast and the point is skipped.
    /// </summary>
    [Fact]
    public void RecallWaypointResolvesItsNameWhenItRunsAndCastsTheRecall()
    {
        var magic = new FakeMagic();
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            Magic = magic,
        };
        RouteWaypoint waypoint = Waypoint(RouteWaypointType.Recall, Position(0d, 0d));
        waypoint.RecallSpellName = "Lifestone Recall";
        NavigationController controller = Controller(automation, RouteMode.Once, waypoint);

        Assert.True(controller.Tick(0.1d, canAct: true));

        Assert.Contains(1635u, magic.CastSpellIds);
        Assert.Equal(RouteRecallKind.LifestoneRecall, waypoint.Recall);
    }

    /// <summary>
    /// A name no table knows is announced in chat, once, rather than only in a
    /// status line on a panel the player may not have open. Mutation: dropping
    /// the WarnOnce leaves the chat log empty.
    /// </summary>
    [Fact]
    public void UnknownRecallNameIsAnnouncedInChatOnce()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
        };
        RouteWaypoint waypoint = Waypoint(RouteWaypointType.Recall, Position(0d, 0d));
        waypoint.RecallSpellName = "Not A Recall At All";
        NavigationController controller = Controller(automation, RouteMode.Circular, waypoint);

        Assert.True(controller.Tick(0.1d, canAct: true));
        Assert.True(controller.Tick(0.1d, canAct: true));

        string posted = Assert.Single(automation.PostedSystemMessages);
        Assert.Equal(
            "[MossTank] Recall spell 'Not A Recall At All' not found; skipping waypoint.",
            posted);
    }

    /// <summary>Steps the armed mover through a stretch of host frames, each long enough to be a steering frame.</summary>
    private static void StepFrames(NavigationController controller, double seconds)
    {
        for (double elapsed = 0d; elapsed < seconds; elapsed += 0.05d)
            controller.StepArmedMover(0.05d, navigationSlotsAreClear: true);
    }

    private static NavigationController Controller(
        FakeAutomation automation,
        RouteMode mode,
        double minimumDistanceMeters,
        ClientPathing clientPathing,
        params RouteWaypoint[] waypoints)
    {
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = mode,
            MinimumDistanceMeters = minimumDistanceMeters,
            ClientPathing = clientPathing,
        };
        settings.Waypoints.AddRange(waypoints);
        return new NavigationController(new FakeHost(automation), settings);
    }
    private static NavigationController Controller(
        FakeAutomation automation,
        RouteMode mode,
        params RouteWaypoint[] waypoints) =>
        Controller(automation, mode, 2d, waypoints);

    private static NavigationController Controller(
        FakeAutomation automation,
        RouteMode mode,
        double minimumDistanceMeters,
        params RouteWaypoint[] waypoints)
    {
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = mode,
            MinimumDistanceMeters = minimumDistanceMeters,
        };
        settings.Waypoints.AddRange(waypoints);
        return new NavigationController(new FakeHost(automation), settings);
    }

    /// <summary>
    /// One host frame: the clock moves, then the controller takes its turn.
    /// That is the production order — the panel steps the mover, which owns
    /// the clock, and then runs the scheduler pass.
    /// </summary>
    private static bool Frame(NavigationController controller, double seconds)
    {
        controller.AdvanceClock(seconds);
        return controller.Tick(seconds, canAct: true);
    }

    private static RouteWaypoint Waypoint(
        RouteWaypointType type,
        PluginNavigationPosition position) => new()
    {
        Type = type,
        Position = position,
    };

    private static PluginNavigationSnapshot Snapshot(
        PluginNavigationPosition position,
        bool airborne = false) => new(
        IsAvailable: true,
        IsPortalSpace: false,
        LocalObjectId: 1u,
        position,
        IsMoving: false,
        IsAirborne: airborne);

    private static PluginNavigationPosition Position(
        double eastWest,
        double northSouth,
        float heading = 0f) => new(
        0x7F7F0001u,
        eastWest,
        northSouth,
        0d,
        heading,
        IsOutdoor: true);

    private sealed class FakeHost(
        FakeAutomation automation,
        IPluginStorage? storage = null) : IPluginHost
    {
        public bool HasUi => false;
        public FakeLogger Logger { get; } = new();
        public IPluginLogger Log => Logger;
        public IGameState State { get; } = new FakeState();
        public IEvents Events { get; } = new FakeEvents();
        public ISelectionService Selection { get; } = new FakeSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IPluginStorage Storage { get; } = storage ?? NoOpPluginStorage.Instance;
        public IAutomationSurface Automation { get; } = automation;
        public IPluginStorage VtankProfiles { get; } = storage ?? NoOpPluginStorage.Instance;
    }

    private sealed class FakeAutomation
        : IAutomationSurface, INavigationAutomation, IPluginChat, IItemAutomation,
          ICombatAutomation, IEquipmentAutomation
    {
        public bool IsAvailable => true;
        public ICombatAutomation Combat => this;
        public IEquipmentAutomation Equipment => this;
        public PluginCombatSnapshot CombatSnapshot { get; set; }
        PluginCombatSnapshot ICombatAutomation.Snapshot => CombatSnapshot;
        public List<string> ModeRequests { get; } = [];
        public List<string> PostedSystemMessages { get; } = [];
        public bool ChatInputActive { get; set; }
        bool IPluginChat.IsInputActive => ChatInputActive;
        public List<PluginEquipmentItem> EquipmentItems { get; set; } = [];
        bool IEquipmentAutomation.IsAvailable => EquipmentItems.Count > 0;
        bool IEquipmentAutomation.IsBusy => false;
        IReadOnlyList<PluginEquipmentItem> IEquipmentAutomation.CaptureOwnedEquipment() =>
            EquipmentItems;
        IReadOnlyList<PluginEquipmentPlacement>
            IEquipmentAutomation.CaptureWorldPlacementsInOrder() =>
            EquipmentItems.Select(static item => new PluginEquipmentPlacement(
                item.ObjectId, item.EquippedLocation)).ToArray();
        PluginEquipmentCommandResult IEquipmentAutomation.Equip(
            uint objectId,
            uint requestedLocation)
        {
            ModeRequests.Add($"Equip:{objectId}");
            return new(PluginEquipmentCommandStatus.Started);
        }

        IReadOnlyList<PluginCombatTarget> ICombatAutomation.CaptureHostileTargets(
            float maximumDistance) => [];
        PluginCombatCommandResult ICombatAutomation.EnterDefaultMode() =>
            new(PluginCombatCommandStatus.Unavailable);
        PluginCombatCommandResult ICombatAutomation.EnterMode(PluginCombatMode mode)
        {
            ModeRequests.Add($"EnterMode:{mode}");
            return new(PluginCombatCommandStatus.Started);
        }

        PluginCombatCommandResult ICombatAutomation.BeginPhysicalAttack(
            uint targetObjectId,
            PluginAttackHeight height,
            float power) => new(PluginCombatCommandStatus.Unavailable);
        PluginCombatCommandResult ICombatAutomation.ReleasePhysicalAttack() =>
            new(PluginCombatCommandStatus.Unavailable);
        PluginCombatCommandResult ICombatAutomation.AbortPhysicalAttack() =>
            new(PluginCombatCommandStatus.Unavailable);
        public ICharacterInfo Character => NoOpAutomationSurface.Instance;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic { get; set; } = NoOpAutomationSurface.Instance;
        public IPluginChat Chat => this;
        public IItemAutomation Items => this;
        public INavigationAutomation Navigation => this;
        public PluginNavigationSnapshot NavigationSnapshot { get; set; }
        PluginNavigationSnapshot INavigationAutomation.Snapshot => NavigationSnapshot;
        public Dictionary<uint, PluginNavigationObject> Objects { get; } = [];
        public List<PluginNavigationObject> WorldObjects { get; } = [];
        public List<PluginMovementIntent> Intents { get; } = [];
        public List<string> SubmittedChat { get; } = [];
        public List<PluginChatMessage> ChatMessages { get; } = [];
        public List<uint> UsedObjects { get; } = [];
        public int ClearCount { get; private set; }
        public PluginItemUseCompletion ItemCompletion { get; set; }
        public PluginItemUseCompletion LastCompletion => ItemCompletion;
        public uint ActiveVendorObjectId { get; set; }
        public PluginNavigationObject? FoundObject { get; set; }
        public string? FindName { get; private set; }

        public bool TryGetObject(uint objectId, out PluginNavigationObject value) =>
            Objects.TryGetValue(objectId, out value);

        public bool TryFindObject(
            string name,
            in PluginNavigationPosition near,
            double maximumDistanceMeters,
            out PluginNavigationObject value)
        {
            FindName = name;
            value = FoundObject ?? default;
            return FoundObject.HasValue;
        }

        public IReadOnlyList<PluginNavigationObject> CaptureObjects() =>
            WorldObjects;

        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent)
        {
            Intents.Add(intent);
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus ClearMovementIntent()
        {
            ClearCount++;
            return PluginNavigationCommandStatus.Accepted;
        }

        public List<(PluginNavigationPosition Position, float ArrivalMeters)> GoToRequests { get; } = [];
        public PluginNavigationCommandStatus GoToAnswer { get; set; } = PluginNavigationCommandStatus.Accepted;
        public PluginGoToReport GoToReport { get; set; }
        public int StopGoToCalls { get; private set; }

        public PluginNavigationCommandStatus GoTo(PluginNavigationPosition position, float arrivalMeters)
        {
            GoToRequests.Add((position, arrivalMeters));
            if (GoToAnswer != PluginNavigationCommandStatus.Accepted)
                return GoToAnswer;
            GoToReport = new PluginGoToReport(
                GoToReport.Sequence + 1,
                PluginGoToState.Walking,
                0u,
                0f,
                0,
                "walking");
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus StopGoTo()
        {
            StopGoToCalls++;
            GoToReport = GoToReport with { State = PluginGoToState.Stopped, Reason = "stopped" };
            return PluginNavigationCommandStatus.Accepted;
        }

        public void EndGoTo(PluginGoToState state, string reason = "") =>
            GoToReport = GoToReport with { State = state, Reason = reason };

        public List<float> FacedHeadings { get; } = [];

        public PluginNavigationCommandStatus FaceHeading(float headingDegrees)
        {
            FacedHeadings.Add(headingDegrees);
            return PluginNavigationCommandStatus.Accepted;
        }

        /// <summary>
        /// The moves the client was asked to carry out, the jumps it was
        /// asked for by power, and the channels it was asked to stop (null
        /// for every channel at once), each in order.
        /// </summary>
        public List<(PluginMoveDirection Direction, PluginMovePace Pace, float Amount, PluginMoveUnit Unit)> Moves { get; } = [];
        public List<float> Jumps { get; } = [];
        public List<PluginMoveChannel?> StoppedMoves { get; } = [];

        public PluginNavigationCommandStatus Move(
            PluginMoveDirection direction,
            PluginMovePace pace,
            float amount,
            PluginMoveUnit unit = PluginMoveUnit.MetersOrDegrees)
        {
            Moves.Add((direction, pace, amount, unit));
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus StopMoving()
        {
            StoppedMoves.Add(null);
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus StopMoving(PluginMoveChannel channel)
        {
            StoppedMoves.Add(channel);
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus Jump(float power)
        {
            Jumps.Add(power);
            return PluginNavigationCommandStatus.Accepted;
        }

        public bool Submit(string text)
        {
            SubmittedChat.Add(text);
            return true;
        }

        public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
            ChatMessages.Where(message => message.Sequence > afterSequence).ToArray();

        public void PostSystemMessage(string text) =>
            PostedSystemMessages.Add(text);

        public PluginItemCommandResult Use(uint objectId)
        {
            UsedObjects.Add(objectId);
            return new PluginItemCommandResult(PluginItemCommandStatus.Started);
        }
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
        public Dictionary<string, string> Text => _text;
        public bool IsAvailable => true;
        public string? ReadText(string key) =>
            _text.TryGetValue(key, out string? value) ? value : null;
        public IReadOnlyList<string> List(string prefix) => _text.Keys
            .Where(key => prefix.Length == 0
                || key.StartsWith(prefix + "/", StringComparison.Ordinal))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        public void WriteText(string key, string content) => _text[key] = content;
        public bool Delete(string key) => _text.Remove(key);
    }

    private sealed class FakeLogger : IPluginLogger
    {
        public List<string> Warnings { get; } = [];
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class FakeEvents : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }

        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeSelection : ISelectionService
    {
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }

        public bool Select(uint objectId) => false;
        public bool Clear() => false;
    }
}
