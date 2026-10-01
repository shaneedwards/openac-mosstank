using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class CombatControllerTests
{
    /// <summary>
    /// A monster with no health left is a corpse, and the host refuses a
    /// swing at it. The macro used to keep it and ask again every pass, so a
    /// character standing in a crowd swung at the one thing that could not be
    /// hit while the rest of the crowd hit back. Mutation: take the health
    /// gate out of the candidate walk and the next swing goes back to the
    /// dead monster.
    /// </summary>
    [Fact]
    public void ADeadMonsterIsDroppedAndTheNextOneIsAttacked()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "First", distance: 1.5f, angle: 0),
                Target(20, "Second", distance: 2f, angle: 5),
                Target(30, "Third", distance: 3f, angle: 10),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 8f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25d);
        Assert.Equal(10u, surface.LastBeginTarget);

        // The swing lands and the monster dies: the capture still carries it,
        // with no health left, and the host refuses a swing at it.
        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "First", distance: 1.5f, angle: 0, health: 0f),
            Target(20, "Second", distance: 2f, angle: 5),
            Target(30, "Third", distance: 3f, angle: 10),
        ];
        surface.UnattackableTargets.Add(10u);
        surface.BeginTargets.Clear();
        // Past the shot floor the swing at the first monster put up: the
        // swing at the next one waits that floor out like any other.
        controller.OnTick(1.0d);
        controller.OnTick(0.25d);

        Assert.Equal(20u, surface.LastBeginTarget);
        Assert.DoesNotContain(10u, surface.BeginTargets);
    }

    /// <summary>
    /// The host answers a swing it cannot arm with the reason. Whatever the
    /// reason, the monster is not one this pass can act on, so the pass lets
    /// it go and chooses again instead of holding the character in front of
    /// it. Nothing went out, so nothing is charged against the monster: an
    /// attempt counts against a request that reached the server. Mutation:
    /// leave the refusal claiming the pass and the second monster is never
    /// swung at.
    /// </summary>
    [Fact]
    public void ARefusedSwingLetsTheTargetGoAndThePassChoosesAgain()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "First", distance: 1.5f, angle: 0),
                Target(20, "Second", distance: 2f, angle: 5),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.UnattackableTargets.Add(10u);
        var settings = new CombatSettings
        {
            MaximumRange = 8f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        var lines = new List<string>();
        controller.Log = (_, text) => lines.Add(text);

        controller.Toggle();
        controller.OnTick(0.25d);

        Assert.Equal(20u, surface.LastBeginTarget);
        Assert.DoesNotContain(
            lines,
            static line => line.Contains("Blacklist", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a pass learns about ONE monster is about that monster only, and it
    /// dies with the pass. The refused monster is out of the running until the
    /// next pass rebuilds the picture; every other monster in range is
    /// untouched, and the refused one is asked about again next pass.
    /// Mutation: keep the pass-invalid set across ticks and the later swing
    /// never returns to the first monster.
    /// </summary>
    [Fact]
    public void OnlyTheRefusedMonsterLeavesTheRunningAndOnlyForThatPass()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "First", distance: 1.5f, angle: 0),
                Target(20, "Second", distance: 2f, angle: 5),
                Target(30, "Third", distance: 3f, angle: 10),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.UnattackableTargets.Add(10u);
        var settings = new CombatSettings
        {
            MaximumRange = 8f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25d);
        Assert.Equal(20u, surface.LastBeginTarget);

        // The refusal was the monster being out of play for a moment, not a
        // verdict on it. With the two it was passed over for gone, the next
        // pass asks about it again - the pass it failed took nothing with it
        // and left nothing behind.
        surface.UnattackableTargets.Clear();
        surface.CombatSnapshot = Physical();
        surface.Targets = [Target(10, "First", distance: 1.5f, angle: 0)];
        surface.BeginTargets.Clear();
        controller.OnTick(1.0d);

        Assert.Contains(10u, surface.BeginTargets);
    }

    /// <summary>
    /// The character can be set to keep swinging on its own, and it keeps
    /// swinging at whatever it was last pointed at - which, after a kill, is
    /// a corpse. The pass is waiting on no swing of its own, so it ends that
    /// repeat instead of standing behind it. Mutation: let the repeat fall
    /// through to the wait and the pass claims the tick with nothing asked
    /// for and nothing cancelled.
    /// </summary>
    [Fact]
    public void ARepeatAtSomethingElseIsEndedRatherThanWaitedOn()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with
            {
                SelectedObjectId = 99u,
                RepeatAttackInProgress = true,
            },
            Targets = [Target(10, "Drudge", distance: 2f, angle: 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 8f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25d);

        Assert.Equal(1, surface.AbortCount);
        Assert.Empty(surface.BeginTargets);
    }

    /// <summary>
    /// With nothing else in range the attack has no target, so it yields the
    /// pass and whatever sits below it -- looting, above all -- gets to run.
    /// Mutation: hold the refused monster as the target and the rule claims
    /// the pass for ever.
    /// </summary>
    [Fact]
    public void ARefusedLoneMonsterLeavesTheAttackWithNothingToClaim()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Only", distance: 1.5f, angle: 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.UnattackableTargets.Add(10u);
        var settings = new CombatSettings
        {
            MaximumRange = 8f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25d);

        Assert.False(controller.HasTarget);
    }

    /// <summary>
    /// The winner acts before the losers are torn down here, which the
    /// reference does the other way round, so a teardown that reaches
    /// outside its own rule can undo the work the winner just did. The
    /// attack's teardown used to stop the walk to a monster -- a rule
    /// twenty positions below it, and usually the very rule the attack just
    /// lost the pass to, so the walk was cancelled on the pass it was
    /// armed. Each teardown releases only what its own rule holds now.
    /// Mutation: put <c>StopApproachMovement</c> back into the paused
    /// branch of <c>SetPaused</c> and the intent is cleared.
    /// </summary>
    [Fact]
    public void TheAttacksTeardownLeavesTheMonsterWalkAlone()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 12, angle: 0)],
            NavigationSnapshot = NavigationAt(heading: 0f),
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(0x7F7F0001, 0d, 0.1d, 0d, 0f, true));
        var settings = new CombatSettings
        {
            MaximumRange = 5f,
            ApproachDistance = 20f,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.05d);
        // The approach rule wins the pass and arms the walk.
        Assert.True(controller.TickMonsterApproach(canAct: true));
        Assert.Single(surface.MovementIntents);
        int clearedBefore = surface.ClearMovementCount;

        // The attack, which lost that same pass, is torn down afterwards.
        controller.SetPaused(true);

        Assert.Equal(clearedBefore, surface.ClearMovementCount);
    }

    /// <summary>
    /// Losing the turn is not a reason for anything. The attack reports why it
    /// declined, and the reason is whatever its own pass produced — "waiting
    /// for a target", "charging", "waiting for combat mode". The teardown that
    /// runs afterwards, once another rule has won, must leave that sentence
    /// alone: a teardown that writes its own reason over it renames every
    /// decline after the fact, and the log then says the attack was standing
    /// down for a buff when it was doing nothing of the kind.
    /// Mutation: write a status in the paused branch of <c>SetPaused</c> and
    /// this fails — the true reason is overwritten before it is ever read.
    /// </summary>
    [Fact]
    public void LosingTheTurnLeavesTheAttacksOwnReasonStanding()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings { ScanIntervalSeconds = 0.05d };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25d);
        string reason = controller.Status;
        Assert.Equal("Waiting for a target", reason);

        controller.SetPaused(true);

        Assert.Equal(reason, controller.Status);
    }

    /// <summary>
    /// The reference writes Running=false to every loser on every pass, and
    /// a navigate rule told that releases the keys it was holding. The
    /// monster-approach rule sits twenty positions below the attack, so it
    /// loses the pass often, and without the teardown the walk it started
    /// carries on under whichever rule won -- two movement owners steering
    /// at once, against the route. Mutation: drop
    /// <c>StopMonsterApproachForLostTurn</c> from the NavigateMonster row's
    /// <c>onLostTurn</c> and the intent is never cleared.
    /// </summary>
    [Fact]
    public void LosingTheTurnStopsTheMonsterApproachWalk()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 12, angle: 0)],
            NavigationSnapshot = NavigationAt(heading: 0f),
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(0x7F7F0001, 0d, 0.1d, 0d, 0f, true));
        var settings = new CombatSettings
        {
            MaximumRange = 5f,
            ApproachDistance = 20f,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.05d);
        Assert.True(controller.TickMonsterApproach(canAct: true));
        Assert.Single(surface.MovementIntents);
        int clearedBefore = surface.ClearMovementCount;

        controller.StopMonsterApproachForLostTurn();

        Assert.Equal(clearedBefore + 1, surface.ClearMovementCount);
    }

    /// <summary>
    /// Mutation: put the approach back inside the attack (build the attack's
    /// candidates out to the approach range and walk from there) and the
    /// second half fails — the attack would claim the pass with the monster
    /// still twelve metres away, so nothing below it would ever run.
    /// </summary>
    [Fact]
    public void WalkingToAMonsterIsItsOwnJobBelowTheAttack()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 12, angle: 0)],
            NavigationSnapshot = NavigationAt(heading: 0f),
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(0x7F7F0001, 0d, 0.1d, 0d, 0f, true));
        var settings = new CombatSettings
        {
            MaximumRange = 5f,
            ApproachDistance = 20f,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.05d);

        // The attack has nothing to do: the monster is out of weapon range, so
        // it is not one of its candidates at all.
        Assert.False(controller.HasTarget);
        Assert.Empty(surface.MovementIntents);
        Assert.Equal(0, surface.BeginCount);

        Assert.True(controller.TickMonsterApproach(canAct: true));
        PluginMovementIntent intent = Assert.Single(surface.MovementIntents);
        Assert.True(intent.Forward);
        Assert.Contains("Approaching", controller.Status, StringComparison.Ordinal);

        surface.Targets = [Target(10, "Drudge", distance: 4, angle: 0)];
        controller.OnTick(0.05d);

        Assert.Equal(10u, surface.LastBeginTarget);
        // Nothing left to walk to.
        Assert.False(controller.TickMonsterApproach(canAct: true));
    }

    /// <summary>
    /// Mutation: drop the approach rule's own candidate pick and reuse the
    /// attack's target and this fails — the attack has no target at all here.
    /// </summary>
    [Fact]
    public void TheApproachPicksItsOwnTargetAtTheApproachRange()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "Drudge", distance: 12, angle: 0),
                Target(20, "Olthoi Soldier", distance: 18, angle: 30),
            ],
            NavigationSnapshot = NavigationAt(heading: 0f),
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.NavigationObjects[20u] = new PluginNavigationObject(
            20u,
            "Olthoi Soldier",
            new PluginNavigationPosition(0x7F7F0001, 0.1d, 0d, 0d, 0f, true));
        var settings = new CombatSettings
        {
            MaximumRange = 5f,
            ApproachDistance = 20f,
            ScanIntervalSeconds = 0.05d,
            SelectionMethod = TargetSelectionMethod.Range,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.05d);

        Assert.True(controller.TickMonsterApproach(canAct: true));
        Assert.Contains(
            "Olthoi Soldier",
            controller.Status,
            StringComparison.Ordinal);
    }

    private static (FakeAutomation Surface, CombatController Controller)
        ApproachRig(float selfHeading, double targetEastWest, double targetNorthSouth)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 12, angle: 0)],
            NavigationSnapshot = NavigationAt(selfHeading),
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(
                0x7F7F0001,
                targetEastWest,
                targetNorthSouth,
                0d,
                0f,
                true));
        var settings = new CombatSettings
        {
            MaximumRange = 5f,
            ApproachDistance = 20f,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        return (surface, controller);
    }

    /// <summary>
    /// The walk steers through the same close-in mover as the route: outside
    /// the alignment band it HOLDS a turn key and keeps running, so the
    /// character curves onto the bearing at speed. It used to stop dead for
    /// any error over four degrees and re-face on a throttle, which is the
    /// mover's typing branch used as the normal one, and that turned every
    /// approach into a stop-turn-stop shuffle.
    ///
    /// Mutation: steer with the stop-and-face branch again and the held turn
    /// key disappears along with the forward intent.
    /// </summary>
    [Fact]
    public void ApproachHoldsATurnAndKeepsRunningInsideTheFarRelaxation()
    {
        // Target ~40 degrees east of north, twelve metres off.
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest: 0.0839d, targetNorthSouth: 0.1d);

        controller.TickMonsterApproach(canAct: true);

        PluginMovementIntent intent = Assert.Single(surface.MovementIntents);
        Assert.True(intent.Forward);
        Assert.True(intent.Run);
        Assert.True(intent.TurnLeft ^ intent.TurnRight);
        Assert.Empty(surface.FacedHeadings);
        Assert.Equal(0, surface.ClearMovementCount);
    }

    /// <summary>
    /// Past the far relaxation the mover turns in place instead: the turn key
    /// stays held, the forward key does not. Forty-five degrees is the line
    /// while the goal is further than three metres.
    /// </summary>
    [Theory]
    // ~44 degrees east of north: inside the relaxation, so it runs.
    [InlineData(0.0966d, true)]
    // ~46 degrees east of north: past it, so it turns on the spot.
    [InlineData(0.1036d, false)]
    public void TheFarRelaxationIsFortyFiveDegrees(
        double targetEastWest,
        bool expectForward)
    {
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest, targetNorthSouth: 0.1d);

        controller.TickMonsterApproach(canAct: true);

        PluginMovementIntent intent = Assert.Single(surface.MovementIntents);
        Assert.True(intent.TurnRight);
        Assert.Equal(expectForward, intent.Forward);
        // The run key stays held even while turning in place: that is what
        // keeps the turn at its fast rate. Only the creep band lets it go.
        Assert.True(intent.Run);
        Assert.Empty(surface.FacedHeadings);
    }

    /// <summary>
    /// The absolute re-face belongs to the branch where a held key would be
    /// typed into the chat entry, and to nothing else.
    /// </summary>
    [Fact]
    public void ApproachOnlyFacesTheTargetWhileSomebodyIsTyping()
    {
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest: 0.0839d, targetNorthSouth: 0.1d);
        surface.ChatInputActive = true;

        controller.TickMonsterApproach(canAct: true);

        Assert.Empty(surface.MovementIntents);
        float faced = Assert.Single(surface.FacedHeadings);
        Assert.InRange(faced, 39f, 41f);
    }

    [Fact]
    public void ApproachRunsForwardInsideTheFourDegreeBandWithoutFacingAgain()
    {
        // Target ~1.7 degrees east of north.
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest: 0.003d, targetNorthSouth: 0.1d);

        controller.TickMonsterApproach(canAct: true);

        PluginMovementIntent intent = Assert.Single(surface.MovementIntents);
        Assert.True(intent.Forward);
        Assert.True(intent.Run);
        Assert.False(intent.TurnLeft);
        Assert.False(intent.TurnRight);
        Assert.Empty(surface.FacedHeadings);
        Assert.Contains("Approaching", controller.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// The walk is a navigation goal, so it steers on the mover's own
    /// interval on the host's frame — not once per rule pass, which is six
    /// times coarser and only on the passes this row wins, twenty rows below
    /// the attack.
    ///
    /// Mutation: step the mover from the rule pass again and the half-interval
    /// frame below steers too.
    /// </summary>
    [Fact]
    public void TheArmedWalkStepsOnTheMoverInterval()
    {
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest: 0.003d, targetNorthSouth: 0.1d);

        Assert.True(controller.ClaimMonsterApproachFromRulePass(canAct: true));
        Assert.Single(surface.MovementIntents);

        controller.StepArmedApproachMover(NavigationMover.MoverIntervalSeconds / 2d);
        Assert.Single(surface.MovementIntents);

        controller.StepArmedApproachMover(NavigationMover.MoverIntervalSeconds);
        Assert.Equal(2, surface.MovementIntents.Count);
    }

    /// <summary>
    /// A disarmed mover steers at nothing: the walk only runs while the rule
    /// that armed it is winning passes.
    /// </summary>
    [Fact]
    public void ADisarmedWalkDoesNotSteer()
    {
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest: 0.003d, targetNorthSouth: 0.1d);

        controller.StepArmedApproachMover(1d);

        Assert.Empty(surface.MovementIntents);
    }

    /// <summary>
    /// Which monster is worth walking to is re-asked on every mover frame,
    /// not once per rule pass: the whole comparison chain runs again at the
    /// approach range, so a better monster appearing mid-walk is walked to
    /// instead.
    /// </summary>
    [Fact]
    public void TheWalkRepicksItsTargetOnEveryMoverFrame()
    {
        (FakeAutomation surface, CombatController controller) =
            ApproachRig(selfHeading: 0f, targetEastWest: 0.003d, targetNorthSouth: 0.1d);

        Assert.True(controller.ClaimMonsterApproachFromRulePass(canAct: true));
        Assert.Contains("Drudge", controller.Status, StringComparison.Ordinal);

        surface.Targets = [Target(20, "Mosswart", distance: 9, angle: 0)];
        surface.NavigationObjects[20u] = new PluginNavigationObject(
            20u,
            "Mosswart",
            new PluginNavigationPosition(0x7F7F0001, 0.003d, 0.1d, 0d, 0f, true));

        controller.StepArmedApproachMover(NavigationMover.MoverIntervalSeconds);

        Assert.Contains("Mosswart", controller.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// The walk ends where the attack begins: the character closes to weapon
    /// range and stops there. There is one stop distance and it is the same
    /// maximum target range the attack picks its own candidates at, whatever
    /// the weapon — a melee profile closes to melee range because that is
    /// what the profile's range says.
    /// </summary>
    [Fact]
    public void TheWalkStopsAtTheAttacksOwnMaximumRange()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 12, angle: 0)],
            NavigationSnapshot = NavigationAt(heading: 0f),
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(0x7F7F0001, 0.003d, 0.1d, 0d, 0f, true));
        // The owner's melee profile: eight metres of attack distance.
        var settings = new CombatSettings
        {
            MaximumRange = 8d,
            ApproachDistance = 25d,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();

        Assert.True(controller.ClaimMonsterApproachFromRulePass(canAct: true));
        int clearedBefore = surface.ClearMovementCount;

        // Still outside melee range: the walk carries on.
        surface.Targets = [Target(10, "Drudge", distance: 8.5f, angle: 0)];
        Assert.True(controller.ClaimMonsterApproachFromRulePass(canAct: true));

        // One step inside it, and the walk is over: the keys go down and the
        // attack row above takes it from here.
        surface.Targets = [Target(10, "Drudge", distance: 7.5f, angle: 0)];
        Assert.False(controller.ClaimMonsterApproachFromRulePass(canAct: true));
        Assert.Equal(clearedBefore + 1, surface.ClearMovementCount);
    }


    [Fact]
    public void HigherPriorityRuleWinsEvenWhenTargetIsFarther()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "Drudge", distance: 2, angle: 0),
                Target(20, "Olthoi Soldier", distance: 12, angle: 30),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Range,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(20u, surface.LastBeginTarget);
        Assert.Contains("Olthoi Soldier", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void SamePriorityTargetsDoNotFlipFlopBetweenScans()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "Drudge", distance: 3, angle: 0),
                Target(20, "Drudge", distance: 9, angle: 0),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(10u, surface.LastBeginTarget);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "Drudge", distance: 9, angle: 0),
            Target(20, "Drudge", distance: 2, angle: 0),
        ];
        controller.OnTick(0.25);

        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.Contains("9.0m", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetTextDistanceIsInvariantUnderASwedishCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            CultureInfo.CurrentUICulture = new CultureInfo("sv-SE");
            var surface = new FakeAutomation
            {
                CombatSnapshot = Physical(),
                Targets = [Target(10, "Drudge", distance: 9, angle: 0)],
                EquipmentItems = [WieldedPlannedWeapon()],
            };
            var settings = new CombatSettings
            {
                MaximumRange = 20f,
                SelectionMethod = TargetSelectionMethod.Range,
                ScanIntervalSeconds = 0.05d,
            };
            ProfileFixtureWeapon(settings);
            var controller = new CombatController(new FakeHost(surface), settings);

            controller.Toggle();
            controller.OnTick(0.25);

            Assert.Contains("9.0m", controller.TargetText, StringComparison.Ordinal);
            Assert.DoesNotContain("9,0m", controller.TargetText, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [Fact]
    public void StickyTargetStillYieldsToAHigherPriorityRule()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 3, angle: 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(10u, surface.LastBeginTarget);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "Drudge", distance: 3, angle: 0),
            Target(20, "Olthoi Soldier", distance: 15, angle: 60),
        ];
        controller.OnTick(1.0);

        Assert.Equal(20u, surface.LastBeginTarget);
    }

    [Fact]
    public void TargetLockKeepsCurrentTargetAcrossRescan()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "First", 4, 20)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            TargetLock = true,
            ScanIntervalSeconds = 0.1,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.1);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "First", 4, 20),
            Target(11, "Closer", 1, 0),
        ];
        controller.OnTick(0.1);

        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.Contains("First", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousValidTargetWinsAngleTieBreakWithoutTargetLock()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "First", 4, 1),
                Target(11, "Second", 4, 20),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            SelectionMethod = TargetSelectionMethod.Angle,
            TargetLock = false,
            ScanIntervalSeconds = 0.1,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.1);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "First", 4, 20),
            Target(11, "Second", 4, 1),
        ];
        controller.OnTick(0.1);

        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.Contains("First", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousTargetDoesNotBeatNewHigherPriorityRule()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.1,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.1);

        surface.CombatSnapshot = Physical();
        surface.Targets =
        [
            Target(10, "Drudge", 2, 0),
            Target(20, "Olthoi Soldier", 4, 20),
        ];
        controller.OnTick(1.0);

        Assert.Equal(20u, surface.LastBeginTarget);
        Assert.Contains("Olthoi Soldier", controller.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSelectionUsesAngleForNearTargetsAndRangeWhenNoneAreNear()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets =
            [
                Target(10, "Near side", 3, 80),
                Target(11, "Near ahead", 8, 5),
                Target(12, "Far", 20, 0),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Both,
            TargetSelectAngleRange = 10,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(11u, surface.LastBeginTarget);
    }

    [Fact]
    public void PhysicalAttackWaitsForConfiguredPowerBeforeRelease()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings { AttackPower = 0.6f };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(1, surface.BeginCount);

        surface.CombatSnapshot = Physical(
            request: true, build: true, bar: 0.59f) with
        {
            DesiredPower = 0.6f,
        };
        controller.OnTick(0.01);
        Assert.Equal(0, surface.ReleaseCount);

        surface.CombatSnapshot = Physical(
            request: true, build: true, bar: 0.60f) with
        {
            DesiredPower = 0.6f,
        };
        controller.OnTick(0.01);
        Assert.Equal(1, surface.ReleaseCount);
    }

    /// <summary>
    /// Summoning is a rule of its own that runs beside the pass, and refilling
    /// has its own places in the pass: the attack itself neither uses an
    /// essence nor waits on one.
    /// </summary>
    [Fact]
    public void TheAttackNeitherSummonsNorWaitsOnAPet()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            ItemEntries = [PetDevice(88, 49387)],
        };
        var settings = new CombatSettings { SummonPets = true };
        settings.CombatItemObjectIds.Add(88u);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.NotEqual(88u, surface.LastUsedItem);
        Assert.DoesNotContain("Summoning", controller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void MagicModeCastsBestProjectedAttackOnExplicitTarget()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells = [Spell(100, "Incantation of Flame Bolt")],
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface), FireAttackRule(new CombatSettings()));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        Assert.Contains("Flame Bolt", controller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void BreakableTurnFacesTargetBeforeDispatchingTargetedSpell()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    RequiresTurnTo = true,
                },
            ],
            EquipmentItems = [WieldedCaster()],
            NavigationSnapshot = NavigationAt(heading: 0f),
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(
                0x7F7F0001u,
                0.1d,
                0d,
                0d,
                0f,
                true));
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseBreakableTurnTo = true }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Empty(surface.MovementIntents);
        Assert.Equal(90f, Assert.Single(surface.FacedHeadings));
        Assert.Empty(surface.CastSpellIds);

        surface.NavigationSnapshot = NavigationAt(heading: 90f);
        controller.OnTick(0.25);

        Assert.Equal([100u], surface.CastSpellIds);
        Assert.Equal(2, surface.ClearMovementCount);
    }

    [Fact]
    public void BreakableTurnDoesNotReissueFaceHeadingInsideSevenTenthsOfASecond()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    RequiresTurnTo = true,
                },
            ],
            EquipmentItems = [WieldedCaster()],
            NavigationSnapshot = NavigationAt(heading: 0f),
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(
                0x7F7F0001u,
                0.1d,
                0d,
                0d,
                0f,
                true));
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseBreakableTurnTo = true }));

        controller.Toggle();
        controller.OnTick(0.293);
        controller.OnTick(0.293);

        Assert.Equal(90f, Assert.Single(surface.FacedHeadings));
        Assert.Empty(surface.CastSpellIds);

        controller.OnTick(0.293);
        Assert.Single(surface.FacedHeadings);
        controller.OnTick(0.293);
        Assert.Equal(2, surface.FacedHeadings.Count);
    }

    [Fact]
    public void ProjectileAwarenessBlocksSpellBeforeCastDispatch()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            ProjectilePath = new(
                PluginProjectilePathStatus.Blocked,
                CollisionChecks: 3,
                BlockingObjectId: 0x50000001u),
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseProjectileAwareness = true }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Empty(surface.CastSpellIds);
        Assert.Equal(10u, surface.LastProjectileTarget);
        // Nothing can be delivered to it, so it is out of the running for this
        // pass and there is nothing else to pick.
        Assert.False(controller.HasTarget);
    }

    [Fact]
    public void ProjectileAwarenessCanBeExplicitlyDisabled()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            ProjectilePath = new(PluginProjectilePathStatus.Blocked),
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseProjectileAwareness = false }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Contains(100u, surface.CastSpellIds);
        Assert.Equal(0u, surface.LastProjectileTarget);
    }

    /// <summary>
    /// A client that cannot test a flight has not said the flight is blocked.
    /// The spell is cast untested and the player is told the wall check is
    /// doing nothing.
    /// </summary>
    [Fact]
    public void AClientThatCannotTestAFlightDoesNotStopTheCast()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            ProjectilePath = new(PluginProjectilePathStatus.Unavailable),
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseProjectileAwareness = true }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Contains(100u, surface.CastSpellIds);
        Assert.Contains(
            surface.PostedSystemMessages,
            message => message.Contains(
                "cannot test projectile paths", StringComparison.Ordinal));
    }

    [Fact]
    public void CollisionDebugPublishesDiagnosticSamplesToTheGraphicalHost()
    {
        PluginProjectileDebugSample[] samples =
        [new(new System.Numerics.Vector3(1f, 2f, 3f), false, 0.4f)];
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            ProjectilePath = new(
                PluginProjectilePathStatus.Blocked,
                CollisionChecks: 1)
            {
                DebugSamples = samples,
            },
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings
            {
                UseProjectileAwareness = true,
                ShowCollisionDebug = true,
            }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(samples, surface.ShownProjectileDebugSamples);
    }

    [Fact]
    public void DoJiggleUsesAuthenticSelectionCycleInsteadOfMovingTheCharacter()
    {
        PluginSpellInfo attack = Spell(100, "Incantation of Flame Bolt") with
        {
            IsProjectile = false,
            School = 34,
            Difficulty = 300,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells = [attack],
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            new CombatSettings
            {
                UseProjectileAwareness = false,
                DoJiggle = true,
            });
        controller.Toggle();
        controller.OnTick(0.25);
        surface.LastCastCompletion = new PluginCastCompletion(1, 100, 10, 0);

        controller.OnTick(0.01);
        controller.OnTick(0.131);

        Assert.Equal(
            [
                PluginSelectionAction.PreviousSelection,
                PluginSelectionAction.NextPlayer,
                PluginSelectionAction.PreviousPlayer,
            ],
            surface.SelectionActions);
        Assert.Empty(surface.MovementIntents);
    }

    /// <summary>
    /// Mutation: drop the nudge window's deadline and this fails — the macro
    /// would go on cycling its selection for ever between casts instead of
    /// for one short window after each one.
    /// </summary>
    [Fact]
    public void TheNudgeStopsAfterItsOwnWindow()
    {
        PluginSpellInfo attack = Spell(100, "Incantation of Flame Bolt") with
        {
            IsProjectile = false,
            School = 34,
            Difficulty = 300,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells = [attack],
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            new CombatSettings
            {
                UseProjectileAwareness = false,
                DoJiggle = true,
            });
        controller.Toggle();
        controller.OnTick(0.25);
        surface.LastCastCompletion = new PluginCastCompletion(1, 100, 10, 0);
        controller.OnTick(0.01);

        for (int tick = 0; tick < 10; tick++)
            controller.OnTick(0.131);
        int afterTheWindow = surface.SelectionActions.Count;

        for (int tick = 0; tick < 20; tick++)
            controller.OnTick(0.131);

        Assert.Equal(afterTheWindow, surface.SelectionActions.Count);
        // One opening pulse plus the seven 0.131 s beats inside 0.907 s.
        Assert.InRange(afterTheWindow, 2, 9);
    }

    [Fact]
    public void MagicRuleDebuffsAndWaitsForServerReceiptBeforeAttack()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [imperil],
            KnownAttackSpells = [Spell(100, "Incantation of Flame Bolt")],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Imperil | MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((90u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(100u, surface.CastSpellIds);

        surface.LastCastCompletion = new PluginCastCompletion(1, 90, 10, 0);
        controller.OnTick(0.25);
        Assert.Contains(100u, surface.CastSpellIds);
    }

    /// <summary>
    /// The reference makes no distinction between a wand cast and a learned
    /// spell for its busy count: both hold the whole pass until the server
    /// answers, so a walk can never start under either. The host's own
    /// casting flag used to carry the spell case by accident. Mutation:
    /// answer false from <c>LearnedDebuffCastInFlight</c> and the middle
    /// assertion fails.
    /// </summary>
    [Fact]
    public void ALearnedDebuffCastIsReportedInFlightUntilTheServerAnswers()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [imperil],
            KnownAttackSpells = [Spell(100, "Incantation of Flame Bolt")],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Imperil | MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        Assert.False(controller.LearnedDebuffCastInFlight);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((90u, 10u), surface.LastTargetedCast);
        Assert.True(controller.LearnedDebuffCastInFlight);

        surface.LastCastCompletion = new PluginCastCompletion(1, 90, 10, 0);
        controller.OnTick(0.25);
        Assert.False(controller.LearnedDebuffCastInFlight);
    }

    /// <summary>
    /// That same cast holds the whole pass, and the attack is only asked
    /// anything on a pass it wins — which it cannot do while its own cast is
    /// what is holding the pass. So the answer has to be read on the host
    /// frame, with no turn at all, or the hold ends only on its watchdog.
    /// Mutation: make <c>ObserveLearnedDebuffReceipt</c> return without
    /// observing and the last assertion fails.
    /// </summary>
    [Fact]
    public void TheServersAnswerToALearnedDebuffIsReadOnTheFrameWithoutATurn()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [imperil],
            KnownAttackSpells = [Spell(100, "Incantation of Flame Bolt")],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Imperil | MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.True(controller.LearnedDebuffCastInFlight);

        // No OnTick from here: the pass is held, only frames run.
        surface.LastCastCompletion = new PluginCastCompletion(1, 90, 10, 0);
        controller.ObserveLearnedDebuffReceipt(0.05);

        Assert.False(controller.LearnedDebuffCastInFlight);
    }

    /// <summary>
    /// "You're too busy" is the server saying the character was not ready,
    /// not that the spell did anything. Reading that answer frees the wait at
    /// once, and with nothing else holding the macro back the very same cast
    /// went out again on the next pass, and the one after, dozens of times a
    /// second, for as long as the character stayed busy.
    /// <para>
    /// A refusal of that kind buys a short wait before the request may be
    /// made again — the same wait a refused dispel already takes.
    /// </para>
    /// Mutation: drop the wait and the second count check sees the recast.
    /// </summary>
    [Fact]
    public void ABusyRefusalHoldsTheNextDebuffRequestForItsWait()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [imperil],
            KnownAttackSpells = [Spell(100, "Incantation of Flame Bolt")],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Imperil | MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((90u, 10u), surface.LastTargetedCast);
        int issued = surface.CastSpellIds.Count;

        // The server's answer: refused, 0x1D, "You're too busy!".
        surface.LastCastCompletion = new PluginCastCompletion(1, 90, 10, 0x1Du);
        controller.ObserveLearnedDebuffReceipt(0.05);
        Assert.False(controller.LearnedDebuffCastInFlight);

        // The next pass, a frame later: nothing new goes out.
        controller.OnTick(0.05);
        Assert.Equal(issued, surface.CastSpellIds.Count);

        // Once the wait is up, the macro is free to try again.
        controller.OnTick(0.3);
        Assert.Equal(issued + 1, surface.CastSpellIds.Count);
    }

    [Fact]
    public void RingArmAlsoRequiresNoStreakColumnAndANonZeroTally()
    {
        PluginSpellInfo[] known =
        [
            MagicSpell(110, "Cassius' Ring of Fire", difficulty: 300) with
            {
                TargetMask = 0u,
                IsUntargeted = true,
            },
            MagicSpell(102, "Flame Streak VII", difficulty: 350),
            MagicSpell(100, "Flame Bolt VII", difficulty: 300),
        ];

        (uint untargeted, (uint, uint) targeted) = CastRingScenario(
            known,
            MonsterActionFlags.Ring | MonsterActionFlags.Streak,
            distance: 3f,
            ringDistance: 5d);
        Assert.Equal(0u, untargeted);
        Assert.Equal(102u, targeted.Item1);

        // Ring only, but the monster is beyond RingDistance so the tally is
        // zero: the ring arm fails and the pass bolts.
        (untargeted, targeted) = CastRingScenario(
            known,
            MonsterActionFlags.Ring,
            distance: 12f,
            ringDistance: 5d);
        Assert.Equal(0u, untargeted);
        Assert.Equal(100u, targeted.Item1);
    }

    /// <summary>
    /// The ring arm asks one question — are the components for the family's
    /// first rung in the pack — and commits. It does not also ask whether the
    /// client would start the cast this instant; when it would not, the ring
    /// is refused where every other refusal is reported, rather than quietly
    /// becoming a bolt at a monster the profile wanted ringed.
    /// Mutation: put the cast-gate test back in front of the ring choice and
    /// the bolt goes out instead.
    /// </summary>
    [Fact]
    public void TheRingArmAsksForComponentsAndNothingElse()
    {
        PluginSpellInfo[] known =
        [
            MagicSpell(110, "Cassius' Ring of Fire", difficulty: 300) with
            {
                TargetMask = 0u,
                IsUntargeted = true,
            },
            MagicSpell(100, "Flame Bolt VII", difficulty: 300),
        ];
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 3f, 0)],
            KnownCombatSpells = known,
            EquipmentItems = [WieldedCaster()],
        };
        // The client is not ready to start the ring this instant.
        surface.CastGates[110u] = PluginCastGate.Refused;
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            RingDistance = 5d,
            MinimumRingTargets = 1,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Ring,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(0u, surface.LastUntargetedCast);
        Assert.Equal((0u, 0u), surface.LastTargetedCast);
        Assert.Contains(
            "Cassius' Ring of Fire",
            controller.Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAMonsterThePassFollowsAndHasNotGivenUpOnIsPointable()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        var controller = new CombatController(new FakeHost(surface), settings);

        // Nothing has been scanned yet, so nothing is pointable.
        Assert.False(controller.IsTrackedAndNotBlacklisted(10u));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.True(controller.IsTrackedAndNotBlacklisted(10u));
        Assert.False(controller.IsTrackedAndNotBlacklisted(11u));
        Assert.False(controller.IsTrackedAndNotBlacklisted(0u));
    }

    [Fact]
    public void WithNoEquipmentProjectionTheAttackStillWaitsForCombatMode()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            EquipmentAvailable = false,
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", 300)],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);

        // Nothing is cast out of peace mode, and the mode is asked for.
        Assert.Empty(surface.CastSpellIds);
        Assert.Contains("EnterMode:Magic", surface.CallLog);
    }

    [Fact]
    public void TheDrainArmCastsWhatThePlannerChose()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets =
            [
                new PluginCombatTarget(
                    10u, "Olthoi Slasher", 1010u, 5f, 0f, true, 0.05f)
                {
                    HealthRevision = 4,
                },
            ],
            KnownCombatSpells =
            [
                MagicSpell(1237, "Drain Health Other I", difficulty: 100),
                MagicSpell(1238, "Drain Health Other II", difficulty: 150),
                MagicSpell(1239, "Drain Health Other III", difficulty: 200),
                MagicSpell(2760, "Martyr's Hecatomb I", difficulty: 100),
                MagicSpell(2761, "Martyr's Hecatomb II", difficulty: 150),
                MagicSpell(2762, "Martyr's Hecatomb III", difficulty: 200),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            MonsterFacts = new MonsterFactTable(GameInfo),
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.DrainAuto,
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            vitalSettings: null,
            gameInfo: GameInfo);
        controller.Toggle();
        controller.OnTick(0.25);

        // The database lists this monster as unaffectable by magic, so no
        // drain may be planned at all; among the martyrs the plan takes the
        // best monster health taken off per millisecond spent.
        Assert.Equal((2761u, 10u), surface.LastTargetedCast);
    }

    [Fact]
    public void CastsNoOneEverAnswersGetTheMonsterDeleted()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Olthoi Slasher", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            GhostMonsterSpellAttemptCount = 3,
            DeleteGhostMonsters = true,
            MonsterFacts = new MonsterFactTable(GameInfo),
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();

        // One pass issues the cast; the rule track is then held while it is in
        // flight, which is what the whole re-issue mechanism exists for.
        controller.OnTick(0.25);
        Assert.Contains(100u, surface.CastSpellIds);
        Assert.Equal(
            SpellCastTrackerState.AwaitingLaunch,
            controller.CastTracker.State);

        // The server never acknowledges it: the tracker re-sends every 200 ms,
        // and each re-send is one more unanswered attempt.
        for (int tick = 0; tick < 10; tick++)
            controller.CastTracker.Advance(0.1);

        Assert.Equal(10u, Assert.Single(surface.DismissedGhosts));
        Assert.Contains(
            surface.PostedSystemMessages,
            line => line.Contains("Deleting ghost monster Olthoi Slasher"));

        // Deleting it is the whole consequence. There is no permanent verdict
        // on the monster, so the very next pass picks it again.
        surface.CastSpellIds.Clear();
        controller.OnTick(0.25);
        Assert.Contains(100u, surface.CastSpellIds);
    }

    [Fact]
    public void OnlyAMonsterWithAKnownHealthCeilingCanGoStaleIntoAGhost()
    {
        FakeAutomation listed = StalledHealthScenario("Olthoi Slasher");
        Assert.NotEmpty(listed.DismissedGhosts);
        Assert.All(listed.DismissedGhosts, id => Assert.Equal(10u, id));
        Assert.Contains(
            listed.PostedSystemMessages,
            line => line.Contains("due to HP tracker notification"));

        // The database does not list "Drudge", so the client is never told
        // this monster's health in points and its silence means nothing.
        Assert.Empty(StalledHealthScenario("Drudge").DismissedGhosts);
    }

    /// <summary>
    /// Forgetting a monster whose health never moved is a profile choice. With
    /// it off, the very same silent monster is left alone however long the
    /// fight drags on, and the character keeps swinging at it.
    ///
    /// Mutation: sweep for silent monsters whatever the profile says and the
    /// second row forgets one.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ForgettingASilentMonsterIsAProfileChoice(
        bool byHealthTracker,
        bool forgets)
    {
        FakeAutomation surface = StalledHealthScenario(
            "Olthoi Slasher",
            byHealthTracker: byHealthTracker);

        Assert.Equal(forgets, surface.DismissedGhosts.Count != 0);
    }

    /// <summary>
    /// How long the silence has to last is the profile's number: thirty
    /// seconds of an unmoving health bar is long enough under a ten-second
    /// profile and not yet long enough under a sixty-second one.
    ///
    /// Mutation: compare against a constant and both rows answer the same way.
    /// </summary>
    [Theory]
    [InlineData(10d, true)]
    [InlineData(60d, false)]
    public void TheSilenceThatMakesAMonsterAGhostIsTheProfilesOwnLength(
        double staleSeconds,
        bool forgets)
    {
        FakeAutomation surface = StalledHealthScenario(
            "Olthoi Slasher",
            staleSeconds: staleSeconds);

        Assert.Equal(forgets, surface.DismissedGhosts.Count != 0);
    }

    private static FakeAutomation StalledHealthScenario(
        string name,
        bool byHealthTracker = true,
        double staleSeconds = 10d)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, name, 5, 0) with { HealthRevision = 3 }],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            DeleteGhostMonstersByHealthTracker = byHealthTracker,
            GhostDeleteHealthTrackerSeconds = staleSeconds,
            MonsterFacts = new MonsterFactTable(GameInfo),
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        for (int tick = 0; tick < 30; tick++)
            controller.OnTick(1d);
        return surface;
    }

    [Fact]
    public void TheRingTallyCountsOnlyValidCandidatesStrictlyInsideTheRing()
    {
        PluginSpellInfo[] known =
        [
            MagicSpell(110, "Cassius' Ring of Fire", difficulty: 300) with
            {
                TargetMask = 0u,
                IsUntargeted = true,
            },
            MagicSpell(100, "Flame Bolt VII", difficulty: 300),
        ];

        // Two monsters, both within RingDistance by the old inclusive test:
        // one exactly ON the ring boundary, which the strict comparison
        // excludes, so the tally is one and the pass bolts instead.
        Assert.Equal(
            100u,
            RingTallyScenario(
                known,
                [Target(10, "Drudge", 3f, 0), Target(11, "Drudge", 5f, 0)],
                minimumRange: 0d).Targeted.Item1);

        // Same, but the second monster is nearer than AttackMinimumDistance,
        // so it is not a candidate at all and cannot be tallied.
        Assert.Equal(
            100u,
            RingTallyScenario(
                known,
                [Target(10, "Drudge", 3f, 0), Target(11, "Drudge", 0.5f, 0)],
                minimumRange: 1d).Targeted.Item1);

        // Two valid candidates strictly inside the ring: the ring fires.
        Assert.Equal(
            110u,
            RingTallyScenario(
                known,
                [Target(10, "Drudge", 3f, 0), Target(11, "Drudge", 4f, 0)],
                minimumRange: 0d).Untargeted);
    }

    private static (uint Untargeted, (uint, uint) Targeted) RingTallyScenario(
        IReadOnlyList<PluginSpellInfo> known,
        IReadOnlyList<PluginCombatTarget> targets,
        double minimumRange)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = targets,
            KnownCombatSpells = known,
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            RingDistance = 5d,
            MinimumRange = minimumRange,
            MinimumRingTargets = 2,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Ring | MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        return (surface.LastUntargetedCast, surface.LastTargetedCast);
    }

    private static (uint Untargeted, (uint, uint) Targeted) CastRingScenario(
        IReadOnlyList<PluginSpellInfo> known,
        MonsterActionFlags flags,
        float distance,
        double ringDistance)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", distance, 0)],
            KnownCombatSpells = known,
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            RingDistance = ringDistance,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = flags,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        return (surface.LastUntargetedCast, surface.LastTargetedCast);
    }

    [Fact]
    public void StrongerBoltBeatsTheArcEvenWithUseArcsYes()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 20, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                MagicSpell(101, "Flame Arc IV", difficulty: 150),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            UseArcs = UseArcsMode.Yes,
            ArcRange = 1d,
            MaximumRange = 40d,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
    }

    /// <summary>
    /// A profile saved before the arc default was corrected has no arc key at
    /// all, and loading one must not put arcing back: a fresh character bolts
    /// at five metres where an arcing one would throw over the monster's head.
    /// There are two defaults on this road, and each load below takes one of
    /// them: a stored combat section with no arc key reads the sidecar
    /// record's, and a stored profile with no combat section at all reads the
    /// live settings object's.
    /// Mutation: set the sidecar record's default back to "at range" and the
    /// first pair of assertions fails; set the settings object's own back and
    /// the last one does.
    /// </summary>
    [Fact]
    public void AProfileWithNoArcSettingLoadsWithArcsOff()
    {
        var storage = new MemoryStorage();
        storage.WriteText(
            "profile.json",
            """{ "combat": { "maximumRange": 5.0 } }""");
        var store = new MossTankProfileStore(
            new StorageHost(storage, "Acdream", "Fixture"));
        store.BindCharacter("Acdream");
        var settings = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = new CombatSettings(),
            Buffs = new BuffSettings(),
            Vitals = new VitalSettings(),
            Inventory = new InventorySettings(),
            Navigation = new NavigationSettings(),
        };

        store.LoadCurrent(
            settings,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(UseArcsMode.No, settings.Combat.UseArcs);
        Assert.Equal(5d, settings.Combat.ArcRange, precision: 6);

        // The other road: nothing combat-shaped is stored at all, so the load
        // falls back on the settings object's own declared default.
        var bareStorage = new MemoryStorage();
        bareStorage.WriteText("profile.json", "{ }");
        var bareStore = new MossTankProfileStore(
            new StorageHost(bareStorage, "Acdream", "Fixture"));
        bareStore.BindCharacter("Acdream");
        var bare = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = new CombatSettings { UseArcs = UseArcsMode.Yes },
            Buffs = new BuffSettings(),
            Vitals = new VitalSettings(),
            Inventory = new InventorySettings(),
            Navigation = new NavigationSettings(),
        };

        bareStore.LoadCurrent(
            bare,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(UseArcsMode.No, bare.Combat.UseArcs);
    }

    /// <summary>
    /// Every road onto a different profile has to leave the old profile's
    /// imported tables behind. Seeding from the shipped defaults used to read
    /// only some of them, so the profile just left went on saying which hand
    /// each item is used in and what each consumable is for.
    /// Mutation executed: <c>seeded from the defaults without re-reading the imported tables</c>.
    /// </summary>
    [Fact]
    public void SeedingFromTheDefaultsDropsThePreviousImportedTables()
    {
        var storage = new MemoryStorage();
        var store = new MossTankProfileStore(
            new StorageHost(storage, "Acdream", "Fixture"));
        store.BindCharacter("Acdream");
        var settings = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = new(), Buffs = new(), Vitals = new(),
            Inventory = new(), Navigation = new(),
        };
        var noBuffs = new HashSet<string>(StringComparer.Ordinal);
        var logs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // No file for this character yet: the load seeds from the defaults.
        StaleImportedTables(settings.Combat);
        Assert.Equal(
            MossTankProfileLoad.Missing,
            store.LoadCurrent(settings, noBuffs, logs));
        Assert.Empty(settings.Combat.ItemUseSpecifiers);
        Assert.Empty(settings.Combat.ImportedAssistItems);
        Assert.Empty(settings.Combat.CombatItemObjectIds);

        // And a brand new profile, which seeds from the same defaults.
        StaleImportedTables(settings.Combat);
        Assert.True(store.Create(
            "Empty", copyCurrent: false, settings, noBuffs, logs, out _));
        Assert.Empty(settings.Combat.ItemUseSpecifiers);
        Assert.Empty(settings.Combat.ImportedAssistItems);
        Assert.Empty(settings.Combat.CombatItemObjectIds);

        static void StaleImportedTables(CombatSettings combat)
        {
            combat.ItemUseSpecifiers[901u] = 1;
            combat.ImportedAssistItems.Add(
                new AssistItem("Stale Pea", ConsumableCategory.Pea));
            combat.CombatItemObjectIds.Add(901u);
        }
    }

    /// <summary>
    /// Mutation pin: clear ordered IDs while applying the name sidecar;
    /// the first reload loses both identities.
    /// </summary>
    [Fact]
    public void ProfileReloadKeepsUsdItemIdsAcrossSidecarAndSwitches()
    {
        var storage = new MemoryStorage();
        var store = new MossTankProfileStore(
            new StorageHost(storage, "Acdream", "Fixture"));
        store.BindCharacter("Acdream");
        var settings = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = new(), Buffs = new(), Vitals = new(),
            Inventory = new(), Navigation = new(),
        };
        var noBuffs = new HashSet<string>(StringComparer.Ordinal);
        var logs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        settings.Combat.CombatItemNames.Add("Same Wand");
        settings.Combat.CombatItemOrder.Add("Same Wand");
        settings.Combat.CombatItemObjectIds.UnionWith([801u, 802u]);
        settings.Combat.CombatItemOrderIds.Add(802u);
        settings.Combat.CombatItemOrderIds.Add(801u);
        store.SaveCurrent(settings, noBuffs, logs);

        settings.Combat.CombatItemObjectIds.Clear();
        settings.Combat.CombatItemOrderIds.Clear();
        store.LoadCurrent(settings, noBuffs, logs);
        Assert.Equal([802u, 801u], settings.Combat.CombatItemOrderIds);
        Assert.Equal(["Same Wand"], settings.Combat.CombatItemOrder);

        Assert.True(store.Create("Empty", copyCurrent: false,
            settings, noBuffs, logs, out _));
        Assert.Empty(settings.Combat.CombatItemOrderIds);
        Assert.True(store.Select(MossTankProfileStore.ByCharacter));
        store.LoadCurrent(settings, noBuffs, logs);
        Assert.Equal([802u, 801u], settings.Combat.CombatItemOrderIds);
        Assert.True(store.Select("Empty"));
        store.LoadCurrent(settings, noBuffs, logs);
        Assert.Empty(settings.Combat.CombatItemOrderIds);
    }

    [Fact]
    public void UseArcsDecidesOnlyAnExactQualityTie()
    {
        PluginSpellInfo[] known =
        [
            MagicSpell(100, "Flame Bolt VII", difficulty: 300),
            MagicSpell(101, "Flame Arc VII", difficulty: 300),
        ];

        Assert.Equal((101u, 10u), CastWithUseArcs(known, UseArcsMode.Yes, distance: 20));
        Assert.Equal((100u, 10u), CastWithUseArcs(known, UseArcsMode.No, distance: 20));
        // "At range" arcs only from the arc range outwards; inside it the
        // bolt still wins.
        Assert.Equal(
            (101u, 10u),
            CastWithUseArcs(known, UseArcsMode.AtRange, distance: 20, arcRange: 10));
        Assert.Equal(
            (100u, 10u),
            CastWithUseArcs(known, UseArcsMode.AtRange, distance: 5, arcRange: 10));
    }

    private static (uint, uint) CastWithUseArcs(
        IReadOnlyList<PluginSpellInfo> known,
        UseArcsMode mode,
        double distance,
        double arcRange = 1d)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", (float)distance, 0)],
            KnownCombatSpells = known,
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings
            {
                UseArcs = mode,
                ArcRange = arcRange,
                MaximumRange = 40d,
            }));
        controller.Toggle();
        controller.OnTick(0.25);
        return surface.LastTargetedCast;
    }

    /// <summary>
    /// The per-tick budget counts attempts, and one attempt evaluates one
    /// chosen monster: four monsters the character cannot shoot cost four
    /// attempts with a budget of four, and only two with a budget of two. The
    /// budget is the ceiling on the whole pass, not a ceiling on each monster.
    ///
    /// Mutation: read the budget as the number of extra choices after the
    /// first, or ignore it, and the counted attempts stop matching.
    /// </summary>
    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public void ThePerTickBudgetCapsHowManyMonstersOnePassEvaluates(
        int budget,
        int attempts)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets =
            [
                Target(10, "Drudge", 5, 0),
                Target(11, "Drudge", 6, 0),
                Target(12, "Drudge", 7, 0),
                Target(13, "Drudge", 8, 0),
            ],
            KnownCombatSpells =
            [
                MagicSpell(102, "Flame Streak VII", difficulty: 350),
            ],
            ProjectilePath = new(
                PluginProjectilePathStatus.Blocked,
                CollisionChecks: 3,
                BlockingObjectId: 0x50000001u),
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
            MaximumCollisionChecksPerTick = budget,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Streak,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        var lines = new List<string>();
        controller.Log = (_, text) => lines.Add(text);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Contains(
            lines,
            line => line.EndsWith($"Loop iterations: {attempts}", StringComparison.Ordinal));
        Assert.Empty(surface.CastSpellIds);
    }

    /// <summary>
    /// With the flight check off there is nothing an undeliverable decision
    /// could teach the pass, so the pass gets exactly one attempt whatever the
    /// budget says. The two settings are coupled.
    ///
    /// Mutation: honour the budget with the flight check off and the pass
    /// churns through every monster in the scan.
    /// </summary>
    [Fact]
    public void TheBudgetIsIgnoredWhileTheFlightCheckIsOff()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets =
            [
                Target(10, "Drudge", 5, 0),
                Target(11, "Drudge", 6, 0),
                Target(12, "Drudge", 7, 0),
                Target(13, "Drudge", 8, 0),
            ],
            KnownCombatSpells = [],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = false,
            MaximumCollisionChecksPerTick = 4,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Streak,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        var lines = new List<string>();
        controller.Log = (_, text) => lines.Add(text);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Contains(
            lines,
            line => line.EndsWith("Loop iterations: 1", StringComparison.Ordinal));
    }

    /// <summary>
    /// Mutation: drop the streak's flight test and the first assertion fails
    /// — the streak is cast straight into the wall, every pass, for ever.
    /// </summary>
    [Fact]
    public void AStreakIntoCoverIsRefusedAndTakesItsColumnWithIt()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(102, "Flame Streak VII", difficulty: 350),
            ],
            ProjectilePath = new(
                PluginProjectilePathStatus.Blocked,
                CollisionChecks: 3,
                BlockingObjectId: 0x50000001u),
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Streak,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Empty(surface.CastSpellIds);
        // The monster stops being a candidate once its only column is off, so
        // the pass stops rather than spinning against it.
        Assert.False(controller.HasTarget);
        Assert.InRange(surface.ProjectilePathChecks, 1, 4);
    }

    /// <summary>
    /// Mutation: pass <c>settings.AttackHeight</c> for either shape and this
    /// fails — the ray would start at the configured melee height instead of
    /// the height the shape itself flies at.
    /// </summary>
    [Fact]
    public void BoltAndArcClearanceUseTheShapesOwnHeight()
    {
        PluginSpellInfo[] known =
        [
            MagicSpell(100, "Flame Bolt VII", difficulty: 300) with
            {
                IsProjectile = true,
            },
            MagicSpell(101, "Flame Arc VII", difficulty: 300) with
            {
                IsProjectile = true,
            },
        ];

        Assert.Equal(
            PluginAttackHeight.Medium,
            ClearanceHeightFor(known, UseArcsMode.No));
        Assert.Equal(
            PluginAttackHeight.High,
            ClearanceHeightFor(known, UseArcsMode.Yes));
    }

    /// <summary>
    /// Grenades come from the game database in use: with the built-in one
    /// (no GrenadeOptions rows) the phial is nothing, and once a downloaded
    /// database replaces it the very next pass throws the phial. Mutation:
    /// read the grenade table from the database the controller was built
    /// with, and the phial is never thrown.
    /// </summary>
    [Fact]
    public void GrenadesComeFromTheGameDatabaseInUse()
    {
        PluginSpellInfo imperil = Spell(1323, "Imperil Other I") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        PluginInventoryItem phial = InventoryItem(
            200, "Iron Phial of Imperil", 0x100, 0, equipped: false)
            with { CombatUse = 0 };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Missile },
            Targets = [Target(10, "Drudge", 5, 0)],
            SpellLookup = [imperil],
            ItemEntries = [phial],
            EquipmentItems = [Equipment(200u, "Iron Phial of Imperil",
                damageType: 0, itemType: 0x100u)],
            CharacterSkills = [new(38u, "Alchemy", PluginSkillTraining.Trained, 400)],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.ConsumableNames.Add("Iron Phial of Imperil");
        settings.UseProjectileAwareness = true;
        var controller = new CombatController(
            new FakeHost(surface), settings, gameInfo: VtankGameInfoDatabase.LoadDefault());

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(0u, surface.LastProjectileTarget);

        controller.ReplaceGameInfo(GameInfo);
        controller.OnTick(0.25);

        Assert.Equal(10u, surface.LastProjectileTarget);
        Assert.Equal(PluginProjectilePathKind.Missile, surface.LastProjectileKind);
    }

    /// <summary>
    /// A debuff's way to the monster is tested at the height its own flight
    /// takes, exactly as an attack's is: a thrown phial is tested at the
    /// profile's swing height, not at a fixed level one.
    /// Mutation: hard-code <c>PluginAttackHeight.Medium</c> in the debuff
    /// clearance check again and this fails.
    /// </summary>
    [Fact]
    public void ADebuffsClearanceUsesItsOwnFlightHeight()
    {
        PluginSpellInfo imperil = Spell(1323, "Imperil Other I") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        PluginInventoryItem phial = InventoryItem(
            200, "Iron Phial of Imperil", 0x100, 0, equipped: false)
            with { CombatUse = 0 };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Missile },
            Targets = [Target(10, "Drudge", 5, 0)],
            SpellLookup = [imperil],
            ItemEntries = [phial],
            EquipmentItems = [Equipment(200u, "Iron Phial of Imperil",
                damageType: 0, itemType: 0x100u)],
            CharacterSkills = [new(38u, "Alchemy", PluginSkillTraining.Trained, 400)],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.ConsumableNames.Add("Iron Phial of Imperil");
        settings.UseProjectileAwareness = true;
        // Deliberately neither of the two shape heights.
        settings.AttackHeight = PluginAttackHeight.Low;
        var controller = new CombatController(new FakeHost(surface), settings, gameInfo: GameInfo);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(
            PluginProjectilePathKind.Missile,
            surface.LastProjectileKind);
        Assert.Equal(PluginAttackHeight.Low, surface.LastProjectileHeight);
    }

    /// <summary>
    /// The flight check is a fat ray walked in steps, and the profile sets
    /// both: how wide the ray is and how far apart the samples along it are.
    /// A profile that widens the ray or shortens the stride asks a different
    /// question of the client, so both numbers have to reach it unchanged.
    ///
    /// Mutation: send a constant width or stride and the asked-for shape stops
    /// matching the profile.
    /// </summary>
    [Theory]
    [InlineData(0.4d, 0.7d)]
    [InlineData(1.25d, 0.2d)]
    public void TheFlightCheckIsAskedForInTheProfilesOwnShape(
        double radius,
        double step)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(102, "Flame Streak VII", difficulty: 350),
            ],
            ProjectilePath = new(
                PluginProjectilePathStatus.Blocked,
                CollisionChecks: 3,
                BlockingObjectId: 0x50000001u),
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
            CollisionProjectileRadius = radius,
            CollisionStepDistance = step,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Streak,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.True(surface.ProjectilePathChecks > 0);
        Assert.Equal((float)radius, surface.LastProjectileRadius);
        Assert.Equal((float)step, surface.LastProjectileStepDistance);
    }

    /// <summary>
    /// Jumping clear of a wand cast is a profile choice: with it on the
    /// character is asked to jump once shortly after the wand went off, and
    /// with it off it is not asked to jump at all.
    ///
    /// Mutation: jump whatever the profile says, or never jump, and one of the
    /// two rows fails.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JumpingClearOfAWandCastIsAProfileChoice(bool jumps)
    {
        PluginSpellInfo imperil = Spell(1323, "Imperil Other VI") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            SpellLookup = [imperil],
            ItemEntries =
            [
                InventoryItem(200, "Wand of Imperil", 0x8000u, 0, equipped: true)
                    with { SpellId = imperil.SpellId },
            ],
            EquipmentItems =
            [
                Equipment(200u, "Wand of Imperil", damageType: 0, itemType: 0x8000u),
            ],
        };
        CombatSettings settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.MaximumRange = 40d;
        settings.JumpOutWandCasting = jumps;
        settings.CombatItemNames.Add("Wand of Imperil");
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 8; tick++)
            controller.OnTick(0.25);

        Assert.Equal(
            jumps,
            surface.MovementIntents.Any(static intent => intent.Jump));
    }

    private static PluginAttackHeight ClearanceHeightFor(
        IReadOnlyList<PluginSpellInfo> known,
        UseArcsMode mode)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = known,
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
            UseArcs = mode,
            // Deliberately neither of the two shape heights.
            AttackHeight = PluginAttackHeight.Low,
        });
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        return surface.LastProjectileHeight;
    }

    [Fact]
    public void AttackPlusStreakUsesTheStreakOnlyAsAFinishingBlow()
    {
        PluginSpellInfo[] known =
        [
            MagicSpell(100, "Flame Bolt VII", difficulty: 300),
            MagicSpell(102, "Flame Streak VII", difficulty: 350),
        ];

        // "Olthoi Slasher" is listed in the fixture database at 2000 health,
        // and the streak's difficulty of 350 sets the bar at 50 points until a
        // real blow is seen: 1800 left bolts, 20 left finishes.
        Assert.Equal(100u, CastAgainstHealth(known, healthFraction: 0.9f).Item1);
        Assert.Equal(102u, CastAgainstHealth(known, healthFraction: 0.01f).Item1);

        // A monster the database does not list has no health in points at
        // all, so the finishing move can never be chosen for it.
        Assert.Equal(
            100u,
            CastAgainstHealth(known, healthFraction: 0.01f, name: "Drudge").Item1);
    }

    private static (uint, uint) CastAgainstHealth(
        IReadOnlyList<PluginSpellInfo> known,
        float healthFraction,
        string name = "Olthoi Slasher")
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets =
            [
                new PluginCombatTarget(
                    10u, name, 1010u, 5f, 0f, true, healthFraction)
                {
                    HealthRevision = 7,
                },
            ],
            KnownCombatSpells = known,
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            MonsterFacts = new MonsterFactTable(GameInfo),
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack | MonsterActionFlags.Streak,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        return surface.LastTargetedCast;
    }

    private static PluginSpellInfo MagicSpell(
        uint id,
        string name,
        int difficulty) => new(
            id,
            name,
            Family: id,
            Tier: 7,
            Difficulty: difficulty,
            ManaCost: 30,
            DurationSeconds: 0,
            School: 34,
            Description: string.Empty,
            IsSelfTargeted: false,
            IsBeneficial: false)
        {
            IsOffensive = true,
            Saying = "testwords",
            TargetMask = 0x10,
        };

    [Fact]
    public void DebuffsGoOutInAuthenticTwelveStepOrder()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                Debuff(80, "Fester Other VII"),
                Debuff(81, "Broadside of a Barn"),
                Debuff(82, "Fire Vulnerability Other VII"),
                Debuff(83, "Imperil Other VII"),
                Debuff(84, "Magic Yield Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Yield
                    | MonsterActionFlags.Imperil
                    | MonsterActionFlags.Vulnerability
                    | MonsterActionFlags.Broadside
                    | MonsterActionFlags.Fester,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();

        long revision = 0;
        foreach (uint expected in new uint[] { 84u, 83u, 82u, 81u, 80u })
        {
            controller.OnTick(0.25);
            Assert.Equal((expected, 10u), surface.LastTargetedCast);
            surface.LastCastCompletion = new PluginCastCompletion(
                ++revision, expected, 10u, 0);
            controller.OnTick(0.25);
        }
    }

    /// <summary>
    /// A debuff is renewed before it lapses, and the profile says how far
    /// before: the step comes due once the enchantment has that many seconds
    /// left. Forty-five seconds into a sixty-second curse, a five-second lead
    /// is not due yet and a twenty-second one is.
    ///
    /// Mutation: renew only on a lapsed enchantment (a zero lead) and the
    /// wide-lead row never recasts.
    /// </summary>
    [Theory]
    [InlineData(5d, false)]
    [InlineData(20d, true)]
    public void TheDebuffLeadDecidesWhenACurseIsRenewed(
        double precastSeconds,
        bool renews)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [Debuff(83, "Imperil Other VII")],
            EquipmentItems = [WieldedCaster()],
        };
        CombatSettings settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.MaximumRange = 40d;
        settings.DebuffPrecastSeconds = precastSeconds;
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();

        // The curse goes out and the server answers it: sixty seconds on it.
        controller.OnTick(0.25);
        Assert.Equal([83u], surface.CastSpellIds);
        surface.LastCastCompletion = new PluginCastCompletion(1L, 83u, 10u, 0);
        controller.OnTick(0.25);

        // Forty-five seconds on, with fifteen seconds of curse left.
        for (int frame = 0; frame < 180; frame++)
            controller.OnTick(0.25);

        Assert.Equal(renews, surface.CastSpellIds.Count > 1);
    }

    [Fact]
    public void OnlyOneDebuffKindIsDispatchedPerPass()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                Debuff(83, "Imperil Other VII"),
                Debuff(84, "Magic Yield Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Yield
                    | MonsterActionFlags.Imperil,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal([84u], surface.CastSpellIds);
    }

    [Fact]
    public void BlockedDebuffPathDropsThatColumnAndTheChainMovesOn()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                // The first step of the debuff chain, and the only spell here
                // whose family declares a flight (117 is a bolt family).
                Debuff(84, "Magic Yield Other VII") with { Family = 117u },
                // Step 7.
                Debuff(83, "Imperil Other VII"),
            ],
            ProjectilePath = new(
                PluginProjectilePathStatus.Blocked,
                CollisionChecks: 3,
                BlockingObjectId: 0x50000001u),
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
        };
        Assert.False(settings.AllowDebuffFallback);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Yield
                    | MonsterActionFlags.Imperil,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((83u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(84u, surface.CastSpellIds);
    }

    /// <summary>
    /// Mutation: put the bare mode change back in place of the wield gate and
    /// this fails — the debuff goes out with the sword still in hand, so the
    /// wand's own spellcraft and mana never pay for it.
    /// </summary>
    [Fact]
    public void ALearnedDebuffWieldsAWandBeforeItIsCast()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Melee },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [Debuff(83, "Imperil Other VII")],
            EquipmentItems =
            [
                WieldedPlannedWeapon(),
                Equipment(
                    990u,
                    "Fixture Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
            ],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.MaximumRange = 40d;
        settings.CombatItemNames.Add("Fixture Wand");
        settings.CombatItemOrderIds.Add(990u);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        // Peace first, then the wand: nothing is cast while the sword is in
        // hand.
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(990u, surface.LastEquipObjectId);
        Assert.Empty(surface.CastSpellIds);

        // The server confirms the swap: the sword is away, the wand is in.
        surface.EquipmentItems =
        [
            Equipment(
                990u,
                "Fixture Wand",
                damageType: 0,
                itemType: 0x00008000u,
                equippedLocation: 0x00100000u),
        ];
        surface.EmitPlacement(new PluginEquipmentObservation(991u, 0u, true));
        surface.EmitPlacement(new PluginEquipmentObservation(
            990u, 0x00100000u, false));
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal((83u, 10u), surface.LastTargetedCast);
    }

    /// <summary>
    /// Mutation: drop the <c>SwitchWandsToDebuff</c> branch and this fails —
    /// with the setting off the debuff always reaches for the first profiled
    /// wand, so the two arms must pick different wands here.
    /// </summary>
    [Fact]
    public void SwitchWandsToDebuffDebuffsWithTheCasterAttackWeapon()
    {
        Assert.Equal(991u, DebuffWandFor(switchWandsToDebuff: true));
        Assert.Equal(990u, DebuffWandFor(switchWandsToDebuff: false));
    }

    private static uint DebuffWandFor(bool switchWandsToDebuff)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [Debuff(83, "Imperil Other VII")],
            EquipmentItems =
            [
                Equipment(
                    991u,
                    "Attack Wand",
                    damageType: 0x0010,
                    itemType: 0x00008000u),
                Equipment(
                    990u,
                    "Spare Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            SwitchWandsToDebuff = switchWandsToDebuff,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Imperil,
                WeaponName = "Attack Wand",
            }));
        settings.CombatItemNames.Add("Attack Wand");
        settings.CombatItemNames.Add("Spare Wand");
        settings.CombatItemOrder.Add("Spare Wand");
        settings.CombatItemOrderIds.Add(990u);
        settings.CombatItemOrderIds.Add(991u);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        return surface.LastEquipObjectId;
    }

    /// <summary>
    /// Mutation: drop the range term from the debuff source walk and the
    /// first half fails — an out-of-reach spell would be chosen and refused.
    /// </summary>
    [Fact]
    public void ADebuffSpellOutOfReachIsNotChosen()
    {
        Assert.Equal((0u, 0u), DebuffAtDistance(reach: 4f, distance: 6f));
        Assert.Equal((83u, 10u), DebuffAtDistance(reach: 20f, distance: 6f));
    }

    private static (uint, uint) DebuffAtDistance(float reach, float distance)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", distance, 0)],
            KnownCombatSpells =
            [
                Debuff(83, "Imperil Other VII") with
                {
                    BaseRangeConstant = reach,
                },
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.MaximumRange = 40d;
        settings.SpellRangeFudge = 0d;
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        return surface.LastTargetedCast;
    }

    /// <summary>
    /// Mutation: restore the walk over every source and this fails — the item
    /// would be used after the out-of-reach spell dropped out AND after the
    /// winner failed, instead of exactly one source being chosen per decision.
    /// </summary>
    [Fact]
    public void AnOutOfReachSpellLeavesTheItemAsTheDebuffSource()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
            BaseRangeConstant = 4f,
        };
        PluginInventoryItem lens = InventoryItem(
            800, "Imperil Lens", 0x8000, 90, equipped: true) with
        {
            ItemSpellcraft = 400,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 6, 0)],
            KnownCombatSpells = [imperil],
            SpellLookup = [imperil],
            ItemEntries = [lens],
            // The learned spell would out-rank the lens on skill; only its
            // reach keeps it out of the choice.
            CharacterSkills = [new(31u, "Creature", PluginSkillTraining.Trained, 500)],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.MaximumRange = 40d;
        settings.SpellRangeFudge = 0d;
        settings.CombatItemObjectIds.Add(lens.ObjectId);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((800u, 10u), surface.LastAppliedItem);
        Assert.Empty(surface.CastSpellIds);
    }

    [Fact]
    public void BlockedDebuffPathStillLetsTheAttackGoOutOnStockSettings()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                Debuff(84, "Magic Yield Other VII") with { Family = 117u },
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
            ],
            ProjectilePath = new(PluginProjectilePathStatus.Blocked),
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack | MonsterActionFlags.Yield,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
    }

    [Fact]
    public void AnUnknownVulnScoresNoUrgencyForTheTargetItCannotDebuff()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets =
            [
                Target(10, "Drudge", distance: 9, angle: 0),
                Target(20, "Rat", distance: 2, angle: 0),
            ],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                MagicSpell(101, "Acid Stream VII", difficulty: 300),
                // The Fire Vuln line is deliberately absent.
                Debuff(85, "Acid Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 40d,
            SelectionMethod = TargetSelectionMethod.Range,
        };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "name#^Drudge",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        settings.Rules.Add(new MonsterRule(
            "name#^Rat",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Acid,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((101u, 20u), surface.LastTargetedCast);
    }

    [Fact]
    public void ThePlannedWeaponsOwnElementPreemptsTheDamageTable()
    {
        PluginEquipmentItem fireWand = Equipment(
            990u,
            "Flame Wand",
            damageType: 0x0010,
            itemType: 0x00008000u,
            equippedLocation: 0x00100000u);
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Magma Golem", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                MagicSpell(102, "Frost Bolt VII", difficulty: 300),
            ],
            EquipmentItems = [fireWand],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
                WeaponName = "Flame Wand",
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            vitalSettings: null,
            GameInfo);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
    }

    /// <summary>
    /// Mutation: read only the weapon's plain damage again and the first half
    /// fails; apply the caster training cascade whatever the weapon is and the
    /// second fails — a martyr mage's melee build would be handed a drain.
    /// </summary>
    [Fact]
    public void AnImbuedMeleeWeaponKeepsItsOwnElementForALifeOnlyCaster()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                Debuff(70, "Fire Vulnerability Other VII"),
                Debuff(71, "Cold Vulnerability Other VII"),
            ],
            EquipmentItems =
            [
                Equipment(
                    990u,
                    "Spare Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
                Equipment(
                    991u,
                    "Imbued Sword",
                    // Plainly a slashing sword; its imbue rends fire.
                    damageType: 0x0001,
                    equippedLocation: 0x00100000u) with
                {
                    ImbuedEffect = 0x0200,
                },
            ],
            // Life magic only: no war, no void.
            CharacterSkills =
                [new(33u, "Life Magic", PluginSkillTraining.Trained, 300)],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.CombatItemNames.Add("Spare Wand");
        settings.CombatItemNames.Add("Imbued Sword");
        settings.CombatItemOrderIds.Add(990u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Vulnerability,
                DamageType = MonsterDamageType.Auto,
                ExtraVulnerability = MonsterDamageType.None,
                WeaponName = "Imbued Sword",
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 5; tick++)
            controller.OnTick(0.25);

        Assert.Equal((70u, 10u), surface.LastTargetedCast);
    }

    /// <summary>
    /// Mutation: make "can this element be delivered" mean "do I know a spell
    /// or own a weapon of it" again and this fails — that question can always
    /// be answered yes by some spell, so the empty-quiver warning would never
    /// be reached. Only a launcher can fail to deliver, and only for want of
    /// ammunition.
    /// </summary>
    [Fact]
    public void ABowWithAnEmptyPackCanDeliverNoElementAtAll()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Magma Golem", 5, 0)],
            EquipmentItems =
            [
                Equipment(
                    900u,
                    "Yumi",
                    damageType: 0,
                    itemType: 0x100,
                    equippedLocation: 0x00100000u,
                    ammoType: 0x001u),
            ],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.CombatItemNames.Add("Yumi");
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            vitalSettings: null,
            GameInfo);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Contains(
            surface.PostedSystemMessages,
            message => message.Contains(
                "No ammunition available at all!",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Mutation: skip a preference the character has no spell for and this
    /// fails — a wand can always deliver, so the monster's FIRST listed
    /// weakness is the one that is used even when nothing is known for it.
    /// </summary>
    [Fact]
    public void TheLoadedGameInfoDatabaseDrivesTheAutoAttackElement()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Magma Golem", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(103, "Force Bolt VII", difficulty: 300),
                MagicSpell(105, "Shock Wave VII", difficulty: 300),
                // The Magma Golem's first listed weakness is cold.
                MagicSpell(104, "Frost Bolt VII", difficulty: 300),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            vitalSettings: null,
            GameInfo);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((104u, 10u), surface.LastTargetedCast);
    }

    /// <summary>
    /// The extra vulnerability column is off unless the row asks for it. A
    /// default row with every debuff column unticked debuffs nothing, however
    /// weak the monster is to an element.
    /// Mutation: default <c>MonsterRuleActions.ExtraVulnerability</c> to
    /// automatic again and this fails - the column resolves to the Magma
    /// Golem's first listed weakness and an element vulnerability goes out
    /// before the first bolt, which is what the live session showed.
    /// </summary>
    [Fact]
    public void ADefaultRowWithNoDebuffColumnsCastsNoVulnerability()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Magma Golem", 5, 0)],
            KnownCombatSpells =
            [
                // The Magma Golem's first listed weakness is cold, so the
                // character has both an attack and a debuff available for it.
                MagicSpell(104, "Frost Bolt VII", difficulty: 300),
                Debuff(85, "Cold Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            vitalSettings: null,
            GameInfo);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((104u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(85u, surface.CastSpellIds);
    }

    /// <summary>
    /// With the vulnerability column ticked AND an extra vulnerability
    /// element, both are cast, the attack element first: they are steps 8 and
    /// 9 of the chain, in that order.
    /// Mutation: swap the two chain entries and the second assertion fails;
    /// gate the extra vulnerability away and nothing follows the first.
    /// </summary>
    [Fact]
    public void VulnAndAnExtraVulnerabilityCastTheAttackElementFirst()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
                Debuff(71, "Acid Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Vulnerability,
                DamageType = MonsterDamageType.Fire,
                ExtraVulnerability = MonsterDamageType.Acid,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(70u, surface.LastTargetedCast.Item1);

        // The first debuff is still in flight, so let it finish.
        surface.LastCastCompletion = new PluginCastCompletion(1, 70, 10, 0);
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.Equal(71u, surface.LastTargetedCast.Item1);
    }

    /// <summary>
    /// A Lure is an item enchantment; the vulnerability the column asks for
    /// is the creature spell of the same element. Knowing both must not make
    /// the Lure a candidate.
    /// Mutation: accept a Lure as a vulnerability again and the Lure - the
    /// lower-difficulty spell of the two - wins the choice, which is the cast
    /// the server answered with "You fail to affect".
    /// </summary>
    [Fact]
    public void ALureIsNeverChosenAsACreatureVulnerability()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(80, "Flame Lure III"),
                Debuff(81, "Fire Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((81u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(80u, surface.CastSpellIds);
    }

    /// <summary>
    /// With only Lures known there is no vulnerability to cast at all, so the
    /// pass goes straight to the attack rather than spending itself on a
    /// spell the monster cannot be the target of.
    /// Mutation: accept a Lure as a vulnerability again and the first
    /// assertion fails - the Lure goes out instead of the bolt.
    /// </summary>
    [Fact]
    public void KnowingOnlyLuresCastsNothingAtTheCreature()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(80, "Flame Lure III"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(80u, surface.CastSpellIds);
    }

    // ---- Casts other clients on this computer reported ----

    [Fact]
    public void RejectedOnlyPeerBatchAdvancesCursorWithoutReadingTrustSnapshot()
    {
        var surface = new FakeAutomation();
        surface.Peers.Casts.Add(PeerCast(1, spellId: 70, caster: surface.ObjectId));
        surface.Peers.Casts.Add(PeerCast(2, spellId: 70) with { Landed = false });
        surface.Peers.Casts.Add(PeerCast(3, spellId: 70, secondsRemaining: 0));
        surface.Peers.Casts.Add(PeerCast(4, spellId: 70, secondsRemaining: -1));
        surface.Peers.Casts.Add(PeerCast(5, spellId: 70, secondsRemaining: double.NaN));
        var controller = new CombatController(new FakeHost(surface), FireVulnerabilityRule());
        int tagReads = 0;
        controller.BindCastSharing(static () => true, () => { tagReads++; return "debuffs"; });

        controller.ObserveRemoteCasts();
        controller.ObserveRemoteCasts();

        Assert.Equal([0L, 5L], surface.Peers.CaptureRequests);
        Assert.Equal(0, surface.Peers.ClientCaptureCount);
        Assert.Equal(0, tagReads);
        Assert.Empty(surface.Ledger.Reported);
    }

    [Fact]
    public void MixedPeerBatchTakesOneTrustSnapshotAndNextBatchRechecksTags()
    {
        var surface = new FakeAutomation
        {
            SpellLookup = [Debuff(70, "Fire Vulnerability Other VII")],
        };
        surface.Peers.Clients.Add(NetworkClient(7u, "Healer", "heals"));
        surface.Peers.Clients.Add(NetworkClient(8u, "Debuffer", "Debuffs"));
        surface.Peers.Casts.Add(PeerCast(1, spellId: 70) with { Landed = false });
        surface.Peers.Casts.Add(PeerCast(2, spellId: 70, clientId: 7u));
        surface.Peers.Casts.Add(PeerCast(3, spellId: 70, clientId: 8u));
        surface.Peers.Casts.Add(PeerCast(4, spellId: 70, clientId: 8u));
        var controller = new CombatController(new FakeHost(surface), FireVulnerabilityRule());
        controller.BindCastSharing(static () => true, static () => "debuffs");

        controller.ObserveRemoteCasts();
        Assert.Equal(1, surface.Peers.ClientCaptureCount);
        Assert.Equal(2, surface.Ledger.Reported.Count);
        surface.Peers.Clients.Clear();
        surface.Peers.Casts.Add(PeerCast(5, spellId: 70, clientId: 8u));
        controller.ObserveRemoteCasts();
        controller.ObserveRemoteCasts();
        Assert.Equal(2, surface.Peers.ClientCaptureCount);
        Assert.Equal(2, surface.Ledger.Reported.Count);
        Assert.Equal([0L, 4L, 5L], surface.Peers.CaptureRequests);
    }

    [Fact]
    public void UntaggedPeerBatchReadsTagOnceAndDoesNotCaptureClients()
    {
        var surface = new FakeAutomation
        {
            SpellLookup = [Debuff(70, "Fire Vulnerability Other VII")],
        };
        surface.Peers.Casts.Add(PeerCast(1, spellId: 70));
        surface.Peers.Casts.Add(PeerCast(2, spellId: 70));
        var controller = new CombatController(new FakeHost(surface), FireVulnerabilityRule());
        int tagReads = 0;
        controller.BindCastSharing(static () => true, () => { tagReads++; return null; });

        controller.ObserveRemoteCasts();

        Assert.Equal(1, tagReads);
        Assert.Equal(0, surface.Peers.ClientCaptureCount);
        Assert.Equal(2, surface.Ledger.Reported.Count);
    }

    [Fact]
    public void FailedTrustReadDoesNotConsumeAnEligiblePeerCast()
    {
        var surface = new FakeAutomation
        {
            SpellLookup = [Debuff(70, "Fire Vulnerability Other VII")],
        };
        surface.Peers.Clients.Add(NetworkClient(8u, "Debuffer", "debuffs"));
        surface.Peers.Casts.Add(PeerCast(1, spellId: 70) with { Landed = false });
        surface.Peers.Casts.Add(PeerCast(2, spellId: 70, clientId: 8u));
        surface.Peers.ClientCaptureFailuresRemaining = 1;
        var controller = new CombatController(new FakeHost(surface), FireVulnerabilityRule());
        controller.BindCastSharing(static () => true, static () => "debuffs");

        Assert.Throws<IOException>(controller.ObserveRemoteCasts);
        Assert.Empty(surface.Ledger.Reported);
        controller.ObserveRemoteCasts();
        controller.ObserveRemoteCasts();

        Assert.Single(surface.Ledger.Reported);
        Assert.Equal([0L, 1L, 2L], surface.Peers.CaptureRequests);
        Assert.Equal(2, surface.Peers.ClientCaptureCount);
    }

    /// <summary>
    /// A vulnerability another client landed on the monster is one this
    /// character does not cast again: the debuff chain reads the same table
    /// the remote report lands in, so the pass goes straight to the bolt.
    /// The landed cast is also mirrored into the host's own ledger, so the
    /// host and every other plugin agree on what is on the target.
    /// Mutation: drop the record into the debuff table and the vulnerability
    /// goes out first, exactly as it does with no peer at all.
    /// </summary>
    [Fact]
    public void AVulnerabilityAnotherClientLandedStandsTheDebuffChainDown()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        surface.Peers.Casts.Add(PeerCast(1, spellId: 70, secondsRemaining: 45d));
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());

        controller.Toggle();
        controller.ObserveRemoteCasts();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(70u, surface.CastSpellIds);
        Assert.Equal((10u, 70u, 45d), Assert.Single(surface.Ledger.Reported));
    }

    /// <summary>
    /// The element choice asks whether the monster already carries an
    /// element's vulnerability before it picks a weapon. With the Magma
    /// Golem weakest to cold, the cold blade wins on its own; once another
    /// client has landed the blade vulnerability, the slashing blade of the
    /// same rating wins instead, because the monster is already open to it.
    /// Mutation: leave the remote report out of the table the predicate
    /// reads and the second controller equips the cold blade too.
    /// </summary>
    [Fact]
    public void AVulnerabilityAnotherClientLandedMakesTheElementChoiceSeeItAsAlreadyThere()
    {
        static FakeAutomation Surface() => new()
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Magma Golem", 2, 0)],
            KnownCombatSpells = [Debuff(72, "Blade Vulnerability Other VII")],
            EquipmentItems =
            [
                Equipment(700, "Frost Blade", damageType: 0x0008) with { CrushingBlow = true },
                Equipment(701, "Slashing Blade", damageType: 0x0001) with { CrushingBlow = true },
            ],
        };
        static CombatSettings Settings()
        {
            var settings = new CombatSettings { MaximumRange = 40d };
            settings.CombatItemNames.Add("Frost Blade");
            settings.CombatItemNames.Add("Slashing Blade");
            settings.Rules.Clear();
            settings.Rules.Add(new MonsterRule(
                "DEFAULT",
                new MonsterRuleActions
                {
                    Flags = MonsterActionFlags.Attack,
                    DamageType = MonsterDamageType.Auto,
                }));
            return settings;
        }

        FakeAutomation alone = Surface();
        var withoutPeer = new CombatController(
            new FakeHost(alone), Settings(), vitalSettings: null, GameInfo);
        withoutPeer.Toggle();
        for (int tick = 0; tick < 4; tick++)
            withoutPeer.OnTick(0.25);
        Assert.Equal(700u, alone.LastEquipObjectId);

        FakeAutomation helped = Surface();
        helped.Peers.Casts.Add(PeerCast(1, spellId: 72, secondsRemaining: 45d));
        var withPeer = new CombatController(
            new FakeHost(helped), Settings(), vitalSettings: null, GameInfo);
        withPeer.Toggle();
        withPeer.ObserveRemoteCasts();
        for (int tick = 0; tick < 4; tick++)
            withPeer.OnTick(0.25);
        Assert.Equal(701u, helped.LastEquipObjectId);
    }

    /// <summary>
    /// A cast that names this character as the caster is this character's
    /// own, whatever the host says: it is neither recorded nor mirrored, and
    /// the pass casts as if no peer had spoken. The cursor still moves past
    /// it, so it is not looked at again.
    /// Mutation: drop the own-id test and the vulnerability is skipped.
    /// </summary>
    [Fact]
    public void ACastFromThisCharacterIsIgnored()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        surface.Peers.Casts.Add(PeerCast(1, spellId: 70, caster: surface.ObjectId));
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());

        controller.Toggle();
        controller.ObserveRemoteCasts();
        controller.ObserveRemoteCasts();
        controller.OnTick(0.25);

        Assert.Equal((70u, 10u), surface.LastTargetedCast);
        Assert.Empty(surface.Ledger.Reported);
        Assert.Equal([0L, 1L], surface.Peers.CaptureRequests);
    }

    /// <summary>
    /// A spell is classified in this client's own catalogue, never taken
    /// from the wire. One that classifies as none of the tracked debuffs is
    /// dropped from the debuff table: a peer's Strength on the monster says
    /// nothing about its vulnerability, and the chain casts as before. The
    /// landed effect still reaches the host's ledger, where being a debuff is
    /// not the question. A spell this client's table does not know at all
    /// reaches nothing.
    /// Mutation: skip the classifier and record every landed spell, and the
    /// vulnerability step reads the Strength record as the vulnerability.
    /// </summary>
    [Fact]
    public void ARemoteSpellThatIsNotATrackedDebuffIsDroppedFromTheDebuffTable()
    {
        PluginSpellInfo strength = Debuff(200, "Strength Other VII") with
        {
            IsDebuff = false,
            IsOffensive = false,
            IsBeneficial = true,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
            ],
            SpellLookup = [strength],
            EquipmentItems = [WieldedCaster()],
        };
        surface.Peers.Casts.Add(PeerCast(1, spellId: 200, secondsRemaining: 60d));
        surface.Peers.Casts.Add(PeerCast(2, spellId: 9999, secondsRemaining: 60d));
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());

        controller.Toggle();
        controller.ObserveRemoteCasts();
        controller.OnTick(0.25);

        Assert.Equal((70u, 10u), surface.LastTargetedCast);
        Assert.Equal((10u, 200u, 60d), Assert.Single(surface.Ledger.Reported));
    }

    /// <summary>
    /// Reads never consume, so the cursor is the only thing between a cast
    /// and being applied twice. Each look asks for what is above the highest
    /// sequence already dealt with, and a cast is mirrored and recorded once
    /// however many frames look at it.
    /// Mutation: stop moving the cursor and the ledger gets the same cast on
    /// every look.
    /// </summary>
    [Fact]
    public void TheCursorAdvancesAndACastIsNeverAppliedTwice()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
                Debuff(71, "Acid Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        surface.Peers.Casts.Add(PeerCast(3, spellId: 70));
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());
        var lines = new List<string>();
        controller.Log = (_, text) => lines.Add(text);

        controller.Toggle();
        controller.ObserveRemoteCasts();
        controller.ObserveRemoteCasts();
        controller.ObserveRemoteCasts();
        Assert.Equal([0L, 3L, 3L], surface.Peers.CaptureRequests);
        Assert.Single(surface.Ledger.Reported);
        Assert.Single(lines, static line => line.StartsWith("Peer cast:", StringComparison.Ordinal));

        surface.Peers.Casts.Add(PeerCast(5, spellId: 71));
        controller.ObserveRemoteCasts();
        Assert.Equal([0L, 3L, 3L, 3L], surface.Peers.CaptureRequests);
        Assert.Equal(2, surface.Ledger.Reported.Count);
        controller.ObserveRemoteCasts();
        Assert.Equal(5L, surface.Peers.CaptureRequests[^1]);
        Assert.Equal(2, surface.Ledger.Reported.Count);
    }

    /// <summary>
    /// With a tag chosen, only casts from peers whose published record
    /// carries that tag are taken in; a peer tagged otherwise, or whose
    /// record has already gone stale, is ignored. The tag is compared the
    /// way the expressions compare it, without regard to case.
    /// Mutation: ignore the tag and the ledger gets both casts.
    /// </summary>
    [Fact]
    public void RemoteCastsAreTakenOnlyFromPeersCarryingTheChosenTag()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        surface.Peers.Clients.Add(NetworkClient(7u, "Healer", "heals"));
        surface.Peers.Clients.Add(NetworkClient(8u, "Debuffer", "Debuffs", "mules"));
        surface.Peers.Casts.Add(PeerCast(1, spellId: 70, clientId: 7u, secondsRemaining: 20d));
        surface.Peers.Casts.Add(PeerCast(2, spellId: 70, clientId: 8u, secondsRemaining: 30d));
        surface.Peers.Casts.Add(PeerCast(3, spellId: 70, clientId: 9u, secondsRemaining: 40d));
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());
        controller.BindCastSharing(static () => true, static () => "debuffs");

        controller.Toggle();
        controller.ObserveRemoteCasts();

        Assert.Equal((10u, 70u, 30d), Assert.Single(surface.Ledger.Reported));
    }

    /// <summary>
    /// Sharing off means nothing is read from the other clients at all, and
    /// the pass behaves exactly as it does with no peers.
    /// </summary>
    [Fact]
    public void WithSharingOffNothingIsTakenIn()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        surface.Peers.Casts.Add(PeerCast(1, spellId: 70));
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());
        controller.BindCastSharing(static () => false, static () => null);

        controller.Toggle();
        controller.ObserveRemoteCasts();
        controller.OnTick(0.25);

        Assert.Empty(surface.Peers.CaptureRequests);
        Assert.Empty(surface.Ledger.Reported);
        Assert.Equal((70u, 10u), surface.LastTargetedCast);
    }

    /// <summary>
    /// While the macro is off, a peer's cast is still mirrored into the
    /// host's ledger, but the debuff table is not written: it is cleared
    /// whenever the macro stops and the controller's clock stands still
    /// meanwhile, so a record made then would be stamped against a frozen
    /// clock. Starting the macro takes the cursor back to the beginning, so
    /// every effect still in force is taken in fresh rather than lost to a
    /// look that happened before there was a table to write.
    /// Mutation: leave the cursor where it was on start and the vulnerability
    /// landed before the macro started goes out again.
    /// </summary>
    [Fact]
    public void ACastSeenBeforeTheMacroStartedIsTakenInWhenItStarts()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        surface.Peers.Casts.Add(PeerCast(1, spellId: 70, secondsRemaining: 45d));
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());
        var lines = new List<string>();
        controller.Log = (_, text) => lines.Add(text);

        controller.ObserveRemoteCasts();
        Assert.Equal([0L], surface.Peers.CaptureRequests);
        Assert.Single(surface.Ledger.Reported);
        Assert.DoesNotContain(lines, static line => line.StartsWith("Peer cast:", StringComparison.Ordinal));

        controller.Toggle();
        controller.ObserveRemoteCasts();
        controller.OnTick(0.25);

        Assert.Equal([0L, 0L], surface.Peers.CaptureRequests);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(70u, surface.CastSpellIds);
    }

    /// <summary>
    /// A learned debuff tells the other clients twice: once when it goes
    /// out, with the skill it is cast with, so a second character can decide
    /// not to start the same spell; and once when the server says it landed,
    /// with the spell's whole duration, so that character can stand down. A
    /// cast that failed lands nothing and says nothing.
    /// Mutation: announce on the send alone and the success list stays
    /// empty; announce on any completion and the failed cast is announced.
    /// </summary>
    [Fact]
    public void ALearnedDebuffAnnouncesItsAttemptWithTheSkillAndItsSuccessWithTheDuration()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
            CharacterSkills =
            [
                new PluginSkillInfo(31, "Creature Enchantment", PluginSkillTraining.Specialized, 320),
            ],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((70u, 10u), surface.LastTargetedCast);
        Assert.Equal((10u, 70u, 320), Assert.Single(surface.Peers.Attempts));
        Assert.Empty(surface.Peers.Successes);

        // The first try fails: nothing landed, so nothing is announced.
        surface.LastCastCompletion = new PluginCastCompletion(1, 70, 10, 0x0402);
        controller.OnTick(0.25);
        Assert.Empty(surface.Peers.Successes);

        // The second try goes out and lands.
        controller.OnTick(0.25);
        Assert.Equal(2, surface.Peers.Attempts.Count);
        surface.LastCastCompletion = new PluginCastCompletion(2, 70, 10, 0);
        controller.OnTick(0.25);
        Assert.Equal((10u, 70u, 320, 60d), Assert.Single(surface.Peers.Successes));
    }

    /// <summary>
    /// The server's answer to a learned debuff is read on the host frame as
    /// well as on the pass, and the success is announced from whichever reads
    /// it first, once: the receipt is counted by revision.
    /// Mutation: announce from the frame reader alone and the pass-only
    /// path above fails; announce without the revision guard and this
    /// announces twice.
    /// </summary>
    [Fact]
    public void ASuccessReadOnTheFrameIsAnnouncedOnce()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((10u, 70u, 0), Assert.Single(surface.Peers.Attempts));

        surface.LastCastCompletion = new PluginCastCompletion(1, 70, 10, 0);
        controller.ObserveLearnedDebuffReceipt(0.1);
        controller.OnTick(0.25);
        controller.ObserveLearnedDebuffReceipt(0.1);

        Assert.Equal((10u, 70u, 0, 60d), Assert.Single(surface.Peers.Successes));
    }

    /// <summary>
    /// A wand's debuff is announced as an attempt when the wand is used and
    /// as a success when the wand's own confirmation line is matched, which
    /// is the moment the table records it.
    /// </summary>
    [Fact]
    public void AWandDebuffAnnouncesItsAttemptAndItsConfirmedSuccess()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        PluginInventoryItem lens = InventoryItem(
            800, "Imperil Lens", 0x8000, 90, equipped: true) with
        {
            ItemSpellcraft = 400,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            SpellLookup = [imperil],
            ItemEntries = [lens],
            CharacterSkills =
            [
                new PluginSkillInfo(31, "Creature Enchantment", PluginSkillTraining.Trained, 280),
            ],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.CombatItemObjectIds.Add(lens.ObjectId);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((800u, 10u), surface.LastAppliedItem);
        Assert.Equal((10u, 90u, 280), Assert.Single(surface.Peers.Attempts));
        Assert.Empty(surface.Peers.Successes);

        surface.LastItemCompletion = new PluginItemUseCompletion(1, 800, 10, 0);
        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty,
                "You cast Imperil Other VII on Drudge.",
                string.Empty)
            {
                LogTextType = 0x07,
            },
        ];
        controller.OnTick(0.25);

        Assert.Equal((10u, 90u, 280, 60d), Assert.Single(surface.Peers.Successes));
    }

    /// <summary>
    /// Sharing off means nothing is told to the other clients either.
    /// </summary>
    [Fact]
    public void WithSharingOffNothingIsAnnounced()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(70, "Fire Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var controller = new CombatController(
            new FakeHost(surface),
            FireVulnerabilityRule());
        controller.BindCastSharing(static () => false, static () => null);

        controller.Toggle();
        controller.OnTick(0.25);
        surface.LastCastCompletion = new PluginCastCompletion(1, 70, 10, 0);
        controller.OnTick(0.25);

        Assert.Contains(70u, surface.CastSpellIds);
        Assert.Empty(surface.Peers.Attempts);
        Assert.Empty(surface.Peers.Successes);
    }

    private static PluginNetworkClient NetworkClient(
        uint clientId,
        string name,
        params string[] tags) => new(
            clientId,
            0x50000000u + clientId,
            name,
            "Coldeve",
            default,
            tags,
            100u, 100u, 100u, 100u, 100u, 100u,
            0f);

    /// <summary>Attack with fire, and tick the vulnerability column.</summary>
    private static CombatSettings FireVulnerabilityRule()
    {
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Vulnerability,
                DamageType = MonsterDamageType.Fire,
            }));
        return settings;
    }

    [Fact]
    public void AFistsRowCastsTuskerFistsBeforeTheEnchantmentIsUp()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(0x0B76u, "Tusker Fists", difficulty: 300),
                MagicSpell(105, "Shock Wave VII", difficulty: 300),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fists,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((0x0B76u, 10u), surface.LastTargetedCast);
    }

    [Fact]
    public void AFistsRowWithoutTuskerFistsAttacksWithBludgeon()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(105, "Shock Wave VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fists,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((105u, 10u), surface.LastTargetedCast);
    }

    private static PluginSpellInfo Debuff(uint id, string name) => new(
        id,
        name,
        Family: id,
        Tier: 7,
        Difficulty: 250,
        ManaCost: 30,
        DurationSeconds: 60,
        School: 31,
        Description: string.Empty,
        IsSelfTargeted: false,
        IsBeneficial: false)
    {
        IsDebuff = true,
        IsOffensive = true,
        TargetMask = 0x10,
        // Real spells carry a reach; a fixture with none would be refused by
        // the debuff range gate before anything else could be observed.
        BaseRangeConstant = 80f,
        Saying = "testwords",
    };

    [Fact]
    public void UnknownMonsterFallsToAuthenticUnlistedElementAndCastsNoExtraVuln()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "New Server Creature", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Force Bolt VII", difficulty: 300),
                Debuff(85, "Piercing Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
                // Ex. Vuln = Auto resolves through the damage table, which has
                // nothing for this monster, so step 9 never runs.
                ExtraVulnerability = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        // Pierce is eDamageElement 0, the first entry of the element walk.
        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        Assert.DoesNotContain(85u, surface.CastSpellIds);
        Assert.Contains(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Using unlisted damage type: Pierce",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A monster the character cannot attack is given up on with the reason
    /// said out loud: which spell was turned down and why, in chat once per
    /// monster and in the log on every pass.
    /// </summary>
    [Fact]
    public void AMonsterGivenUpForWantOfASpellIsToldWhy()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0), Target(11, "Golem", 6, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                MagicSpell(27, "Flame Bolt I", difficulty: 1),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        surface.MissingComponentSpellIds.Add(100u);
        surface.MissingComponentSpellIds.Add(27u);
        var settings = FireAttackRule(new CombatSettings { MaximumRange = 40d });
        var controller = new CombatController(new FakeHost(surface), settings);
        var lines = new List<string>();
        controller.Log = (_, text) => lines.Add(text);

        controller.Toggle();
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        const string why =
            "No usable attack spell (Flame Bolt VII and 1 lower: missing components)";
        Assert.Equal(
            ["[MossTank] Not attacking Drudge: " + why, "[MossTank] Not attacking Golem: " + why],
            surface.PostedSystemMessages
                .Where(static message => message.Contains("Not attacking", StringComparison.Ordinal))
                .ToArray());
        Assert.Contains(
            lines,
            line => line == $"Attack: Drudge yielded nothing this pass ({why}), choosing again");
    }

    /// <summary>
    /// Every rule shares one cast tracker, so a buff cast at the character's
    /// own guid reaches the combat controller's result-timeout arm like any
    /// other. Giving a target up is a verdict about a creature the pass
    /// follows: the caster is not one, so nothing is suppressed and nothing
    /// is announced. A monster beside it still gets both.
    /// Mutation: drop the creature guard from
    /// <c>CombatFailureTracker.RecordMiss</c> and the first assertion fails
    /// with "Cannot hit ??? (1342177290) — blacklisted for 120
    /// seconds." — the exact line the live run printed.
    /// </summary>
    [Fact]
    public void ASelfCastThatTimesOutIsNotAnnouncedAsAnUnhittableTarget()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
            BlacklistMonsterAttemptCount = 0,
            BlacklistMonsterTimeoutSeconds = 120,
        });
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);

        const uint self = 1342177290u;
        TimeOutACast(controller, self, 0x1131u, "Incantation of Flame Bane", "MalarQuaTak");
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Cannot hit",
                StringComparison.Ordinal));

        TimeOutACast(controller, 10u, 100u, "Flame Bolt VII", "ZojakQuazael");
        Assert.Contains(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Cannot hit Drudge (10) — blacklisted for",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Drives one cast all the way to the result timeout: begin it, let the
    /// gesture echo move it on to waiting for a result, then spend the whole
    /// result budget.
    /// </summary>
    private static void TimeOutACast(
        CombatController controller,
        uint targetObjectId,
        uint spellId,
        string spellName,
        string saying)
    {
        controller.CastTracker.Reset();
        controller.CastTracker.Begin(
            spellId,
            spellName,
            targetObjectId,
            string.Empty,
            hitsMultipleTargets: false,
            saying);
        controller.CastTracker.ObserveChat(
            0uL,
            saying,
            ownSpeech: true,
            logTextType: 0x11u);
        controller.CastTracker.Advance(SpellCastTracker.ResultTimeoutSeconds + 0.1d);
    }

    [Fact]
    public void PermanentFailResultTextForceBlacklistsTheTarget()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
            BlacklistMonsterTimeoutSeconds = 300,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);

        controller.CastTracker.ObserveChat(0uL,
            surface.KnownCombatSpells[0].Saying,
            ownSpeech: true, logTextType: 0x11u);
        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty, "Drudge is an invalid target.", string.Empty)
            {
                LogTextType = 0x07,
            },
        ];
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.Contains(
            "Waiting for a target",
            controller.Status,
            StringComparison.Ordinal);
        Assert.False(controller.HasTarget);
    }

    [Fact]
    public void PermanentFailIsIgnoredWhileAMultiTargetSpellIsInFlight()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300) with
                {
                    Family = 638u,
                },
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
            BlacklistMonsterTimeoutSeconds = 300,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);

        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty, "Drudge is an invalid target.", string.Empty)
            {
                LogTextType = 0x07,
            },
        ];
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.True(controller.HasTarget);
    }

    [Fact]
    public void AKillLineEndsTheTargetBeforeTheWorldRemovesIt()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        int castsBeforeTheKill = surface.CastSpellIds.Count;

        controller.CastTracker.ObserveChat(0uL,
            surface.KnownCombatSpells[0].Saying,
            ownSpeech: true, logTextType: 0x11u);
        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty, "You killed Drudge!", string.Empty),
        ];
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.False(controller.HasTarget);
        Assert.Equal(castsBeforeTheKill, surface.CastSpellIds.Count);
        Assert.Contains(
            "Waiting for a target",
            controller.Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultWaitTimeoutBumpsTheBlacklistAttemptCount()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
            // No attempts allowed, so the first one that records trips.
            BlacklistMonsterAttemptCount = 0,
            BlacklistMonsterTimeoutSeconds = 300,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);

        controller.CastTracker.ObserveChat(0uL,
            surface.KnownCombatSpells[0].Saying,
            ownSpeech: true, logTextType: 0x11u);
        Assert.True(controller.HasTarget);

        // 4 x 907 ms of the result timer.
        controller.CastTracker.Advance(3.7d);
        controller.OnTick(0.25);

        Assert.False(controller.HasTarget);
    }

    [Fact]
    public void AServerRefusedCastDoesNotCountAgainstTheTarget()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            ScanIntervalSeconds = 0.05d,
            BlacklistMonsterAttemptCount = 1,
            BlacklistMonsterTimeoutSeconds = 300,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal((100u, 10u), surface.LastTargetedCast);

        surface.LastCastCompletion = new PluginCastCompletion(1, 100u, 10u, 0x1Du);
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.True(controller.HasTarget);
    }

    [Fact]
    public void ATwentyCandidateScanDoesNotRebuildThePlanPerCandidate()
    {
        var targets = new List<PluginCombatTarget>();
        for (uint i = 0; i < 20u; i++)
            targets.Add(Target(10u + i, "Drudge", distance: 3f + i, angle: 0));
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = targets,
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                Debuff(80, "Fester Other VII"),
                Debuff(81, "Broadside of a Barn"),
                Debuff(82, "Fire Vulnerability Other VII"),
                Debuff(83, "Imperil Other VII"),
                Debuff(84, "Magic Yield Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Yield
                    | MonsterActionFlags.Imperil
                    | MonsterActionFlags.Vulnerability
                    | MonsterActionFlags.Broadside
                    | MonsterActionFlags.Fester,
                DamageType = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        // Measured: 2 and 145 with the memos, 169 and 211 without.
        Assert.InRange(surface.CaptureOwnedEquipmentCount, 1, 20);
        Assert.InRange(surface.KnownCombatSpellReads, 1, 180);
    }

    /// <summary>
    /// A pass that has to choose again — every monster in range unreachable —
    /// re-runs the whole selection up to five hundred times. The host builds
    /// the equipment and inventory projections by walking every object it
    /// knows and sorting the result, so they are read once for the pass, not
    /// once per attempt.
    /// Mutation: read the host directly in <c>RefreshTarget</c> (or in
    /// <c>TryPrepareAttack</c>) instead of the pass memo and the counts run
    /// into the dozens.
    /// </summary>
    [Fact]
    public void APassThatChoosesAgainStillCapturesTheCharacterOnlyOnce()
    {
        var targets = new List<PluginCombatTarget>();
        for (uint i = 0; i < 8u; i++)
            targets.Add(Target(10u + i, "Drudge", distance: 3f + i, angle: 0));
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = targets,
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            EquipmentItems = [WieldedCaster()],
            // Nothing can be reached, so every attempt turns a column off and
            // the pass chooses again until it runs out of monsters.
            ProjectilePath = new(PluginProjectilePathStatus.Blocked),
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.True(
            surface.ProjectilePathChecks >= 2,
            "the pass never had to choose again, so nothing is being measured");
        Assert.InRange(surface.CaptureOwnedEquipmentCount, 1, 2);
        Assert.InRange(surface.CaptureOwnedItemsCount, 1, 2);
    }

    /// <summary>
    /// A kill or a success starts the attempt count over; it does NOT lift a
    /// blacklist that is still running. Mutation: make <c>ResetAttempts</c>
    /// clear the deadline too and the second assertion fails.
    /// </summary>
    [Fact]
    public void KillAndSuccessResultTextResetTheAttemptCountOnly()
    {
        var settings = new CombatSettings { BlacklistMonsterTimeoutSeconds = 300 };
        var tracker = new CombatFailureTracker();

        tracker.ObserveTargets([Target(10, "Drudge", 5, 0)], 0d, settings);
        tracker.ForceBlacklist(10u, now: 0d, settings);
        Assert.Equal(
            CombatSuppressionReason.Blacklisted,
            tracker.Reason(10u, now: 1d));

        tracker.ResetAttempts(10u);
        Assert.Equal(
            CombatSuppressionReason.Blacklisted,
            tracker.Reason(10u, now: 1d));
    }

    [Fact]
    public void RingOnlyRuleCastsUntargetedRingWithOneNearbyRingTarget()
    {
        PluginSpellInfo ring = Spell(110, "Cassius' Ring of Fire") with
        {
            IsOffensive = true,
            TargetMask = 0,
            Description = "Shoots waves of fire outward from the caster.",
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 3, 0)],
            KnownCombatSpells = [ring],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Ring,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(110u, surface.LastUntargetedCast);
        Assert.Equal(default, surface.LastTargetedCast);
    }

    /// <summary>
    /// Two wands of the same name, neither of them named by the rule, both on
    /// the Items page: the automatic pick has to name the same one on the pass
    /// after it is wielded. The list the host hands out puts what is held
    /// first, so the pick has to come from the page's order rather than that
    /// one, or the two wands take turns and the character swaps for ever
    /// without ever attacking.
    ///
    /// Mutation: hand <c>VtankWeaponLadder.Select</c> the projection itself
    /// instead of <c>InProfileOrder</c> and this fails with two equips.
    /// </summary>
    [Fact]
    public void TwoWandsOfOneNameDoNotTakeTurnsBeingWielded()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(
                    0x80000A4Cu,
                    "Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
                Equipment(
                    0x80000B34u,
                    "Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Wand");
        settings.CombatItemOrder.Add("Wand");
        settings.CombatItemOrderIds.Add(0x80000A4Cu);
        settings.CombatItemOrderIds.Add(0x80000B34u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Auto,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        string[] first = surface.CallLog
            .Where(static entry => entry.StartsWith("Equip:", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(first);

        controller.OnTick(0.25);
        controller.OnTick(0.25);

        string[] every = surface.CallLog
            .Where(static entry => entry.StartsWith("Equip:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(first, every);
    }

    [Fact]
    public void ExplicitMonsterWeaponUsesCanonicalEquipmentCommandBeforeAttack()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(700, "Fire Sword", damageType: 0x10),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(700u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                WeaponObjectId = 700,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(700u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void ExplicitMonsterWeaponResolvesDurableNameAfterRelogChangesObjectId()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(900, "Fire Sword", damageType: 0x10),
            ],
        };
        var settings = new CombatSettings();
        // An older profile lists its items by name alone, which is exactly
        // what still matches the same sword under a new object id.
        settings.CombatItemNames.Add("Fire Sword");
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                WeaponObjectId = 700,
                WeaponName = "Fire Sword",
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(900u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
    }

    /// <summary>
    /// The Items page is the whole roster the fight may reach for. A rule
    /// carrying a weapon the page does not list - a pick made before the item
    /// was removed, or written by an editor that offered more than the page -
    /// is not wielded: the pick is dropped and the automatic choice among
    /// listed items answers instead. The fight says so once, not per pass.
    ///
    /// Mutation: resolve the rule's weapon against owned equipment without
    /// asking whether it is listed, and the unlisted blade is wielded.
    /// </summary>
    [Fact]
    public void AnUnlistedRuleWeaponIsNeverWieldedAndTheAutomaticChoiceAnswers()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(700, "Unlisted Blade", damageType: 0x10),
                Equipment(701, "Listed Sword", damageType: 0x10),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(701u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponObjectId = 700u,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 8; tick++)
            controller.OnTick(0.25);

        Assert.DoesNotContain("Equip:000002BC", surface.CallLog);   // 700
        Assert.Equal(701u, surface.LastEquipObjectId);
        Assert.Single(
            surface.PostedSystemMessages,
            line => line.Contains(
                "not in the Items list", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same rule with the same weapon, once the Items page carries it:
    /// the named pick is honoured, and nothing is said about it.
    /// </summary>
    [Fact]
    public void AListedRuleWeaponIsStillWieldedByName()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(700, "Unlisted Blade", damageType: 0x10),
                Equipment(701, "Listed Sword", damageType: 0x10),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(700u);
        settings.CombatItemObjectIds.Add(701u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponObjectId = 700u,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 8; tick++)
            controller.OnTick(0.25);

        Assert.Equal(700u, surface.LastEquipObjectId);
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            line => line.Contains(
                "not in the Items list", StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticDamageSelectionChoosesStrongestMatchingWeapon()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(700, "Weak fire", damageType: 0x10, damage: 20),
                Equipment(701, "Strong fire", damageType: 0x10, damage: 35),
                Equipment(702, "Acid", damageType: 0x20, damage: 99),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Strong fire");
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(701u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void AttackRoutesTheCombatModeThroughTheSharedGateNotEnterDefaultMode()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "War Wand",
                    damageType: 0,
                    equippedLocation: 0x00100000u,
                    itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        // The wand is already wielded; only the mode is wrong. The gate
        // recomputes the mode the wielded item implies and asks for it.
        Assert.Contains("EnterMode:Magic", surface.CallLog);
        Assert.Equal(PluginCombatMode.Magic, surface.CombatSnapshot.Mode);
    }

    [Fact]
    public void AttackWithNothingWieldedAndAnEmptyItemsProfileStopsWithTheWandNotice()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems = [],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(
            "[MossTank] " + CombatModeGate.NoWandNotice,
            Assert.Single(
                surface.PostedSystemMessages,
                message => message.Contains(
                    CombatModeGate.NoWandNotice,
                    StringComparison.Ordinal)));
        Assert.False(controller.Enabled);
        Assert.DoesNotContain("EnterDefaultMode", surface.CallLog);
        Assert.DoesNotContain("EnterMode:Melee", surface.CallLog);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void AttackWithNothingWieldedWieldsTheProfiledWandAndRequestsMagic()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "War Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("War Wand");
        settings.CombatItemOrderIds.Add(700u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        int equip = surface.CallLog.IndexOf("Equip:000002BC");
        int magic = surface.CallLog.IndexOf("EnterMode:Magic");
        Assert.True(equip >= 0, "the profiled wand was never wielded");
        Assert.True(magic > equip, "Magic was requested before the wand: "
            + string.Join(" | ", surface.CallLog));
        Assert.True(controller.Enabled);
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains(
                CombatModeGate.NoWandNotice,
                StringComparison.Ordinal));
        Assert.DoesNotContain("EnterDefaultMode", surface.CallLog);
    }

    [Fact]
    public void AttackWithAWieldedSwordButNoProfiledWeaponSaysWhatIsMissing()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems =
            [
                // Wielded, but in NO profile — SelectAutomaticWeapon skips
                // every unprofiled item, so the plan is 0.
                Equipment(
                    900,
                    "Unprofiled Sword",
                    damageType: 0x0001,
                    equippedLocation: 0x00100000u),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        // The character is holding a weapon the profile does not list and
        // the list offers nothing to put in its place. It is told what is
        // missing, once, and the attack steps aside; the weapon in its hand
        // is left alone rather than swapped out for a wand it cannot throw
        // anything with.
        Assert.Single(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Add the weapon you fight with to the Items list.",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains(
                CombatModeGate.NoWandNotice,
                StringComparison.Ordinal));
        Assert.True(controller.Enabled);
        Assert.DoesNotContain("EnterDefaultMode", surface.CallLog);
        Assert.DoesNotContain("EnterMode:Magic", surface.CallLog);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void AttackWithAProfiledMeleeWeaponTakesThePhysicalArmThroughTheGate()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems = [Equipment(900, "Fire Sword", damageType: 0x10)],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Fire Sword");
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 6; tick++)
            controller.OnTick(0.25);

        int equip = surface.CallLog.IndexOf("Equip:00000384");
        int melee = surface.CallLog.IndexOf("EnterMode:Melee");
        Assert.True(equip >= 0, "the planned weapon was never wielded");
        Assert.True(melee > equip, "Melee was requested before the weapon: "
            + string.Join(" | ", surface.CallLog));
        Assert.True(controller.Enabled);
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains(
                CombatModeGate.NoWandNotice,
                StringComparison.Ordinal));
        Assert.DoesNotContain("EnterDefaultMode", surface.CallLog);
    }

    /// <summary>
    /// Mutation executed: remove the <c>ClearPassMemos()</c> call at the
    /// start of <c>EquipOneStepForMonster</c>; the second command returns
    /// ready without issuing the arrow wield.
    /// </summary>
    [Fact]
    public void ManualEquipmentCommandReadsAmmunitionAddedBetweenCalls()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            CharacterSkills = [new PluginSkillInfo(47u, "Missile Weapons",
                PluginSkillTraining.Trained, 300u) { Base = 300u }],
            EquipmentItems =
            [
                Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                    equippedLocation: 0x00100000u, ammoType: 1u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(700u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
            WeaponObjectId = 700u,
        }));
        var controller = new CombatController(new FakeHost(surface), settings,
            gameInfo: AmmoGameInfo);

        Assert.False(controller.EquipOneStepForMonster("Fixture"));
        Assert.Equal(0u, surface.LastEquipObjectId);

        surface.EquipmentItems =
        [
            Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                equippedLocation: 0x00100000u, ammoType: 1u),
            Equipment(801, "Deadly Fire Arrow", 0x10, combatUse: 3,
                ammoType: 1u, stackSize: 20, validLocations: AmmunitionSlot),
        ];
        surface.ItemEntries =
        [
            InventoryItem(801, "Deadly Fire Arrow", 0x100u, 0u, false)
                with { StackSize = 20 },
        ];

        Assert.False(controller.EquipOneStepForMonster("Fixture"));
        Assert.Equal(801u, surface.LastEquipObjectId);
    }
    private (FakeAutomation Surface, CombatController Controller)
        BlockedBowFixture()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            CharacterSkills = [new PluginSkillInfo(47u, "Missile Weapons",
                PluginSkillTraining.Trained, 300u) { Base = 300u }],
            Targets = [Target(10, "Drudge", 5, 0)],
            ProjectilePath = new(PluginProjectilePathStatus.Blocked),
            EquipmentItems =
            [
                Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                    equippedLocation: 0x00100000u, ammoType: 1u),
                Equipment(801, "Deadly Fire Arrow", 0x10, combatUse: 3,
                    ammoType: 1u, stackSize: 20, validLocations: AmmunitionSlot),
            ],
            ItemEntries =
            [
                InventoryItem(801, "Deadly Fire Arrow", 0x100u, 0u, false)
                    with { StackSize = 20 },
            ],
        };
        var settings = new CombatSettings { UseProjectileAwareness = true };
        settings.CombatItemObjectIds.Add(700u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
            WeaponObjectId = 700u,
        }));
        return (surface, new CombatController(new FakeHost(surface), settings,
            gameInfo: AmmoGameInfo));
    }

    /// <summary>
    /// A monster behind a wall must not hold the turn while mode prep is
    /// still owed: the path is tested against the planned bow before the
    /// stance change is requested. Mutation: remove the early path check
    /// before <c>TryPrepareAttack</c>; the pass asks for Missile mode.
    /// </summary>
    [Fact]
    public void BlockedShotYieldsBeforeModePrepClaimsTheTurn()
    {
        var (surface, controller) = BlockedBowFixture();

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(0, surface.ModeChangeRequests);
        // The yield re-picks and finds nothing, exactly as the in-mode path
        // does; the status is the pass's, not mode prep's or the busy gate's.
        Assert.Equal("Waiting for a target", controller.Status);
        Assert.False(controller.HasTarget);
        Assert.Equal(10u, surface.LastProjectileTarget);
    }

    /// <summary>
    /// Same, with the inventory busy: a busy gate must not keep the
    /// unshootable monster holding the turn. Mutation: as above.
    /// </summary>
    [Fact]
    public void BlockedShotYieldsBeforeABusyInventoryClaimsTheTurn()
    {
        var (surface, controller) = BlockedBowFixture();
        surface.CombatSnapshot = Physical() with { Mode = PluginCombatMode.Missile };
        surface.BusyState = surface.BusyState with { PendingInventory = true };

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(0, surface.ModeChangeRequests);
        // The yield re-picks and finds nothing, exactly as the in-mode path
        // does; the status is the pass's, not mode prep's or the busy gate's.
        Assert.Equal("Waiting for a target", controller.Status);
        Assert.False(controller.HasTarget);
    }

    /// <summary>
    /// Mutation: treat any equipped combat-use-3 item as the quiver or
    /// select the first matching stack; item 801 then hides the larger stack.
    /// </summary>
    [Fact]
    public void AmmunitionUsesExactQuiverSlotAndLargestMatchingStack()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            CharacterSkills = [new PluginSkillInfo(47u, "Missile Weapons",
                PluginSkillTraining.Trained, 300u) { Base = 300u }],
            EquipmentItems =
            [
                Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                    equippedLocation: 0x00100000u, ammoType: 1u),
                Equipment(801, "Deadly Fire Arrow", 0x10, combatUse: 3,
                    equippedLocation: 0x00100000u, stackSize: 5,
                    validLocations: AmmunitionSlot),
                Equipment(802, "Deadly Fire Arrow", 0x10, combatUse: 3,
                    stackSize: 10, validLocations: AmmunitionSlot),
            ],
            ItemEntries =
            [
                InventoryItem(801, "Deadly Fire Arrow", 0x100u, 0u, false),
                InventoryItem(802, "Deadly Fire Arrow", 0x100u, 0u, false),
            ],
        };
        CombatModeGate gate = BoundAmmunitionGate(surface);

        Assert.True(gate.AmmunitionStale!(700u, MonsterDamageType.Fire));
        Assert.True(gate.WieldAmmunition!(MonsterDamageType.Fire));
        Assert.Equal(802u, surface.LastEquipObjectId);
    }

    /// <summary>
    /// Mutation: compare names without case; the wrong-case row becomes
    /// available.
    /// </summary>
    [Fact]
    public void AmmunitionAvailabilityRequiresExactCase() =>
        Assert.False(IsAmmunitionSelectionPending("deadly fire arrow", 1));

    /// <summary>
    /// Mutation: clamp an explicit zero stack to one; an empty row becomes
    /// available.
    /// </summary>
    [Fact]
    public void AmmunitionAvailabilityRequiresPositiveCount() =>
        Assert.False(IsAmmunitionSelectionPending("Deadly Fire Arrow", 0));

    private static bool IsAmmunitionSelectionPending(string name, int count)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            CharacterSkills = [new PluginSkillInfo(47u, "Missile Weapons",
                PluginSkillTraining.Trained, 300u) { Base = 300u }],
            EquipmentItems =
            [
                Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                    equippedLocation: 0x00100000u, ammoType: 1u),
            ],
            ItemEntries = [InventoryItem(801, name, 0x100u, 0u, false)
                with { StackSize = count }],
        };
        CombatModeGate gate = BoundAmmunitionGate(surface);

        return gate.AmmunitionStale!(700u, MonsterDamageType.Fire);
    }

    /// <summary>
    /// Mutation: recalculate the application from _plannedWeapon;
    /// the callback loses the evaluated bow's arrow selection.
    /// </summary>
    [Fact]
    public void AmmunitionApplicationUsesTheEvaluatedPrimary()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            CharacterSkills = [new PluginSkillInfo(47u, "Missile Weapons",
                PluginSkillTraining.Trained, 300u) { Base = 300u }],
            EquipmentItems =
            [
                Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                    equippedLocation: 0x00100000u, ammoType: 1u),
                Equipment(801, "Deadly Fire Arrow", 0x10, combatUse: 3,
                    stackSize: 20, validLocations: AmmunitionSlot),
            ],
            ItemEntries =
            [
                InventoryItem(801, "Deadly Fire Arrow", 0x100u, 0u, false),
            ],
        };
        CombatModeGate gate = BoundAmmunitionGate(surface);

        Assert.True(gate.AmmunitionStale!(700u, MonsterDamageType.Fire));
        Assert.True(gate.WieldAmmunition!(MonsterDamageType.Fire));
        Assert.Equal(801u, surface.LastEquipObjectId);
    }

    /// <summary>
    /// Mutation: substitute the bundled ammo table when the game database
    /// is unloaded or has no rows; the arrow then becomes an action.
    /// </summary>
    [Fact]
    public void AmmoGateDoesNotSubstituteBundledOptionsForMissingRows()
    {
        foreach (VtankGameInfoDatabase database in new[]
            { VtankGameInfoDatabase.Empty, VtankGameInfoDatabase.LoadDefault() })
        {
            var surface = new FakeAutomation
            {
                CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
                CharacterSkills = [new PluginSkillInfo(47u, "Missile Weapons",
                    PluginSkillTraining.Trained, 300u) { Base = 300u }],
                EquipmentItems =
                [
                    Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                        equippedLocation: 0x00100000u, ammoType: 1u),
                    Equipment(801, "Deadly Fire Arrow", 0x10, combatUse: 3,
                        stackSize: 20, validLocations: AmmunitionSlot),
                ],
                ItemEntries =
                [
                    InventoryItem(801, "Deadly Fire Arrow", 0x100u, 0u, false),
                ],
            };
            CombatModeGate gate = BoundAmmunitionGate(surface, database);
            Assert.False(gate.AmmunitionStale!(700u, MonsterDamageType.Fire));
        }
    }

    /// <summary>
    /// A launcher with no ammunition it can load says so, once, instead of
    /// leaving the fight to re-enter its mode against a server that drops an
    /// empty quiver out of combat. Mutation: return quietly on an unavailable
    /// selection and nothing is said.
    /// </summary>
    [Fact]
    public void AnUnavailableAmmunitionSelectionIsSaidOnce()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            CharacterSkills = [new PluginSkillInfo(47u, "Missile Weapons",
                PluginSkillTraining.Trained, 300u) { Base = 300u }],
            EquipmentItems =
            [
                Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                    equippedLocation: 0x00100000u, ammoType: 1u),
            ],
        };
        CombatModeGate gate = BoundAmmunitionGate(surface, VtankGameInfoDatabase.LoadDefault());

        Assert.False(gate.AmmunitionStale!(700u, MonsterDamageType.Fire));
        Assert.False(gate.AmmunitionStale!(700u, MonsterDamageType.Fire));

        Assert.Single(
            surface.PostedSystemMessages,
            message => message.Contains("ammunition", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Mutation: treat an unavailable selection as stale;
    /// the gate enters an action branch despite having no pending action.
    /// </summary>
    [Fact]
    public void MissingAmmunitionIsNotAnActionInProgress()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            EquipmentItems =
            [
                Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                    equippedLocation: 0x00100000u, ammoType: 1u),
            ],
        };
        CombatModeGate gate = BoundAmmunitionGate(surface);

        Assert.False(gate.AmmunitionStale!(700u, MonsterDamageType.Fire));
        Assert.False(gate.WieldAmmunition!(MonsterDamageType.Fire));
    }

    /// <summary>
    /// A database downloaded in the middle of a session is read by the very
    /// next ammunition choice: the built-in one has no arrows to choose,
    /// the new one does. Mutation: keep reading the database the controller
    /// was built with, and the arrow is never chosen.
    /// </summary>
    [Fact]
    public void AmmunitionSelectionReadsADatabaseReplacedMidSession()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            CharacterSkills = [new PluginSkillInfo(47u, "Missile Weapons",
                PluginSkillTraining.Trained, 300u) { Base = 300u }],
            EquipmentItems =
            [
                Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                    equippedLocation: 0x00100000u, ammoType: 1u),
                Equipment(801, "Deadly Fire Arrow", 0x10, combatUse: 3,
                    stackSize: 20, validLocations: AmmunitionSlot),
            ],
            ItemEntries =
            [
                InventoryItem(801, "Deadly Fire Arrow", 0x100u, 0u, false),
            ],
        };
        var settings = new CombatSettings();
        var host = new FakeHost(surface);
        var controller = new CombatController(host, settings,
            gameInfo: VtankGameInfoDatabase.LoadDefault());
        CombatModeGate gate = controller.BindCombatModeGate(new CombatModeGate(
            host, settings, new VitalSettings(), _ => { }));
        Assert.False(gate.AmmunitionStale!(700u, MonsterDamageType.Fire));

        controller.ReplaceGameInfo(AmmoGameInfo);

        Assert.True(gate.AmmunitionStale!(700u, MonsterDamageType.Fire));
        Assert.True(gate.WieldAmmunition!(MonsterDamageType.Fire));
        Assert.Equal(801u, surface.LastEquipObjectId);
    }

    private static CombatModeGate BoundAmmunitionGate(
        FakeAutomation surface, VtankGameInfoDatabase? gameInfo = null)
    {
        var settings = new CombatSettings();
        var host = new FakeHost(surface);
        var controller = new CombatController(host, settings,
            gameInfo: gameInfo ?? AmmoGameInfo);
        var gate = new CombatModeGate(host, settings,
            new VitalSettings(), _ => { });
        return controller.BindCombatModeGate(gate);
    }

    [Fact]
    public void MissileLauncherSelectsOfficialBestAvailableAmmunition()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            CharacterSkills =
            [
                new PluginSkillInfo(
                    47u,
                    "Missile Weapons",
                    PluginSkillTraining.Trained,
                    300u)
                {
                    Base = 300u,
                },
            ],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "Fire Bow",
                    damageType: 0x10,
                    itemType: 0x100u,
                    equippedLocation: 0x00100000u,
                    ammoType: 0x001u),
                Equipment(
                    801,
                    "Deadly Fire Arrow",
                    damageType: 0x10,
                    combatUse: 3,
                    ammoType: 0x001u,
                    stackSize: 20,
                    // A quiver goes in the ammunition slot; it is not
                    // something the character can be wielding.
                    validLocations: AmmunitionSlot),
            ],
            ItemEntries =
            [
                InventoryItem(
                    801,
                    "Deadly Fire Arrow",
                    itemType: 0x100,
                    spellId: 0,
                    equipped: false),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(700u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponObjectId = 700,
            }));
        var controller = new CombatController(new FakeHost(surface), settings, gameInfo: AmmoGameInfo);

        controller.Toggle();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.25);

        Assert.Equal(801u, surface.LastEquipObjectId);
        Assert.Equal(0, surface.BeginCount);
    }

    [Fact]
    public void AmmunitionWieldGoesThroughAuthenticDropToPeacePrologue()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Missile },
            Targets = [Target(10, "Drudge", 2, 0)],
            CharacterSkills =
            [
                new PluginSkillInfo(
                    47u, "Missile Weapons", PluginSkillTraining.Trained, 300u)
                {
                    Base = 300u,
                },
            ],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "Fire Bow",
                    damageType: 0x10,
                    itemType: 0x100u,
                    equippedLocation: 0x00100000u,
                    ammoType: 0x001u),
                Equipment(
                    801,
                    "Deadly Fire Arrow",
                    damageType: 0x10,
                    combatUse: 3,
                    ammoType: 0x001u,
                    stackSize: 20,
                    // A quiver goes in the ammunition slot; it is not
                    // something the character can be wielding.
                    validLocations: AmmunitionSlot),
            ],
            ItemEntries =
            [
                InventoryItem(801, "Deadly Fire Arrow", 0x100, 0, equipped: false),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(700u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponObjectId = 700,
            }));
        var controller = new CombatController(new FakeHost(surface), settings, gameInfo: AmmoGameInfo);

        controller.Toggle();
        for (int tick = 0; tick < 6; tick++)
            controller.OnTick(0.25);

        // Peace is asked for BEFORE the arrow is wielded, never after.
        int peace = surface.CallLog.IndexOf("EnterMode:Peace");
        int equip = surface.CallLog.IndexOf("Equip:00000321");
        Assert.True(peace >= 0, "Peace was never requested: "
            + string.Join(" | ", surface.CallLog));
        Assert.True(equip > peace, "The arrow was wielded before Peace: "
            + string.Join(" | ", surface.CallLog));
        Assert.Equal(801u, surface.LastEquipObjectId);
    }

    [Fact]
    public void AmmunitionStillWieldsThroughThePanelsExternallyBoundGate()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
            CharacterSkills =
            [
                new PluginSkillInfo(
                    47u, "Missile Weapons", PluginSkillTraining.Trained, 300u)
                {
                    Base = 300u,
                },
            ],
            EquipmentItems =
            [
                Equipment(
                    700,
                    "Fire Bow",
                    damageType: 0x10,
                    itemType: 0x100u,
                    equippedLocation: 0x00100000u,
                    ammoType: 0x001u),
                Equipment(
                    801,
                    "Deadly Fire Arrow",
                    damageType: 0x10,
                    combatUse: 3,
                    ammoType: 0x001u,
                    stackSize: 20,
                    // A quiver goes in the ammunition slot; it is not
                    // something the character can be wielding.
                    validLocations: AmmunitionSlot),
            ],
            ItemEntries =
            [
                InventoryItem(801, "Deadly Fire Arrow", 0x100, 0, equipped: false),
            ],
        };
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponObjectId = 700,
            }));
        settings.CombatItemObjectIds.Add(700u);
        var vitals = new VitalSettings();
        var host = new FakeHost(surface);
        var controller = new CombatController(host, settings, vitals, AmmoGameInfo);

        // Exactly what the panel builds — the one shared gate, injected.
        var gate = new CombatModeGate(host, settings, vitals, _ => { });
        controller.BindCombatModeGate(gate);

        controller.Toggle();
        for (int tick = 0; tick < 6; tick++)
            controller.OnTick(0.25);

        Assert.Equal(801u, surface.LastEquipObjectId);
        Assert.NotNull(gate.AmmunitionStale);
        Assert.NotNull(gate.WieldAmmunition);
    }

    [Fact]
    public void StuckCombatModeUsesProfiledCasterAfterAuthenticRetryCount()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            IgnoreModeChanges = true,
            Targets = [Target(10, "Drudge", 2, 0)],
            EquipmentItems =
            [
                Equipment(700, "Fire Sword", damageType: 0x10),
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrderIds.Add(800u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                WeaponObjectId = 700,
            }));
        var controller = new CombatController(
            new FakeHost(surface),
            settings,
            new VitalSettings { DropToPeaceModeRetryCount = 2 });

        controller.Toggle();
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Equal(800u, surface.LastUsedItem);
        Assert.Equal(0u, surface.LastEquipObjectId);
        Assert.Contains(
            "bugged combat state",
            controller.Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionLossDisablesAndAborts()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 2, 0)],
        };
        var controller = new CombatController(
            new FakeHost(surface), new CombatSettings());
        controller.Toggle();
        surface.IsAvailable = false;

        controller.OnTick(0.1);

        Assert.False(controller.Enabled);
        Assert.Equal(1, surface.AbortCount);
        Assert.Equal("Session ended", controller.Status);
    }

    [Fact]
    public void CasterItemDebuffWaitsForUseDoneAndConfirmedCastChat()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        PluginInventoryItem lens = InventoryItem(
            800, "Imperil Lens", 0x8000, 90, equipped: true) with
        {
            ItemSpellcraft = 400,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            SpellLookup = [imperil],
            ItemEntries = [lens],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.CombatItemObjectIds.Add(lens.ObjectId);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((800u, 10u), surface.LastAppliedItem);
        Assert.Equal(0, surface.BeginCount);

        Assert.True(controller.HasPendingItemDebuff);

        surface.LastItemCompletion = new PluginItemUseCompletion(
            1, 800, 10, 0);
        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty,
                "You cast Imperil Other VII on Drudge.",
                string.Empty)
            {
                LogTextType = 0x07,
            },
        ];
        controller.OnTick(0.25);

        Assert.Equal(1, surface.ApplyCount);
        Assert.Contains("Waiting for a target", controller.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// Using a wand on a monster holds the item slot for the whole cast, so
    /// the attack rule (whose first refusal is that slot) cannot swing inside
    /// the wand's own animation, and the pass line's item column says so.
    /// Mutation: delete the <c>Arm(ActionLockKind.ItemUse, ...)</c> beside the
    /// wand's <c>Apply</c> and the first assertion fails; delete the
    /// <c>Release</c> in <c>ClearPendingItemDebuff</c> and the last one does.
    /// </summary>
    [Fact]
    public void AWandDebuffHoldsTheItemSlotForItsWholeCast()
    {
        PluginSpellInfo imperil = Spell(90, "Imperil Other VII") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
        };
        PluginInventoryItem lens = InventoryItem(
            800, "Imperil Lens", 0x8000, 90, equipped: true) with
        {
            ItemSpellcraft = 400,
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            SpellLookup = [imperil],
            ItemEntries = [lens],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.CombatItemObjectIds.Add(lens.ObjectId);
        var locks = new ActionLockTable();
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.BindActionLocks(locks, () => false);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((800u, 10u), surface.LastAppliedItem);
        Assert.True(locks.IsLocked(ActionLockKind.ItemUse));

        // Still held most of the way through the cast window.
        locks.Advance(11d);
        Assert.True(locks.IsLocked(ActionLockKind.ItemUse));

        surface.LastItemCompletion = new PluginItemUseCompletion(
            1, 800, 10, 0);
        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty,
                "You cast Imperil Other VII on Drudge.",
                string.Empty)
            {
                LogTextType = 0x07,
            },
        ];
        controller.OnTick(0.25);

        Assert.False(locks.IsLocked(ActionLockKind.ItemUse));
    }

    [Fact]
    public void ProcWeaponChargesAtZeroAndRequiresCastChatNotAttackDone()
    {
        PluginSpellInfo imperil = Spell(91, "Imperil Other VII") with
        {
            School = 31,
            IsDebuff = true,
            IsOffensive = true,
            DurationSeconds = 60,
            TargetMask = 0x10,
        };
        PluginInventoryItem sword = InventoryItem(
            801, "Imperil Sword", 1, 0, equipped: true) with
        {
            ItemSpellcraft = 400,
            AppraisedSpellIds = [91u],
        };
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 5, 0)],
            SpellLookup = [imperil],
            ItemEntries = [sword],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.CombatItemObjectIds.Add(sword.ObjectId);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.Equal(0f, surface.LastBeginPower);

        surface.CombatSnapshot = Physical(request: true, build: true, bar: 0);
        controller.OnTick(0.1);
        Assert.Equal(1, surface.ReleaseCount);

        surface.CombatSnapshot = Physical() with
        {
            CompletionRevision = 1,
            CompletionWeenieError = 0,
        };
        controller.OnTick(0.1);
        Assert.DoesNotContain("Waiting for a target", controller.Status, StringComparison.Ordinal);

        surface.ChatMessages =
        [
            new PluginChatMessage(
                1, 0, 0, string.Empty,
                "You cast Imperil Other VII on Drudge.",
                string.Empty)
            {
                LogTextType = 0x07,
            },
        ];
        controller.OnTick(0.1);
        Assert.Contains("Waiting for a target", controller.Status, StringComparison.Ordinal);
    }


    private static CombatModeGate Gate(
        FakeAutomation surface,
        CombatSettings? settings = null,
        VitalSettings? vitals = null,
        List<string>? stops = null) =>
        new(
            new FakeHost(surface),
            settings ?? new CombatSettings(),
            vitals ?? new VitalSettings(),
            notice => (stops ?? []).Add(notice));

    /// <summary>
    /// The client says "magic" the moment it sends the change; the server
    /// says so only once the body has left its old stance, and a spell sent
    /// in between fizzles. The gate is not ready until the server's stance
    /// arrives, or the confirmation window runs out with nothing heard.
    /// </summary>
    [Fact]
    public void GateWaitsForTheServersStanceAfterAModeChangeBeforeItIsReady()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            WithholdModeEcho = true,
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Equal(PluginCombatMode.Magic, surface.CombatSnapshot.Mode);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Entering Magic mode", gate.Status);
        Assert.Equal(1, surface.ModeChangeRequests);

        surface.ConfirmPendingModeChange();
        gate.AdvancePass(0.1);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Ready", gate.Status);
    }

    /// <summary>
    /// On a host that passes the server's word on, the gate is ready the
    /// moment the server agrees with the client, and not before: not on the
    /// client's own word, and not when the confirmation window runs out.
    /// </summary>
    [Fact]
    public void GateIsReadyTheMomentTheServerAgreesWhenTheHostSaysWhatTheServerSaid()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with
            {
                Mode = PluginCombatMode.Peace,
                ServerMode = PluginCombatMode.Peace,
            },
            WithholdModeEcho = true,
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(PluginCombatMode.Magic, surface.CombatSnapshot.Mode);
        Assert.Equal(PluginCombatMode.Peace, surface.CombatSnapshot.ServerMode);

        // The window running out changes nothing while the server disagrees.
        gate.AdvancePass(0.7);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Entering Magic mode", gate.Status);
        Assert.Equal(1, surface.ModeChangeRequests);

        surface.ConfirmPendingModeChange();
        Assert.Equal(PluginCombatMode.Magic, surface.CombatSnapshot.ServerMode);
        gate.AdvancePass(0.01);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Ready", gate.Status);
    }

    /// <summary>
    /// ACE sends no combat mode when the body is already in the stance asked
    /// for, so a server mode that disagrees may never be corrected. The gate
    /// waits three seconds for the echo and then acts on the client's mode: a
    /// spell may fizzle if the server had not taken the stance, which beats
    /// stalling for good.
    /// </summary>
    [Fact]
    public void GateStopsWaitingOnAnUnechoedServerModeThreeSecondsAfterTheRequest()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with
            {
                Mode = PluginCombatMode.Peace,
                ServerMode = PluginCombatMode.Peace,
            },
            WithholdModeEcho = true,
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(1, surface.ModeChangeRequests);

        gate.AdvancePass(2.9);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Entering Magic mode", gate.Status);

        gate.AdvancePass(0.11);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Ready", gate.Status);
        Assert.Equal(1, surface.ModeChangeRequests);
    }

    /// <summary>
    /// The stall seen live: the mode is already right, the server's word is
    /// stale because ACE does not echo a stance the body is already in, and
    /// the gate never sent a request. It must still release.
    /// </summary>
    [Fact]
    public void GateReleasesAStaleServerModeWhenNoRequestWasEverSent()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with
            {
                Mode = PluginCombatMode.Missile,
                ServerMode = PluginCombatMode.Peace,
            },
            WithholdModeEcho = true,
            EquipmentItems =
            [
                Equipment(700, "Fire Bow", 0x10, itemType: 0x100u,
                    equippedLocation: 0x00100000u, ammoType: 1u),
            ],
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Missile));
        Assert.Equal("Entering Missile mode", gate.Status);

        // The clock starts at 0.6 s after Reset, so 0.7 s is on it now and the
        // three-second bound falls 2.3 s further on.
        gate.AdvancePass(2.2);
        Assert.False(gate.TryPrepare(PluginCombatMode.Missile));

        gate.AdvancePass(0.11);
        Assert.True(gate.TryPrepare(PluginCombatMode.Missile));
        Assert.Equal("Ready", gate.Status);
        Assert.Equal(0, surface.ModeChangeRequests);
    }

    [Fact]
    public void GateGivesUpWaitingForTheServersStanceAfterTheConfirmationWindow()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            WithholdModeEcho = true,
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        gate.AdvancePass(0.5);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        gate.AdvancePass(0.11);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(1, surface.ModeChangeRequests);
    }

    [Fact]
    public void GateWieldedCasterInPeaceReAsksForMagicUntilTheClientAgrees()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            DeferModeConfirmation = true,
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Equal(["EnterMode:Magic"], surface.CallLog);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.ModeChangeRequests);

        surface.ConfirmPendingModeChange();
        gate.AdvancePass(0.1);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.ModeChangeRequests);
    }

    /// <summary>
    /// Mutation pin: replace PendingInventory with BusyCount as the gate;
    /// an appraisal reference then incorrectly blocks preparation.
    /// </summary>
    [Fact]
    public void GateBlocksOnPendingInventoryButNotAppraisalBusyCount()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            EquipmentItems =
            [
                Equipment(800, "Wand", damageType: 0, itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
            BusyState = new PluginBusyState(1, false, 800u, 0u, 0u, false),
        };
        CombatModeGate gate = Gate(surface);

        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        surface.BusyState = surface.BusyState with { PendingInventory = true };
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Busy", gate.Status);
    }

    [Fact]
    public void GateProfiledCasterNotWieldedWieldsThenEntersMagicInOrder()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(), // Mode = Melee
            DeferModeConfirmation = true,
            SimulateAsyncEquip = true,
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        settings.CombatItemOrderIds.Add(800u);
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(["EnterMode:Peace"], surface.CallLog);

        surface.ConfirmPendingModeChange();
        // The ack RESTARTS the 600 ms window — it re-stamps the request time
        // — so the gate still reports the PRE-request mode for one
        // more pass and the drop-to-peace branch re-asks. That second request
        // re-stamps the saved mode from the now-Peace live mode, which is
        // what lets the pass after it proceed.
        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(
            ["EnterMode:Peace", "EnterMode:Peace"],
            surface.CallLog);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        // Equip only happens after the snapshot reports Peace, never before.
        Assert.Equal(
            ["EnterMode:Peace", "EnterMode:Peace", "Equip:00000320"],
            surface.CallLog);

        surface.ConfirmPendingEquip();
        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(
            [
                "EnterMode:Peace", "EnterMode:Peace", "Equip:00000320",
                "EnterMode:Magic",
            ],
            surface.CallLog);

        surface.ConfirmPendingModeChange();
        gate.AdvancePass(1.0);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(
            [
                "EnterMode:Peace", "EnterMode:Peace", "Equip:00000320",
                "EnterMode:Magic",
            ],
            surface.CallLog);
    }

    [Fact]
    public void GateLogsRequestingPeaceThenEquipThenRequestingMagicAndNeverReDropsAfterEquip()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(), // Mode = Melee, empty hands
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        settings.CombatItemOrderIds.Add(800u);
        CombatModeGate gate = Gate(surface, settings);
        var log = new List<(MacroLogChannel Channel, string Message)>();
        gate.Log = (channel, message) => log.Add((channel, message));

        bool ready = false;
        for (int pass = 0; pass < 8 && !ready; pass++)
        {
            gate.AdvancePass(1.0);
            ready = gate.TryPrepare(PluginCombatMode.Magic);
        }
        Assert.True(ready, "gate never converged");

        // Peace is asked for twice: once to leave melee, and once more on
        // the pass the server's stance arrives, while the confirmation
        // window still reports the old mode. That is what a live swap logs.
        Assert.Equal(
            [
                (MacroLogChannel.BusyState, "(FCM) requesting Peace"),
                (MacroLogChannel.BusyState, "(FCM) requesting Peace"),
                (MacroLogChannel.BusyState, "(FCM) equip Recovery Wand"),
                (MacroLogChannel.BusyState, "(FCM) requesting Magic"),
            ],
            log);

        int equipIndex = log.FindIndex(entry => entry.Message.Contains("equip"));
        Assert.DoesNotContain(
            log.Skip(equipIndex + 1),
            entry => entry.Message.Contains("requesting Peace"));
    }

    [Fact]
    public void GateNoWandAnywherePostsTheNoticeOnceAndStopsTheMacro()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var stops = new List<string>();
        CombatModeGate gate = Gate(surface, stops: stops);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(
            "Add at least one wand to your profile first.",
            Assert.Single(surface.PostedSystemMessages).Replace(
                "[MossTank] ",
                string.Empty,
                StringComparison.Ordinal));
        Assert.Equal(
            "Add at least one wand to your profile first.",
            Assert.Single(stops));

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Single(surface.PostedSystemMessages);
        Assert.Equal(2, stops.Count);

        gate.Reset();
        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.PostedSystemMessages.Count);
    }

    /// <summary>
    /// Mutation pin: restore name matching as a fallback; a legacy sidecar
    /// name then equips the unselected caster instead of refusing.
    /// </summary>
    [Fact]
    public void GateDoesNotTreatSidecarNamesAsItemIdentity()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems =
            [
                Equipment(801u, "Same Wand", damageType: 0,
                    itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Same Wand");
        settings.CombatItemOrder.Add("Same Wand");
        var stops = new List<string>();
        CombatModeGate gate = Gate(surface, settings, stops: stops);

        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(CombatModeGate.NoWandNotice, gate.Status);
        Assert.Equal([CombatModeGate.NoWandNotice], stops);
        Assert.Empty(surface.CallLog);
    }

    /// <summary>
    /// Mutation pin: sort authored IDs before selection; the first valid
    /// caster becomes 800 rather than the authored 802.
    /// </summary>
    [Fact]
    public void GateUsesOrderedIdWithDuplicateNamesAndSkipsMissingOrForeignObjects()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems =
            [
                Equipment(801u, "Same Wand", damageType: 0,
                    itemType: 0x00008000u),
                Equipment(802u, "Same Wand", damageType: 0,
                    itemType: 0x00008000u),
                Equipment(800u, "Same Wand", damageType: 0,
                    itemType: 0x00008000u),
            ],
        };
        surface.UnownedEquipmentIds.Add(801u);
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Same Wand");
        settings.CombatItemOrder.Add("Same Wand");
        settings.CombatItemOrderIds.Add(803u); // no such object
        settings.CombatItemOrderIds.Add(801u); // not owned
        settings.CombatItemOrderIds.Add(802u); // first valid authored ID
        settings.CombatItemOrderIds.Add(800u); // valid but later
        CombatModeGate gate = Gate(surface, settings);

        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(802u, surface.LastEquipObjectId);
    }

    [Fact]
    public void GateModeRequestNeverConfirmedKeepsAskingWithoutGivingUp()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            DeferModeConfirmation = true,
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        var stops = new List<string>();
        CombatModeGate gate = Gate(
            surface,
            vitals: new VitalSettings { DropToPeaceModeRetryCount = 2 },
            stops: stops);

        for (int pass = 0; pass < 6; pass++)
        {
            gate.AdvancePass(1.0);
            Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        }

        Assert.Equal(6, surface.ModeChangeRequests);
        Assert.Empty(stops);
        Assert.Empty(surface.PostedSystemMessages);
    }

    [Fact]
    public void GateDropToPeaceBudgetIsCountedInPassesAndEndsInWandUseRecovery()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            DeferModeConfirmation = true,
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        settings.CombatItemOrderIds.Add(800u);
        var stops = new List<string>();
        CombatModeGate gate = Gate(
            surface,
            settings,
            new VitalSettings { DropToPeaceModeRetryCount = 3 },
            stops);

        // Three passes, each well past the 600 ms window so the Peace request
        // is genuinely re-issued; the third exhausts the budget.
        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.ModeChangeRequests);
        Assert.Equal(0u, surface.LastUsedItem);

        gate.AdvancePass(1.0);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(800u, surface.LastUsedItem);
        Assert.Equal(2, surface.ModeChangeRequests);
        Assert.Empty(stops);
        Assert.Contains(
            "bugged combat state",
            Assert.Single(surface.PostedSystemMessages),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GateFallbackWandIsItemsPageInsertionOrderNotNameOrder()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            EquipmentItems =
            [
                Equipment(801, "Zephyr Wand", damageType: 0, itemType: 0x00008000u),
                Equipment(802, "Adamant Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Zephyr Wand");
        settings.CombatItemNames.Add("Adamant Wand");
        settings.CombatItemOrder.Add("Zephyr Wand");
        settings.CombatItemOrder.Add("Adamant Wand");
        settings.CombatItemOrderIds.Add(801u);
        settings.CombatItemOrderIds.Add(802u);
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));

        Assert.Equal(["Equip:00000321"], surface.CallLog);
    }

    [Fact]
    public void GateKeepsAnAlreadyWieldedCasterRatherThanReEquipping()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            EquipmentItems =
            [
                Equipment(
                    801,
                    "Zephyr Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
                Equipment(802, "Adamant Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Adamant Wand");
        settings.CombatItemOrder.Add("Adamant Wand");
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Empty(surface.CallLog);
    }

    [Fact]
    public void GateIsHandsOffWhileTheCombatModeIsUnknown()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = new PluginCombatSnapshot
            {
                Mode = PluginCombatMode.Unknown,
            },
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        settings.CombatItemOrderIds.Add(800u);
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Empty(surface.CallLog);
    }

    [Fact]
    public void GateWithNoEquipmentProjectionStillRequiresTheMode()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            EquipmentAvailable = false,
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(["EnterMode:Magic"], surface.CallLog);

        gate.AdvancePass(1d);
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Ready", gate.Status);
    }

    /// <summary>
    /// Mutation pin: return ready for an unavailable mode command. The gate
    /// reports success while the observed stance still differs.
    /// Mutation executed: <c>the Unavailable branch return false was replaced with return true</c>.
    /// </summary>
    [Fact]
    public void GateDoesNotReportReadyWhenModeCommandIsUnavailable()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            ModeCommandUnavailable = true,
            EquipmentItems =
            [
                Equipment(800, "War Wand", damageType: 0, itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("War Wand");
        settings.CombatItemOrder.Add("War Wand");
        CombatModeGate gate = Gate(surface, settings);

        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal("Combat mode is unavailable", gate.Status);
    }

    /// <summary>
    /// Mutation pin: infer a mode acknowledgement from a changed raw mode.
    /// That spuriously restarts the 600 ms window without a qualifying receipt.
    /// Mutation executed: <c>the acknowledgement revision comparison was replaced with snapshot.Mode != _modeBeforeRequest</c>.
    /// </summary>
    [Fact]
    public void RawModeChangeWithoutMotionAckDoesNotRestartWindow()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            DeferModeConfirmation = true,
        };
        CombatModeGate gate = Gate(surface);

        Assert.False(gate.TryDropToPeace([], "Sword"));
        surface.CombatSnapshot = surface.CombatSnapshot with
        {
            Mode = PluginCombatMode.Peace,
        };
        gate.AdvancePass(0.61d);

        Assert.True(gate.TryDropToPeace([], "Sword"));
        Assert.Equal(["EnterMode:Peace"], surface.CallLog);
    }

    /// <summary>
    /// Mutation pin: seed from held-first/name-sorted equipment instead of
    /// the world table's enumeration. The last name-sorted weapon wins.
    /// Mutation executed: <c>CaptureWorldPlacementsInOrder() was ordered by object ID</c>.
    /// </summary>
    [Fact]
    public void EquipmentTrackerSeedsFromWorldOrderAndUsesExactSlots()
    {
        var surface = new FakeAutomation
        {
            EquipmentItems =
            [
                Equipment(10, "Zulu Sword", damageType: 1,
                    equippedLocation: 0x02000000u),
                Equipment(30, "Combined Mask", damageType: 1,
                    equippedLocation: 0x00300000u),
                Equipment(20, "Alpha Sword", damageType: 1,
                    equippedLocation: 0x00100000u),
            ],
        };
        using var tracker = new EquipmentTracker(surface, () => { });

        Assert.Equal(20u, tracker.WeaponId);
        Assert.Equal(0u, tracker.ShieldId);
        Assert.Equal([10u, 30u, 20u], tracker.EquippedIds);
    }

    /// <summary>
    /// Mutation executed: <c>scheduleSettlement: !observation.IsInitialPlacement</c>
    /// was replaced with <c>scheduleSettlement: true</c>; the initial world
    /// placement then incorrectly woke the gate after 100 ms.
    /// </summary>
    [Fact]
    public void EquipmentTrackerAcceptsInitialEquippedObjectWithoutSettlement()
    {
        var surface = new FakeAutomation { EquipmentItems = [] };
        int pokes = 0;
        using var tracker = new EquipmentTracker(surface, () => pokes++);

        surface.EquipmentItems = [Equipment(10u, "Held Wand", damageType: 0,
            itemType: 0x00008000u, equippedLocation: 0x01000000u)];
        surface.EmitPlacement(new PluginEquipmentObservation(
            10u, 0x01000000u, false)
        {
            IsInitialPlacement = true,
        });

        Assert.Equal(10u, tracker.WeaponId);
        Assert.Equal([10u], tracker.EquippedIds);
        tracker.Advance(0.1d);
        Assert.Equal(0, pokes);
    }

    /// <summary>
    /// Mutation pin: omit the independent settlement scheduled by an
    /// unrelated authoritative receipt. The swap remains in cooldown after
    /// its 100 ms receipt timer and the macro is never poked.
    /// Mutation executed: <c>settlement was conditioned on ValidOrZero(objectId) != 0u</c>.
    /// </summary>
    [Fact]
    public void EquipmentTrackerReceiptsSettleIndependentlyAndValidateIds()
    {
        var surface = new FakeAutomation
        {
            EquipmentItems = [Equipment(10, "Sword", damageType: 1,
                equippedLocation: 0x00100000u)],
        };
        int pokes = 0;
        using var tracker = new EquipmentTracker(surface, () => pokes++);
        Assert.True(tracker.TryArmSwap(10u, requirePeace: true,
            PluginCombatMode.Peace));
        Assert.True(tracker.RecentlySwapped);

        surface.EmitPlacement(new PluginEquipmentObservation(
            99u, 0x00800000u, false));
        Assert.Equal(0u, tracker.AmmoId); // receipt can predate the object
        tracker.Advance(0.099d);
        Assert.True(tracker.RecentlySwapped);
        Assert.Equal(0, pokes);
        tracker.Advance(0.001d);
        Assert.False(tracker.RecentlySwapped);
        Assert.Equal(1, pokes);

        Assert.True(tracker.TryArmSwap(10u, requirePeace: false,
            PluginCombatMode.Melee));
        tracker.Advance(0.8d);
        Assert.True(tracker.RecentlySwapped); // strict deadline comparison
        tracker.Advance(0.001d);
        Assert.False(tracker.RecentlySwapped);

        surface.EmitPlacement(new PluginEquipmentObservation(10u, 0u, true));
        Assert.Equal(0u, tracker.WeaponId);
        Assert.DoesNotContain(10u, tracker.EquippedIds);
    }

    /// <summary>
    /// Mutation pin: classify a shield-valid melee item as a shield before
    /// checking its object class. That sends the wrong item to the offhand.
    /// Mutation executed: <c>KindFor returned Shield for the exact location before checking ObjectClass</c>.
    /// </summary>
    [Theory]
    [InlineData(PluginObjectClass.MeleeWeapon)]
    [InlineData(PluginObjectClass.MissileWeapon)]
    [InlineData(PluginObjectClass.WandStaffOrb)]
    public void GateDefaultShieldUsesKindAfterWeaponClass(
        PluginObjectClass maskedClass)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            EquipmentItems =
            [
                Equipment(100u, "Sword", damageType: 1,
                    equippedLocation: 0x00100000u),
                Equipment(200u, "Misleading Sword", damageType: 1,
                    validLocations: 0x00200000u)
                    with { ObjectClass = maskedClass },
                Equipment(300u, "Shield", damageType: 0,
                    validLocations: 0x00200000u)
                    with { ObjectClass = PluginObjectClass.Armor },
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemOrderIds.Add(200u);
        settings.CombatItemOrderIds.Add(300u);
        CombatModeGate gate = Gate(surface, settings);

        Assert.False(gate.TryPrepare(PluginCombatMode.Melee,
            overrideItemId: 100u, autoSelect: false));
        Assert.Contains("EquipSecondary:0000012C", surface.CallLog);
        Assert.DoesNotContain("EquipSecondary:000000C8", surface.CallLog);
    }

    /// <summary>
    /// Mutation pin: retain signed direct equipment ids when parsing a
    /// monster rule before forwarding its primary and one-hand secondary.
    /// Mutation executed: <c>WeaponObjectId used weapon &gt; 0 and
    /// OffhandObjectId used offhand &gt;= ListedTypesEnd</c>.
    /// </summary>
    [Fact]
    public void SignedMonsterEquipmentIdsReachPrimaryAndOneHandSecondaryDispatch()
    {
        const uint primaryId = 0x8001_AC87u;
        const uint secondaryId = 0x8001_B291u;
        VtankDatabase profile = VtankDefaultSettingsDatabase.Create();
        VtankTable monsters = profile.Find(VtankMonsterRuleTable.TableName)!;
        monsters.Rows[0].Cells[3] = VtankCell.Int(unchecked((int)primaryId));
        monsters.Rows[0].Cells[19] = VtankCell.Int(unchecked((int)secondaryId));
        MonsterRuleActions actions = Assert.Single(
            VtankMonsterRuleTable.TryRead(profile)!).Actions;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            EquipmentItems =
            [
                Equipment(primaryId, "Primary", damageType: 1),
                Equipment(secondaryId, "Secondary", damageType: 1),
            ],
        };
        using CombatModeGate gate = Gate(surface);

        Assert.False(gate.TryPrepare(PluginCombatMode.Melee,
            overrideItemId: actions.WeaponObjectId, autoSelect: false,
            secondaryItemId: actions.OffhandObjectId));
        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Melee,
            overrideItemId: actions.WeaponObjectId, autoSelect: false,
            secondaryItemId: actions.OffhandObjectId));

        Assert.Equal(
            ["Equip:8001AC87", "EquipSecondary:8001B291"],
            surface.CallLog);
    }

    /// <summary>
    /// Mutation: remove the primary-use bit from
    /// <c>SelectAutomaticWeapon</c>; the disabled and left-only rows then
    /// equip the signed item. This reaches the controller through a loaded
    /// hand-use setting rather than only testing the profile serializer.
    /// Mutation executed: <c>removed the ItemUseSpecifiers primary-bit predicate</c>.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(-1, true)] // no imported row defaults to both hands
    public void AutomaticPrimaryHonorsImportedHandUseFlagsIncludingSignedIds(
        int importedUses, bool expectsEquip)
    {
        const uint signedSword = 0x8001_AC87u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems = [Equipment(signedSword, "Signed Fire Sword", 0x0010)],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(signedSword);
        settings.CombatItemOrderIds.Add(signedSword);
        if (importedUses >= 0)
            settings.ItemUseSpecifiers[signedSword] = importedUses;
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
        }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.EquipOneStepForMonster("Fixture");

        Assert.Equal(expectsEquip,
            surface.CallLog.Contains("Equip:8001AC87", StringComparer.Ordinal));
    }

    /// <summary>
    /// When nothing can be picked automatically the fight is had with
    /// whatever is already in hand -- but a weapon the profile has turned off
    /// for that hand is not "in hand" for this purpose, so the plan falls back
    /// to no weapon at all. With no weapon the stance is a caster's, and a
    /// character with only life magic is told it is about to drain.
    ///
    /// Mutation executed: <c>replaced the primary-bit check in the
    /// wielded-weapon fallback with true</c>. The held sword then becomes the
    /// plan, the stance is a swordsman's, and the notice is never posted.
    /// </summary>
    [Fact]
    public void DisabledHeldWeaponIsNotTheAutomaticPrimaryFallback()
    {
        const uint signedSword = 0x8001_B291u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", distance: 5, angle: 0)],
            CharacterSkills =
            [
                new PluginSkillInfo(33u, "Life Magic",
                    PluginSkillTraining.Trained, 300u),
            ],
            EquipmentItems =
            [
                Equipment(signedSword, "Disabled Held Sword", 0x0010,
                    equippedLocation: 0x00100000u),
            ],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.CombatItemObjectIds.Add(signedSword);
        settings.ItemUseSpecifiers[signedSword] = 0;
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Auto,
        }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25d);

        Assert.Contains(
            surface.PostedSystemMessages,
            message => message.Contains("autoselecting drain",
                StringComparison.Ordinal));
        Assert.DoesNotContain("Equip:8001B291", surface.CallLog);
    }

    /// <summary>
    /// Mutation: run the imported hand bits through every equipment request.
    /// Explicit monster equipment is deliberately not an automatic pick, so
    /// both disabled ids must still reach their hand commands.
    /// Mutation executed: <c>applied ItemUseSpecifiers to explicit primary and secondary resolution</c>.
    /// </summary>
    [Fact]
    public void ExplicitEquipmentIdsBypassImportedHandUseFlags()
    {
        const uint primary = 0x8000_DEB2u;
        const uint secondary = 0x8001_AC87u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems =
            [
                Equipment(primary, "Explicit Primary", 0x0010),
                Equipment(secondary, "Explicit Secondary", 0x0010),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(primary);
        settings.ItemUseSpecifiers[primary] = 0;
        settings.ItemUseSpecifiers[secondary] = 0;
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
            WeaponObjectId = primary,
            OffhandObjectId = secondary,
        }));
        var controller = new CombatController(new FakeHost(surface), settings);

        DriveEquipPasses(controller);

        Assert.Contains("Equip:8000DEB2", surface.CallLog);
        Assert.Contains("EquipSecondary:8001AC87", surface.CallLog);
    }

    /// <summary>
    /// Mutation: resolve a rule's named offhand from the pack without asking
    /// where the item can be worn. A pack item that merely shares the name is
    /// then requested on every pass, never reaches the hand, and the pass
    /// never reports the character ready, so it never attacks.
    /// Mutation executed: <c>dropped the off-hand location check from the pack fallback</c>.
    /// </summary>
    [Fact]
    public void NamedOffhandSkipsPackItemThatCannotBeHeld()
    {
        const uint primary = 700u;
        const uint packItem = 0x8001_B292u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems = [Equipment(primary, "Primary", 0x0010)],
            ItemEntries =
            [
                // A trophy sharing the shield's name: worn nowhere at all.
                InventoryItem(packItem, "Shield of Souls", 1u,
                    spellId: 0u, equipped: false),
            ],
        };
        surface.WorldOnlyObjectIds.Add(packItem);
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(primary);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
            WeaponObjectId = primary,
            OffhandName = "Shield of Souls",
        }));
        var controller = new CombatController(new FakeHost(surface), settings);

        Assert.True(DriveEquipPasses(controller));
        Assert.DoesNotContain("EquipSecondary:8001B292", surface.CallLog);
    }

    /// <summary>
    /// Mutation: return zero when an explicit offhand is absent from the
    /// equipment projection. The current inventory fallback is still needed
    /// for an object the world knows but whose equipment view has not caught
    /// up yet.
    /// Mutation executed: <c>removed ResolveWieldPlan's ResolveInventoryObjectId fallback</c>.
    /// </summary>
    [Fact]
    public void MissingEquipmentViewOffhandRetainsInventoryFallback()
    {
        const uint primary = 700u;
        const uint inventoryOnlySecondary = 0x8001_B291u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems = [Equipment(primary, "Primary", 0x0010)],
            ItemEntries =
            [
                InventoryItem(inventoryOnlySecondary, "Inventory Offhand", 1u,
                    spellId: 0u, equipped: false),
            ],
        };
        surface.WorldOnlyObjectIds.Add(inventoryOnlySecondary);
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(primary);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
            WeaponObjectId = primary,
            OffhandObjectId = inventoryOnlySecondary,
        }));
        var controller = new CombatController(new FakeHost(surface), settings);

        DriveEquipPasses(controller);

        Assert.Contains("EquipSecondary:8001B291", surface.CallLog);
    }

    /// <summary>
    /// Mutation: return the first profiled item from
    /// <c>SelectAutomaticSecondaryWeapon</c>. That admits the primary,
    /// caster, two-handed, or primary-only rows before the one eligible
    /// left-hand melee item.
    /// Mutation executed: <c>replaced the secondary candidate guards with a profiled-item check</c>.
    /// </summary>
    [Fact]
    public void AutoWeaponExcludesPrimaryCasterTwoHandAndPrimaryOnlyRows()
    {
        const uint primary = 100u;
        const uint caster = 200u;
        const uint twoHand = 300u;
        const uint primaryOnly = 400u;
        const uint leftHand = 500u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems =
            [
                Equipment(primary, "Primary", 0x0010),
                Equipment(caster, "Caster", 0, itemType: 0x8000u),
                Equipment(twoHand, "Two hand", 0x0010,
                    validLocations: 0x02100000u),
                Equipment(primaryOnly, "Primary only", 0x0010),
                Equipment(leftHand, "Left hand", 0x0010),
            ],
        };
        var settings = SecondarySettings(primary, VtankSecondaryEquip.AutoWeapon);
        foreach (uint id in new[] { primary, caster, twoHand, primaryOnly, leftHand })
        {
            settings.CombatItemObjectIds.Add(id);
            settings.CombatItemOrderIds.Add(id);
        }
        settings.ItemUseSpecifiers[primaryOnly] = 1;
        settings.ItemUseSpecifiers[leftHand] = 2;
        var controller = new CombatController(new FakeHost(surface), settings);

        DriveEquipPasses(controller);

        Assert.Contains("EquipSecondary:000001F4", surface.CallLog);
        Assert.DoesNotContain("EquipSecondary:00000064", surface.CallLog);
        Assert.DoesNotContain("EquipSecondary:000000C8", surface.CallLog);
        Assert.DoesNotContain("EquipSecondary:0000012C", surface.CallLog);
        Assert.DoesNotContain("EquipSecondary:00000190", surface.CallLog);
    }

    [Theory]
    [InlineData((int)VtankSecondaryEquip.AutoShield, false, false, 300u)]
    [InlineData((int)VtankSecondaryEquip.AutoWeapon, false, false, 200u)]
    [InlineData((int)VtankSecondaryEquip.None, false, false, 0u)]
    [InlineData((int)VtankSecondaryEquip.Auto, true, false, 300u)]
    [InlineData((int)VtankSecondaryEquip.Auto, false, true, 200u)]
    [InlineData((int)VtankSecondaryEquip.Auto, true, true, 300u)]
    [InlineData((int)VtankSecondaryEquip.Auto, false, false, 300u)]
    public void SecondaryEquipModesDispatchByTrainedSkills(
        int mode, bool shieldTrained, bool dualWieldTrained,
        uint expectedSecondary)
    {
        const uint primary = 100u;
        const uint weapon = 200u;
        const uint shield = 300u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            CharacterSkills = SecondarySkills(shieldTrained, dualWieldTrained),
            EquipmentItems =
            [
                Equipment(primary, "Primary", 0x0010),
                Equipment(weapon, "Secondary Weapon", 0x0010),
                Equipment(shield, "Shield", 0,
                    validLocations: 0x00200000u) with
                    {
                        ObjectClass = PluginObjectClass.Armor,
                    },
            ],
        };
        var settings = SecondarySettings(primary, (VtankSecondaryEquip)mode);
        settings.CombatItemObjectIds.Add(weapon);
        settings.CombatItemObjectIds.Add(shield);
        settings.CombatItemOrderIds.Add(weapon);
        settings.CombatItemOrderIds.Add(shield);
        settings.ItemUseSpecifiers[weapon] = 2;
        var controller = new CombatController(new FakeHost(surface), settings);

        DriveEquipPasses(controller);

        string[] secondary = surface.CallLog
            .Where(static call => call.StartsWith("EquipSecondary:", StringComparison.Ordinal))
            .ToArray();
        if (expectedSecondary == 0u)
            Assert.Empty(secondary);
        else
            Assert.Contains($"EquipSecondary:{expectedSecondary:X8}", secondary);
    }

    /// <summary>
    /// A hand restriction belongs to the profile that carried it. Loading a
    /// profile that says nothing about the sword must not leave the previous
    /// profile's "never in the right hand" in force, or the character stands
    /// there unarmed for the rest of the session.
    ///
    /// Mutation executed: <c>the imported-flag table was not cleared before
    /// reading a profile</c> (the clear was dropped from the table reader).
    /// The sword then stays forbidden under the second profile and is never
    /// equipped.
    /// </summary>
    [Fact]
    public void LoadingAProfileWithoutHandRulesReleasesTheEarlierRestriction()
    {
        const uint sword = 0x8001_AC87u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems = [Equipment(sword, "Signed Fire Sword", 0x0010)],
        };
        VtankDatabase restricted = VtankDefaultSettingsDatabase.Create();
        restricted.Find("ItemUseSpecifiers")!.Rows.Add(new VtankRow
        {
            Cells = { VtankCell.Int(unchecked((int)sword)), VtankCell.Int(0) },
        });
        VtankDatabase unrestricted = VtankDefaultSettingsDatabase.Create();
        unrestricted.Tables.RemoveAll(
            static entry => entry.Name == "ItemUseSpecifiers");

        var all = new VtankSettingsProfileSerializer.AllSettings
        {
            Combat = new CombatSettings(),
            Buffs = new BuffSettings(),
            Vitals = new VitalSettings(),
            Inventory = new InventorySettings(),
            Navigation = new NavigationSettings(),
        };
        VtankSettingsProfileSerializer.Load(restricted.Render(), all);
        CombatSettings settings = all.Combat;
        settings.CombatItemObjectIds.Add(sword);
        settings.CombatItemOrderIds.Add(sword);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
        }));

        var restrictedController = new CombatController(
            new FakeHost(surface), settings);
        DriveEquipPasses(restrictedController);
        Assert.DoesNotContain("Equip:8001AC87", surface.CallLog);

        VtankSettingsProfileSerializer.Load(unrestricted.Render(), all);
        settings.CombatItemObjectIds.Add(sword);
        settings.CombatItemOrderIds.Add(sword);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
        }));
        var releasedController = new CombatController(
            new FakeHost(surface), settings);
        DriveEquipPasses(releasedController);

        Assert.Contains("Equip:8001AC87", surface.CallLog);
    }

    /// <summary>
    /// The shield the offhand reaches for is chosen by a different rule than
    /// the weapon ladder, and a weapon-hand restriction says nothing about a
    /// shield. A shield marked "right hand only" is still the shield.
    ///
    /// Mutation executed: <c>the weapon hand-flag predicate was added to the
    /// automatic shield helper</c>. The shield is then refused and the
    /// offhand stays empty.
    /// </summary>
    [Fact]
    public void ShieldSelectionIgnoresWeaponHandRestrictions()
    {
        const uint primary = 100u;
        const uint shield = 300u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems =
            [
                Equipment(primary, "Primary", 0x0010),
                Equipment(shield, "Shield", 0, validLocations: 0x00200000u)
                    with { ObjectClass = PluginObjectClass.Armor },
            ],
        };
        var settings = SecondarySettings(primary, VtankSecondaryEquip.Auto);
        settings.CombatItemObjectIds.Add(shield);
        settings.CombatItemOrderIds.Add(shield);
        // Right hand only: meaningless for a shield, and it must be ignored.
        settings.ItemUseSpecifiers[shield] = 1;
        var controller = new CombatController(new FakeHost(surface), settings);

        DriveEquipPasses(controller);

        Assert.Contains("EquipSecondary:0000012C", surface.CallLog);
    }

    /// <summary>
    /// Preparing to cast is not the ranked weapon pick either: the first
    /// profiled wand is taken as it is. A wand a profile has marked "left
    /// hand only" is still the wand that gets held to cast.
    ///
    /// Mutation executed: <c>the weapon hand-flag predicate was added to the
    /// profiled-caster walk</c>. No wand is then found and the macro stops
    /// with the add-a-wand notice instead of equipping it.
    /// </summary>
    [Fact]
    public void CasterSelectionIgnoresWeaponHandRestrictions()
    {
        const uint wand = 990u;
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            EquipmentItems =
            [
                Equipment(wand, "Fixture Wand", 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(wand);
        settings.CombatItemOrderIds.Add(wand);
        settings.ItemUseSpecifiers[wand] = 2;
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
            WeaponToUseRaw = 0,
        }));
        var controller = new CombatController(new FakeHost(surface), settings);

        DriveEquipPasses(controller);

        Assert.Contains("Equip:000003DE", surface.CallLog);
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains("add at least one wand",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Mutation pin: swap the primary before the secondary for a thrown
    /// weapon. The actual requests must remove old primary, equip secondary,
    /// then equip thrown primary on separate passes.
    /// Mutation executed: <c>the thrown-secondary branch condition was replaced with false</c>.
    /// </summary>
    [Fact]
    public void GateThrownSecondaryPrecedesPrimaryAfterRemoval()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Peace },
            EquipmentItems =
            [
                Equipment(991u, "Old Sword", damageType: 1,
                    equippedLocation: 0x00100000u),
                Equipment(100u, "Thrown", damageType: 1,
                    itemType: 0x100u),
                Equipment(300u, "Shield", damageType: 0,
                    validLocations: 0x00200000u)
                    with { ObjectClass = PluginObjectClass.Armor },
            ],
        };
        CombatModeGate gate = Gate(surface);

        Assert.False(gate.TryPrepare(PluginCombatMode.Missile,
            overrideItemId: 100u, autoSelect: false, secondaryItemId: 300u));
        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Missile,
            overrideItemId: 100u, autoSelect: false, secondaryItemId: 300u));
        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Missile,
            overrideItemId: 100u, autoSelect: false, secondaryItemId: 300u));

        Assert.Equal(
            ["Move:000003DF", "EquipSecondary:0000012C", "Equip:00000064"],
            surface.CallLog);
    }

    [Fact]
    public void OncePerRunWarningLatchClearsAtTheMacroStartEdge()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Single(surface.PostedSystemMessages);

        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Single(surface.PostedSystemMessages);

        gate.ResetOncePerRunWarnings();
        gate.AdvancePass(0.1d);
        Assert.False(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Equal(2, surface.PostedSystemMessages.Count);
    }

    /// <summary>
    /// Mutation: restore the ValidLocations weapon mask in the override
    /// predicate; the owned object is spuriously warned away.
    /// </summary>
    [Fact]
    public void FcmOwnedObjectWithNoWeaponMaskIsAccepted()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            EquipmentItems =
            [
                Equipment(900, "Bread", damageType: 0, itemType: 1) with
                {
                    ValidLocations = 0u,
                },
                Equipment(800, "War Wand", damageType: 0, itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        CombatModeGate gate = Gate(surface);

        gate.AdvancePass(0.1d);
        gate.TryPrepare(PluginCombatMode.Magic, overrideItemId: 900u);
        Assert.Empty(surface.PostedSystemMessages);

        gate.AdvancePass(0.1d);
        gate.TryPrepare(PluginCombatMode.Magic, overrideItemId: 900u);
        Assert.Empty(surface.PostedSystemMessages);
    }

    [Fact]
    public void TheModeWindowIsTimeBasedAndTheAckRestartsIt()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(), // Melee
            DeferModeConfirmation = true,
            EquipmentItems =
            [
                Equipment(800, "Recovery Wand", damageType: 0, itemType: 0x00008000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        settings.CombatItemOrderIds.Add(800u);
        CombatModeGate gate = Gate(surface, settings);

        // Ask for Peace so the wand can be wielded.
        gate.AdvancePass(0.1d);
        Assert.False(gate.TryDropToPeace(surface.EquipmentItems, "Recovery Wand"));
        Assert.Equal(["EnterMode:Peace"], surface.CallLog);

        // The ack arrives. The window RESTARTS, so the gate still reports the
        // pre-request Melee and the branch re-asks — which re-stamps the
        // saved mode from the now-Peace live one.
        surface.ConfirmPendingModeChange();
        gate.AdvancePass(0.1d);
        Assert.False(gate.TryDropToPeace(surface.EquipmentItems, "Recovery Wand"));
        Assert.Equal(["EnterMode:Peace", "EnterMode:Peace"], surface.CallLog);

        gate.AdvancePass(0.1d);
        Assert.True(gate.TryDropToPeace(surface.EquipmentItems, "Recovery Wand"));
        Assert.Equal(["EnterMode:Peace", "EnterMode:Peace"], surface.CallLog);
    }

    [Fact]
    public void ThreeGateCallsWithinOnePassAdvanceTheClockExactlyOnce()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            EquipmentItems =
            [
                Equipment(
                    800,
                    "Recovery Wand",
                    damageType: 0,
                    itemType: 0x00008000u,
                    equippedLocation: 0x00100000u),
            ],
        };
        var settings = new CombatSettings();
        settings.CombatItemNames.Add("Recovery Wand");
        settings.CombatItemOrder.Add("Recovery Wand");
        settings.CombatItemOrderIds.Add(800u);
        CombatModeGate gate = Gate(surface, settings);

        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Empty(surface.CallLog);

        gate.AdvancePass(0.25);
        double afterOneAdvance = gate.SinceModeRequestSecondsForTests;

        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.True(gate.TryPrepare(PluginCombatMode.Magic));
        Assert.Empty(surface.CallLog);

        Assert.Equal(afterOneAdvance, gate.SinceModeRequestSecondsForTests);
    }


    private static MacroPassContext IdleTurn(bool canAct = true) =>
        new(0.3d, canAct);

    [Fact]
    public void IdlePeaceRequestsOnEveryPassItWins()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(), // Mode = Melee
            IgnoreModeChanges = true, // keep the rule valid across passes
        };
        var rule = new IdlePeaceRule(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = true });

        Assert.True(rule.ValidNow(IdleTurn()));
        rule.Running = true;
        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Contains("peace", rule.Status!, StringComparison.OrdinalIgnoreCase);

        Assert.True(rule.ValidNow(IdleTurn()));
        rule.Running = true;
        Assert.Equal(2, surface.ModeChangeRequests);
    }

    [Fact]
    public void IdlePeaceIsLastInTheListAndIsInertWhenAskedWithCanActFalse()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var rule = new IdlePeaceRule(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = true });

        IReadOnlyList<MacroRuleEntry> entries = MacroRuleTable.Entries;
        Assert.Equal(MacroRuleSlot.IdlePeace, entries[^1].Slot);
        Assert.DoesNotContain(
            entries.Take(entries.Count - 1),
            static entry => entry.Slot == MacroRuleSlot.IdlePeace);

        Assert.False(rule.ValidNow(IdleTurn(canAct: false)));

        rule.Running = false;
        Assert.Equal(0, surface.ModeChangeRequests);
        Assert.Null(rule.Status);
        Assert.Equal(PluginCombatMode.Melee, surface.CombatSnapshot.Mode);
    }

    [Fact]
    public void IdlePeaceModeOffNeverRequests()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var rule = new IdlePeaceRule(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = false });

        Assert.False(rule.ValidNow(IdleTurn()));
        Assert.Equal(0, surface.ModeChangeRequests);
        Assert.Null(rule.Status);
    }

    /// <summary>
    /// The rule has nothing to do when the character is already at peace.
    /// </summary>
    [Fact]
    public void IdlePeaceIsInvalidWhenAlreadyInPeace()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = new PluginCombatSnapshot { Mode = PluginCombatMode.Peace },
        };
        var rule = new IdlePeaceRule(
            new FakeHost(surface), new CombatSettings { IdlePeaceMode = true });

        Assert.Equal(PluginCombatMode.Peace, surface.CombatSnapshot.Mode);
        Assert.False(rule.ValidNow(IdleTurn()));
    }

    [Fact]
    public void IdlePeaceStillFiresWhenCombatPolicyIsDisabled()
    {
        var surface = new FakeAutomation { CombatSnapshot = Physical() };
        var settings = new CombatSettings { IdlePeaceMode = true, Enabled = false };
        var controller = new CombatController(new FakeHost(surface), settings);
        var rule = new IdlePeaceRule(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.False(controller.HasTarget);
        Assert.Contains("disabled", controller.Status, StringComparison.OrdinalIgnoreCase);

        Assert.True(rule.ValidNow(IdleTurn()));
        rule.Running = true;

        Assert.Equal(1, surface.ModeChangeRequests);
        Assert.Equal(PluginCombatMode.Peace, surface.CombatSnapshot.Mode);
    }

    /// <summary>
    /// Mutation: restore <c>RepeatAttackInProgress</c> (or any of the three
    /// request flags) to the target-refresh hold and this fails — the macro
    /// stays locked onto the drudge for the whole auto-repeat engagement.
    /// </summary>
    [Fact]
    public void AnAutoRepeatSwingDoesNotFreezeTargetSelection()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", distance: 3, angle: 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 20f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        settings.Rules.Insert(0, new MonsterRule("name#^Olthoi", 4));
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(10u, surface.LastBeginTarget);

        // The swing loop is running. It runs BESIDE the rule pass, so it must
        // not stop the pass re-picking.
        surface.CombatSnapshot = Physical() with
        {
            RepeatAttackInProgress = true,
        };
        surface.Targets =
        [
            Target(10, "Drudge", distance: 3, angle: 0),
            Target(20, "Olthoi Soldier", distance: 15, angle: 60),
        ];
        controller.OnTick(0.25);

        Assert.Contains(
            "Olthoi Soldier",
            controller.TargetText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Mutation: delete the <c>_suspendPass()</c> call in
    /// <c>HoldPassForTurn</c> and the first assertion fails; delete the
    /// <c>_resumePass()</c> call in <c>StopBreakableTurnMovement</c> and the
    /// last one does.
    /// </summary>
    [Fact]
    public void ATurnInFlightHoldsTheWholePassUntilTheCharacterHasFaced()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    RequiresTurnTo = true,
                },
            ],
            EquipmentItems = [WieldedCaster()],
            NavigationSnapshot = NavigationAt(heading: 0f),
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(0x7F7F0001u, 0.1d, 0d, 0d, 0f, true));
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseBreakableTurnTo = true }));
        int suspends = 0;
        int resumes = 0;
        controller.BindPassSuspension(() => suspends++, () => resumes++);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(1, suspends);
        Assert.Equal(0, resumes);
        Assert.Empty(surface.CastSpellIds);

        // The pass is frozen; the turn is driven beside it and does not raise
        // a second hold.
        controller.AdvanceHeldTurn(0.25);
        controller.AdvanceHeldTurn(0.25);
        Assert.Equal(1, suspends);
        Assert.Equal(0, resumes);

        surface.NavigationSnapshot = NavigationAt(heading: 90f);
        controller.AdvanceHeldTurn(0.25);

        Assert.Equal(1, resumes);

        controller.OnTick(0.25);
        Assert.Equal([100u], surface.CastSpellIds);
    }

    /// <summary>
    /// One wall clock: the frames a held turn advanced are not added a second
    /// time when the attack's turn comes back and is handed the whole gap.
    /// Mutation: add the turn's elapsed to the clock without subtracting what
    /// the frames added, and the final reading is 2.0.
    /// </summary>
    [Fact]
    public void FramesDrivenDuringAHeldTurnAreNotCountedTwiceOnTheNextTurn()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    RequiresTurnTo = true,
                },
            ],
            EquipmentItems = [WieldedCaster()],
            NavigationSnapshot = NavigationAt(heading: 0f),
        };
        surface.NavigationObjects[10u] = new PluginNavigationObject(
            10u,
            "Drudge",
            new PluginNavigationPosition(0x7F7F0001u, 0.1d, 0d, 0d, 0f, true));
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { UseBreakableTurnTo = true }));
        controller.BindPassSuspension(static () => { }, static () => { });

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(0.25, controller.ClockSeconds, 6);

        controller.AdvanceHeldTurn(0.25);
        controller.AdvanceHeldTurn(0.25);
        controller.AdvanceHeldTurn(0.25);
        Assert.Equal(1.0, controller.ClockSeconds, 6);

        // The turn comes back after the whole 1.0 s gap; 0.75 s of it the
        // frames already counted.
        surface.NavigationSnapshot = NavigationAt(heading: 90f);
        controller.OnTick(1.0);
        Assert.Equal(1.25, controller.ClockSeconds, 6);
    }

    /// <summary>
    /// A swing the host would not let out is the monster saying it is gone,
    /// whether or not anything has said so yet. The macro lets it go and puts
    /// the next one in front of the character instead of standing there for
    /// the unanswered-swing bound. Mutation: treat any answer but "released"
    /// as "carry on" and the next begin still names the monster that is dead.
    /// </summary>
    [Fact]
    public void ASwingThatNeverLeftTheClientLetsTheMonsterGoAndTakesTheNext()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { SelectedObjectId = 10u },
            Targets =
            [
                Target(10, "Drudge", distance: 2, angle: 0),
                Target(20, "Banderling", distance: 3, angle: 0),
            ],
            EquipmentItems = [WieldedPlannedWeapon()],
            TracksAttackRequests = true,
        };
        // The creature is dead on the server; nothing the macro can see says so
        // yet, and the host will not let a swing out at it.
        surface.UnreleasableTargets.Add(10u);
        var settings = new CombatSettings { ScanIntervalSeconds = 0.05d };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(10u, surface.LastBeginTarget);

        // The bar fills and the macro lets go of the swing.
        surface.CombatSnapshot = surface.CombatSnapshot with
        {
            PowerBarLevel = 1f,
            DesiredPower = 1f,
        };
        controller.OnTick(0.25);
        Assert.Equal(1, surface.ReleaseCount);

        // One pass past the shot floor the character is swinging at the one
        // still alive.
        controller.OnTick(1.0);
        Assert.Equal(20u, surface.LastBeginTarget);
    }

    /// <summary>
    /// The profile's power and height are what go out, wherever the player left
    /// the bar. Mutation: fall back to the host's bar setting for the power and
    /// a quarter-charged marker is what the character swings at.
    /// </summary>
    [Fact]
    public void TheProfilePowerAndHeightGoOutWhereverThePlayerLeftTheBar()
    {
        var surface = new FakeAutomation
        {
            // The player left the marker at a quarter.
            CombatSnapshot = Physical() with
            {
                SelectedObjectId = 10u,
                DesiredPower = 0.25f,
            },
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
        };
        var settings = new CombatSettings
        {
            ScanIntervalSeconds = 0.05d,
            AttackHeight = PluginAttackHeight.High,
            AutoAttackPower = true,
            UseRecklessness = false,
        };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.Equal(1f, surface.LastBeginPower, 3);
        Assert.Equal(PluginAttackHeight.High, surface.LastBeginHeight);
    }

    /// <summary>
    /// The host takes the selection away the instant the monster dies, which
    /// is what really happens on a live server, and the kill sentence lands
    /// after that. The sentence still belongs to the swing that was armed, so
    /// the monster is credited and the one beside it is swung at on the next
    /// pass. Mutation: gate the chat reader on the host's current selection
    /// again and the sentence is thrown away — the macro re-arms at the
    /// corpse and loses the seconds this whole arrangement exists to save.
    /// </summary>
    [Fact]
    public void AKillLineArrivingAfterTheHostDroppedTheSelectionStillCredits()
    {
        (FakeAutomation surface, CombatController controller, ActionLockTable locks) =
            MeleeKillRig();
        Assert.Equal(10u, surface.LastBeginTarget);
        surface.Targets =
        [
            Target(10, "Drudge", distance: 2, angle: 0),
            Target(20, "Mosswart", distance: 3, angle: 0),
        ];

        // The host answers the death first: the selection is gone before the
        // sentence is logged.
        surface.CombatSnapshot = surface.CombatSnapshot with
        {
            SelectedObjectId = 0u,
        };
        surface.ChatMessages = [ChatLine(1, "You killed Drudge!")];
        controller.OnTick(0.25);
        surface.ChatMessages = [];

        Assert.True(locks.IsLocked(ActionLockKind.Navigation));
        controller.OnTick(1.0);
        controller.OnTick(0.25);
        Assert.Equal(20u, surface.LastBeginTarget);
    }

    /// <summary>
    /// The host saying the creature is dead is enough on its own: no kill
    /// sentence, and no health ever asked for. Mutation: take the death out
    /// of the plugin's dead test and the corpse stays the target, because
    /// nothing else in the pass can tell it is a corpse.
    /// </summary>
    [Fact]
    public void TheHostsDeathFactAloneDropsTheTarget()
    {
        (FakeAutomation surface, CombatController controller, _) = MeleeKillRig();
        Assert.Equal(10u, surface.LastBeginTarget);

        surface.Targets =
        [
            // Health was never asked for, so it still reads as untouched.
            Target(10, "Drudge", distance: 2, angle: 0) with
            {
                IsHealthKnown = false,
                HealthFraction = 1f,
                IsDead = true,
            },
            Target(20, "Mosswart", distance: 3, angle: 0),
        ];
        controller.OnTick(1.0);
        controller.OnTick(0.25);

        Assert.Equal(20u, surface.LastBeginTarget);
    }

    /// <summary>
    /// A cleaving weapon fells the creature beside the one the swing was
    /// aimed at. The sentence names that creature, and it is that creature
    /// the macro gives up for dead — the swing at our own monster carries on.
    /// Mutation: restore the early return on a cleaving weapon and the
    /// bystander is left in the list as a live candidate, to be walked to and
    /// swung at once our own monster falls.
    /// </summary>
    [Fact]
    public void ACleaveKillSentenceEndsTheMonsterItNames()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { SelectedObjectId = 10u },
            Targets =
            [
                Target(10, "Drudge", distance: 2, angle: 0),
                Target(20, "Mosswart", distance: 2, angle: 20),
            ],
            EquipmentItems = [CleavingWieldedWeapon()],
        };
        var settings = new CombatSettings { ScanIntervalSeconds = 0.05d };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(10u, surface.LastBeginTarget);

        surface.ChatMessages = [ChatLine(1, "You killed Mosswart!")];
        controller.OnTick(0.25);
        surface.ChatMessages = [];

        // Our own monster is still the one being fought.
        Assert.True(controller.HasTarget);
        // And the one the cleave took is not picked up when it falls.
        surface.Targets = [Target(20, "Mosswart", distance: 2, angle: 20)];
        controller.OnTick(0.25);
        controller.OnTick(0.25);
        Assert.NotEqual(20u, surface.LastBeginTarget);
    }

    /// <summary>
    /// Two live monsters of one name, and a cleaving weapon: the sentence
    /// cannot say which of them fell, so neither is given up for dead. The
    /// host's own death fact settles it a moment later.
    /// </summary>
    [Fact]
    public void ACleaveSentenceNamingOneOfTwoAlikeMonstersCreditsNeither()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { SelectedObjectId = 10u },
            Targets =
            [
                Target(10, "Drudge", distance: 2, angle: 0),
                Target(20, "Mosswart", distance: 2, angle: 20),
                Target(30, "Mosswart", distance: 3, angle: 40),
            ],
            EquipmentItems = [CleavingWieldedWeapon()],
        };
        var settings = new CombatSettings { ScanIntervalSeconds = 0.05d };
        ProfileFixtureWeapon(settings);
        var controller = new CombatController(new FakeHost(surface), settings);
        controller.Toggle();
        controller.OnTick(0.25);

        surface.ChatMessages = [ChatLine(1, "You killed Mosswart!")];
        controller.OnTick(0.25);
        surface.ChatMessages = [];

        Assert.True(controller.HasTarget);
    }

    private static PluginEquipmentItem CleavingWieldedWeapon() =>
        WieldedPlannedWeapon() with { Cleaving = 2 };

    private static (FakeAutomation Surface, CombatController Controller, ActionLockTable Locks)
        MeleeKillRig(
            CombatSettings? settings = null,
            bool tracksAttackRequests = false)
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { SelectedObjectId = 10u },
            Targets = [Target(10, "Drudge", distance: 2, angle: 0)],
            EquipmentItems = [WieldedPlannedWeapon()],
            TracksAttackRequests = tracksAttackRequests,
        };
        CombatSettings resolved = settings ?? new CombatSettings();
        resolved.ScanIntervalSeconds = 0.05d;
        ProfileFixtureWeapon(resolved);
        var locks = new ActionLockTable();
        var controller = new CombatController(new FakeHost(surface), resolved);
        controller.BindActionLocks(locks, () => true);
        controller.Toggle();
        controller.OnTick(0.25);
        return (surface, controller, locks);
    }

    /// <summary>
    /// A line the client logged. <paramref name="logTextType"/> names the log
    /// it came from: 0 for a plain line, 0x16 for the character's own combat
    /// log, 0x07 for a spell result.
    /// </summary>
    private static PluginChatMessage ChatLine(
        ulong sequence,
        string text,
        uint logTextType = 0u) =>
        new(sequence, 0u, 0, string.Empty, text, string.Empty)
        {
            LogTextType = (int)logTextType,
        };

    /// <summary>
    /// Mutation: delete the <c>ObservePhysicalResultText</c> call from the
    /// chat walk and this fails — a swung-down monster stays the target until
    /// the world stops listing it, so the bot keeps hitting the corpse.
    /// </summary>
    [Fact]
    public void AMeleeKillLineEndsTheTargetAndHoldsNavigation()
    {
        (FakeAutomation surface, CombatController controller, ActionLockTable locks) =
            MeleeKillRig();
        Assert.Equal(10u, surface.LastBeginTarget);

        surface.ChatMessages = [ChatLine(1, "You killed Drudge!")];
        controller.OnTick(0.25);

        Assert.False(controller.HasTarget);
        Assert.True(locks.IsLocked(ActionLockKind.Navigation));
    }

    /// <summary>
    /// A swing outstanding at a monster that is dropped — killed here, and a
    /// deliberate switch is the same — is cancelled with it, so the next
    /// monster is swung at straight away instead of waiting out an answer
    /// that can never come, and the dead one is not charged the miss.
    /// Mutation: delete the swing teardown from <c>ClearTarget</c> and this
    /// fails — the server is still holding the dead monster's swing, the
    /// waiting arm claims every pass, and nothing is swung at the next
    /// monster until that wait runs out against the wrong name.
    /// </summary>
    [Fact]
    public void KillingTheTargetCancelsTheSwingThatWasOutstandingAtIt()
    {
        (FakeAutomation surface, CombatController controller, _) =
            MeleeKillRig(tracksAttackRequests: true);
        surface.Targets =
        [
            Target(10, "Drudge", distance: 2, angle: 0),
            Target(20, "Mosswart", distance: 3, angle: 0),
        ];
        surface.FillPowerBar();
        controller.OnTick(0.25);
        Assert.True(surface.CombatSnapshot.ServerResponsePending);
        int abortsWithTheSwingOut = surface.AbortCount;

        // The monster dies and the server never answers that last swing.
        surface.ChatMessages = [ChatLine(1, "You killed Drudge!")];
        controller.OnTick(0.25);
        surface.ChatMessages = [];
        Assert.Equal(abortsWithTheSwingOut + 1, surface.AbortCount);
        Assert.False(surface.CombatSnapshot.ServerResponsePending);

        controller.OnTick(0.25);
        controller.OnTick(0.25);
        Assert.Equal(20u, surface.LastBeginTarget);
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Cannot hit Drudge (",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The client is left holding an attack at a monster that has been
    /// dropped. Nothing is steering it, so it is ended and the next monster
    /// is swung at, instead of the pass waiting on it for good.
    /// </summary>
    [Fact]
    public void AnAttackLeftOverFromADroppedMonsterIsEndedNotWaitedOn()
    {
        (FakeAutomation surface, CombatController controller, _) =
            MeleeKillRig(tracksAttackRequests: true);
        surface.Targets = [Target(10, "Drudge", distance: 2, angle: 0)];
        controller.OnTick(0.25);
        Assert.Equal(10u, surface.LastBeginTarget);

        surface.ChatMessages = [ChatLine(1, "You killed Drudge!")];
        controller.OnTick(0.25);
        surface.ChatMessages = [];
        surface.Targets = [Target(20, "Mosswart", distance: 3, angle: 0)];
        // What the live client was seen to do: the attack goes on repeating.
        surface.CombatSnapshot = surface.CombatSnapshot with
        {
            RequestInProgress = false,
            BuildInProgress = false,
            RepeatAttackInProgress = true,
            SelectedObjectId = 20u,
        };

        var seen = new List<string>();
        for (int pass = 0; pass < 8; pass++)
        {
            controller.OnTick(0.25);
            seen.Add(controller.RunningDetail);
        }
        Assert.True(20u == surface.LastBeginTarget, string.Join(" | ", seen));
    }

    /// <summary>
    /// The kill lands while the NEXT swing at the same monster is still
    /// building on the power bar. The next monster is swung at all the same.
    /// </summary>
    [Fact]
    public void AKillWhileTheNextSwingIsStillChargingDoesNotEndTheFight()
    {
        (FakeAutomation surface, CombatController controller, _) =
            MeleeKillRig(tracksAttackRequests: true);
        surface.Targets =
        [
            Target(10, "Drudge", distance: 2, angle: 0),
            Target(20, "Mosswart", distance: 3, angle: 0),
        ];
        controller.OnTick(0.25);
        Assert.True(surface.CombatSnapshot.BuildInProgress);
        Assert.Equal(10u, surface.LastBeginTarget);

        surface.ChatMessages =
            [ChatLine(1, "Drudge catches your attack, with dire consequences!")];
        controller.OnTick(0.25);
        surface.ChatMessages = [];
        surface.Targets = [Target(20, "Mosswart", distance: 3, angle: 0)];

        for (int pass = 0; pass < 12; pass++)
            controller.OnTick(0.25);
        Assert.Equal(20u, surface.LastBeginTarget);
    }

    /// <summary>
    /// The reader keys on which of the client's logs a line came from, not on
    /// its words: a player typing the kill sentence, or the damage sentence,
    /// in chat must not end the fight or clear the give-up count.
    /// Mutation: drop either log-type test in the physical result reader and
    /// the matching half fails.
    /// </summary>
    [Fact]
    public void SomebodyTypingTheKillSentenceInChatChangesNothing()
    {
        (FakeAutomation surface, CombatController controller, ActionLockTable locks) =
            MeleeKillRig();

        // Speech carries the local-speech log type, not the plain one.
        surface.ChatMessages =
        [
            ChatLine(1, "You killed Drudge!", logTextType: 0x02u),
        ];
        controller.OnTick(0.25);

        Assert.True(controller.HasTarget);
        Assert.False(locks.IsLocked(ActionLockKind.Navigation));

        surface.ChatMessages = [ChatLine(2, "You killed Drudge!")];
        controller.OnTick(0.25);

        Assert.False(controller.HasTarget);
    }

    /// <summary>
    /// Mutation: drop the slain-name comparison and this fails — a fellow's
    /// kill of something else would end our own target.
    /// </summary>
    [Fact]
    public void AKillLineNamingAnotherCreatureDoesNotEndOurTarget()
    {
        (FakeAutomation surface, CombatController controller, _) = MeleeKillRig();

        surface.ChatMessages = [ChatLine(1, "You killed Mosswart!")];
        controller.OnTick(0.25);

        Assert.True(controller.HasTarget);
        Assert.Contains("Drudge", controller.TargetText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mutation: feed the give-up counter from anything other than the
    /// shot-hit-the-world line — for instance from every completed swing whose
    /// target health did not move — and the second half of this fails, because
    /// an ordinary miss would count.
    /// </summary>
    [Fact]
    public void OnlyAShotIntoTheSceneryCountsTowardsGivingUpOnAMonster()
    {
        (FakeAutomation surface, CombatController controller, _) = MeleeKillRig(
            new CombatSettings
            {
                BlacklistMonsterAttemptCount = 1,
                BlacklistMonsterTimeoutSeconds = 300,
            });

        surface.ChatMessages =
        [
            ChatLine(1, "Your missile attack hit the environment."),
            ChatLine(2, "Your missile attack hit the environment."),
        ];
        controller.OnTick(0.25);
        Assert.False(controller.HasTarget);

        (surface, controller, _) = MeleeKillRig(new CombatSettings
        {
            BlacklistMonsterAttemptCount = 1,
            BlacklistMonsterTimeoutSeconds = 300,
        });
        surface.ChatMessages =
        [
            ChatLine(1, "You evade the Drudge!"),
            ChatLine(2, "The Drudge evades your attack!"),
            ChatLine(3, "You miss the Drudge!"),
        ];
        controller.OnTick(0.25);
        Assert.True(controller.HasTarget);
    }

    /// <summary>
    /// Mutation: delete the damage-report arm and this fails — the two shots
    /// into the scenery either side of a landed hit would add up and retire a
    /// monster the character is demonstrably hitting.
    /// </summary>
    [Fact]
    public void ALandedHitStartsTheGiveUpCountOver()
    {
        (FakeAutomation surface, CombatController controller, _) = MeleeKillRig(
            new CombatSettings
            {
                BlacklistMonsterAttemptCount = 1,
                BlacklistMonsterTimeoutSeconds = 300,
            });

        surface.ChatMessages =
        [
            ChatLine(1, "Your missile attack hit the environment."),
            ChatLine(
                2,
                "You slash Drudge for 43 points of slashing damage!",
                logTextType: 0x16u),
            ChatLine(3, "Your missile attack hit the environment."),
        ];
        controller.OnTick(0.25);

        Assert.True(controller.HasTarget);
    }

    /// <summary>
    /// A swing that never leaves the client is torn down too. The host holds
    /// the request open with the power bar stopped while the character is in
    /// no position to attack; if that never clears, nothing is sent, there is
    /// no answer to wait for, and no second swing is allowed either. The
    /// monster is not charged a miss for it — nothing reached it.
    /// Mutation: delete the <c>GiveUpOnSwingThatNeverWentOut</c> call from
    /// the request arm of <c>TickPhysical</c> and this fails — the macro
    /// stands there charging a bar that is not moving and never asks again.
    /// </summary>
    [Fact]
    public void ASwingWhoseBarNeverMovesIsTornDownAndAskedForAgain()
    {
        (FakeAutomation surface, CombatController controller, _) = MeleeKillRig(
            new CombatSettings
            {
                BlacklistMonsterAttemptCount = 1,
                BlacklistMonsterTimeoutSeconds = 300,
            },
            tracksAttackRequests: true);
        Assert.Equal(1, surface.BeginCount);
        Assert.True(surface.CombatSnapshot.RequestInProgress);
        int abortsAfterTheArm = surface.AbortCount;

        // The bar stays where it was: the character is in no position to
        // swing and the host is holding the request open.
        controller.OnTick(4.0);
        Assert.Equal(abortsAfterTheArm, surface.AbortCount);
        Assert.Equal(1, surface.BeginCount);

        controller.OnTick(1.0);
        Assert.Equal(abortsAfterTheArm + 1, surface.AbortCount);
        Assert.False(surface.CombatSnapshot.RequestInProgress);

        // The next pass asks again, and the monster has been charged nothing
        // for a swing that never reached it.
        controller.OnTick(0.25);
        Assert.Equal(2, surface.BeginCount);
        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Cannot hit",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A swing the server never answers must not hold the macro for ever: the
    /// host refuses a second swing while the first is open, so the wait ends
    /// itself and the attack is cancelled, which is what closes it server-side.
    /// The whole press → charge → release path is travelled here, and the
    /// wait is measured from the release: the charge is the macro's own time,
    /// not the server's.
    /// Mutation: delete the <c>GiveUpOnUnansweredSwing</c> call from the
    /// waiting arm of <c>TickPhysical</c> and the last assertion fails — the
    /// macro waits on that one swing until something unrelated interrupts it.
    /// Mutation: take the wait's clock from the press instead of the release
    /// and the middle assertion fails — the two seconds spent charging come
    /// off the wait and an answer four seconds after the send arrives too
    /// late.
    /// </summary>
    [Fact]
    public void ASwingThatIsNeverAnsweredIsCancelledOnceTheWaitRunsOut()
    {
        (FakeAutomation surface, CombatController controller, _) =
            MeleeKillRig(tracksAttackRequests: true);
        Assert.Equal(10u, surface.LastBeginTarget);
        Assert.True(surface.CombatSnapshot.BuildInProgress);
        int abortsAfterTheSwing = surface.AbortCount;

        // Two seconds on the power bar before the swing is let go.
        controller.OnTick(2.0);
        Assert.Equal(0, surface.ReleaseCount);

        surface.FillPowerBar();
        controller.OnTick(0.25);
        Assert.Equal(1, surface.ReleaseCount);
        Assert.True(surface.CombatSnapshot.ServerResponsePending);

        // Four seconds of silence after the send is still inside the wait,
        // however long the charge before it took.
        controller.OnTick(4.0);
        Assert.Equal(abortsAfterTheSwing, surface.AbortCount);

        controller.OnTick(0.75);
        Assert.Equal(abortsAfterTheSwing + 1, surface.AbortCount);
    }

    /// <summary>
    /// The server walks the character in to a monster the swing cannot yet
    /// reach and strikes on arrival, which from a distance with a slow weapon
    /// outlasts the whole wait. While the ground is demonstrably being gained
    /// the wait starts over; once it stops being gained the wait runs out as
    /// usual.
    /// Mutation: delete the closing-distance arm of the give-up and the first
    /// assertion fails — a swing the server is still running in for is
    /// cancelled and the monster charged a miss it never had a chance at.
    /// </summary>
    [Fact]
    public void ASwingIsNotCutShortWhileTheServerIsStillClosingTheGround()
    {
        (FakeAutomation surface, CombatController controller, _) =
            MeleeKillRig(tracksAttackRequests: true);
        surface.FillPowerBar();
        controller.OnTick(0.25);
        Assert.Equal(1, surface.ReleaseCount);
        int abortsAfterTheSwing = surface.AbortCount;

        // The range the swing went out at is the mark to measure from.
        controller.OnTick(4.0);

        // Most of a metre nearer: the run-in is working.
        surface.Targets = [Target(10, "Drudge", distance: 1.2f, angle: 0)];
        controller.OnTick(1.0);
        Assert.Equal(abortsAfterTheSwing, surface.AbortCount);

        // No more ground gained, and now the wait runs out.
        controller.OnTick(4.0);
        Assert.Equal(abortsAfterTheSwing, surface.AbortCount);
        controller.OnTick(1.0);
        Assert.Equal(abortsAfterTheSwing + 1, surface.AbortCount);
    }

    /// <summary>
    /// A charged swing goes the moment its bar is full, on the host's own
    /// frame. It used to wait for the attack to win another rule pass, which
    /// cost a fraction of a second on every swing of a whole session.
    /// Mutation: take the release out of the executor and put it back on the
    /// pass and this fails — a frame with no pass in it lets nothing go.
    /// </summary>
    [Fact]
    public void AChargedSwingIsLetGoOnTheHostFrameWithoutWaitingForAPass()
    {
        (FakeAutomation surface, CombatController controller, _) =
            MeleeKillRig(tracksAttackRequests: true);
        Assert.Equal(1, surface.BeginCount);
        Assert.Equal(0, surface.ReleaseCount);

        surface.FillPowerBar();
        // One host frame, and not a single rule pass.
        controller.DriveSwingExecutor(0.015d);

        Assert.Equal(1, surface.ReleaseCount);
    }

    /// <summary>
    /// The next swing is asked for on the executor's own period and behind its
    /// own floor, neither of which is the rule pass's business. The floor is
    /// what stops the executor pressing into the animation of the swing it
    /// just asked for; the period is how often it looks.
    /// Mutation: drop the shot floor and the first assertion fails — a second
    /// swing is asked for a quarter of a second after the first. Mutation:
    /// ask for the swing from the pass instead of the executor and the second
    /// fails — these frames contain no pass at all.
    /// </summary>
    [Fact]
    public void TheNextSwingIsAskedForOnTheExecutorsOwnPeriod()
    {
        (FakeAutomation surface, CombatController controller, _) =
            MeleeKillRig(tracksAttackRequests: true);
        surface.FillPowerBar();
        controller.DriveSwingExecutor(0.015d);
        Assert.Equal(1, surface.ReleaseCount);

        // The server answers and the swing is over: nothing is in flight.
        surface.CombatSnapshot = surface.CombatSnapshot with
        {
            ServerResponsePending = false,
            PowerBarLevel = 0f,
        };

        // Half a second of frames is inside the floor the swing put up.
        for (int frame = 0; frame < 33; frame++)
            controller.DriveSwingExecutor(0.015d);
        Assert.Equal(1, surface.BeginCount);

        // Past the floor, still without a rule pass, the next swing goes out.
        for (int frame = 0; frame < 34; frame++)
            controller.DriveSwingExecutor(0.015d);
        Assert.Equal(2, surface.BeginCount);
    }

    /// <summary>
    /// The attack losing its turn calls the swing off, and holds the shot slot
    /// for longer than a swing does: the character has to come out of what it
    /// was doing before it can be asked for anything else.
    /// Mutation: drop the stop floor and the second assertion fails — the very
    /// next pass asks for a swing into the tail of the one just cancelled.
    /// </summary>
    [Fact]
    public void LosingTheTurnCallsTheSwingOffAndHoldsTheShotSlot()
    {
        (FakeAutomation surface, CombatController controller, ActionLockTable locks) =
            MeleeKillRig(tracksAttackRequests: true);
        int abortsBefore = surface.AbortCount;

        controller.SetPaused(true);

        Assert.Equal(abortsBefore + 1, surface.AbortCount);
        Assert.True(locks.IsLocked(ActionLockKind.MeleeAttackShot));
    }

    /// <summary>
    /// A swing nobody answers is not a miss. Two things are evidence that a
    /// monster cannot be reached — the client saying the shot flew into the
    /// scenery, and a cast whose result never comes — and a swing left
    /// hanging is neither: the silence is on this side of the wire, so it
    /// says nothing about the monster. The wait still ends and the swing is
    /// still cancelled; the monster is simply charged nothing for it, and is
    /// still there to be swung at on the next pass.
    /// Mutation: charge an attempt in the give-up and this fails — a stall
    /// of our own puts a perfectly hittable monster out of play for the whole
    /// blacklist window.
    /// </summary>
    [Fact]
    public void AnUnansweredSwingCostsTheMonsterNothing()
    {
        (FakeAutomation surface, CombatController controller, _) = MeleeKillRig(
            new CombatSettings
            {
                BlacklistMonsterAttemptCount = 1,
                BlacklistMonsterTimeoutSeconds = 300,
            });
        surface.Targets =
        [
            Target(10, "Drudge", distance: 2, angle: 0),
            Target(20, "Mosswart", distance: 3, angle: 0),
        ];
        surface.CombatSnapshot = surface.CombatSnapshot with
        {
            ServerResponsePending = true,
        };

        // The first pass registers the swing as sent; after that one wait
        // runs out per pass. However many run out, none of them is a miss.
        controller.OnTick(5.0);
        int abortsBefore = surface.AbortCount;
        controller.OnTick(5.0);
        controller.OnTick(5.0);
        controller.OnTick(5.0);

        Assert.True(surface.AbortCount > abortsBefore);
        Assert.DoesNotContain(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Cannot hit",
                StringComparison.Ordinal));
        Assert.Contains("Drudge", controller.TargetText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Giving a monster up is a timeout, not a verdict: once the profile's
    /// window has passed the same monster is a target again.
    /// Mutation: make the suppression permanent and this fails.
    /// </summary>
    [Fact]
    public void ARetiredTargetIsTakenUpAgainOnceItsWindowHasPassed()
    {
        (FakeAutomation surface, CombatController controller, _) = MeleeKillRig(
            new CombatSettings
            {
                BlacklistMonsterAttemptCount = 1,
                BlacklistMonsterTimeoutSeconds = 5,
            });

        surface.ChatMessages =
        [
            ChatLine(1, "Your missile attack hit the environment."),
            ChatLine(2, "Your missile attack hit the environment."),
        ];
        controller.OnTick(0.25);
        surface.ChatMessages = [];
        Assert.DoesNotContain("Drudge", controller.TargetText, StringComparison.Ordinal);

        controller.OnTick(6.0);
        Assert.Contains("Drudge", controller.TargetText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mutation: take the retry out of the pass (return instead of choosing
    /// again after an undeliverable decision) and this fails — the macro
    /// stands there staring at the monster behind cover while a reachable one
    /// is next to it.
    /// </summary>
    [Fact]
    public void AMonsterBehindCoverIsPassedOverForAReachableOneInTheSamePass()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets =
            [
                Target(10, "Drudge", distance: 3, angle: 0),
                Target(20, "Mosswart", distance: 6, angle: 10),
            ],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            EquipmentItems = [WieldedCaster()],
        };
        // The near one cannot be reached; the far one can.
        surface.ProjectilePaths[10u] = new(
            PluginProjectilePathStatus.Blocked,
            CollisionChecks: 3,
            BlockingObjectId: 0x50000001u);
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            SelectionMethod = TargetSelectionMethod.Range,
            UseProjectileAwareness = true,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 20u), surface.LastTargetedCast);
    }

    /// <summary>
    /// The walk is its own pass. What the attack's pass learned and turned
    /// off belongs to that pass and must not narrow the walk's choice of
    /// monster: here the attack turns the near monster's attack column off
    /// because nothing can be thrown at it, and the walk must still see it —
    /// see it, and stop, because it is already inside weapon range.
    /// Mutation: delete <c>ClearPassMemos()</c> from the head of
    /// <c>CombatController.TickMonsterApproach</c> and both assertions fail —
    /// the near monster is still excluded by the attack's cleared column, so
    /// the walk picks the far one and sets off towards it.
    /// </summary>
    [Fact]
    public void TheWalkDoesNotInheritTheColumnTheAttackTurnedOff()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets =
            [
                Target(10, "Drudge", distance: 4, angle: 0),
                Target(20, "Mosswart", distance: 15, angle: 10),
            ],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            EquipmentItems = [WieldedCaster()],
            NavigationSnapshot = NavigationAt(heading: 0f),
        };
        // Nothing can be thrown at the near one.
        surface.ProjectilePaths[10u] = new(
            PluginProjectilePathStatus.Blocked,
            CollisionChecks: 3,
            BlockingObjectId: 0x50000001u);
        // Somewhere for the walk to go if it wrongly picks the far one.
        surface.NavigationObjects[20u] = new PluginNavigationObject(
            20u,
            "Mosswart",
            new PluginNavigationPosition(0x7F7F0001, 0.1d, 0d, 0d, 0f, true));
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 5d,
            ApproachDistance = 20d,
            SelectionMethod = TargetSelectionMethod.Range,
            UseProjectileAwareness = true,
            ScanIntervalSeconds = 0.05d,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        // The attack pass ran and found nothing it could do.
        Assert.Equal((0u, 0u), surface.LastTargetedCast);

        // Same pass, the walk's turn. The near monster is back in the running
        // and it is already close enough, so there is nowhere to walk.
        Assert.False(controller.TickMonsterApproach(canAct: true));
        Assert.Empty(surface.MovementIntents);
    }

    /// <summary>
    /// Mutation: make the retry unbounded (drop the budget) and a rule whose
    /// remaining column keeps failing would spin forever; make the budget the
    /// per-path sample cap again and this test's single blocked monster would
    /// cost 500 rebuilds. Either way the count below moves.
    /// </summary>
    [Fact]
    public void TheRetryStopsAsSoonAsNothingIsLeftToChooseFrom()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", distance: 3, angle: 0)],
            KnownAttackSpells =
            [
                Spell(100, "Incantation of Flame Bolt") with
                {
                    IsProjectile = true,
                },
            ],
            ProjectilePath = new(PluginProjectilePathStatus.Blocked),
            EquipmentItems = [WieldedCaster()],
        };
        var settings = FireAttackRule(new CombatSettings
        {
            MaximumRange = 40d,
            UseProjectileAwareness = true,
        });
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Empty(surface.CastSpellIds);
        Assert.False(controller.HasTarget);
        // Three shapes tried against the one monster, then it is out of the
        // running and there is nothing left: no spinning.
        Assert.InRange(surface.ProjectilePathChecks, 1, 8);
    }

    /// <summary>
    /// Mutation: drop the component term from <c>IsUsableAttackSpell</c> and
    /// this fails — the pick lands on the best tier known, the client refuses
    /// the cast for want of components, and every pass picks it again.
    /// </summary>
    [Fact]
    public void ATierThePackCannotPayForIsNotPicked()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Flame Bolt VII", difficulty: 300),
                MagicSpell(101, "Flame Bolt IV", difficulty: 150),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        surface.MissingComponentSpellIds.Add(100u);
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { MaximumRange = 40d }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((101u, 10u), surface.LastTargetedCast);
    }

    /// <summary>
    /// Mutation: same as above — with EVERY tier unpayable the arm must fall
    /// through to the "no usable attack spell" warning instead of casting.
    /// </summary>
    [Fact]
    public void NoTierIsPickedWhenThePackHasNoComponentsAtAll()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems = [WieldedCaster()],
        };
        surface.MissingComponentSpellIds.Add(100u);
        var controller = new CombatController(
            new FakeHost(surface),
            FireAttackRule(new CombatSettings { MaximumRange = 40d }));

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Empty(surface.CastSpellIds);
    }

    /// <summary>
    /// Mutation: classify from the numbers again (ammunition type, damage,
    /// weapon skill) and this fails — a thrown weapon takes no ammunition and
    /// an unarmed weapon lists no damage, so both would be mis-stanced.
    /// Mutation: derive caster and stance from ItemType; Unknown then
    /// masquerades as a caster instead of only receiving the default stance.
    /// </summary>
    [Fact]
    public void AWeaponsStanceComesFromItsClassNotItsNumbers()
    {
        PluginEquipmentItem thrown = Equipment(
            1u,
            "Throwing Dagger",
            damageType: 0x0002,
            itemType: 0x100,
            ammoType: 0);
        PluginEquipmentItem fists = Equipment(
            2u,
            "Training Wraps",
            damageType: 0x0001,
            damage: 0,
            itemType: 0x1) with
        {
            WeaponSkill = 0,
        };

        Assert.Equal(PluginCombatMode.Missile, CombatModeGate.ModeFor(in thrown));
        Assert.Equal(PluginCombatMode.Melee, CombatModeGate.ModeFor(in fists));
        PluginEquipmentItem unknown = thrown with
        {
            ObjectClass = PluginObjectClass.Unknown,
        };
        Assert.Equal(PluginCombatMode.Magic, CombatModeGate.ModeFor(in unknown));
        Assert.False(CombatModeGate.IsCaster(in unknown));
    }

    /// <summary>
    /// Mutation: roll the random element inside the magic arm again and this
    /// fails — a pass that only casts a debuff would leave the cursor where it
    /// was, and the vulnerability would be for last pass's element.
    /// </summary>
    [Fact]
    public void ARolledElementAdvancesEvenOnAPassThatOnlyDebuffs()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                Debuff(70, "Piercing Vulnerability Other VII"),
                Debuff(71, "Bludgeoning Vulnerability Other VII"),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack
                    | MonsterActionFlags.Vulnerability,
                DamageType = MonsterDamageType.Random,
                ExtraVulnerability = MonsterDamageType.None,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(70u, surface.LastTargetedCast.Item1);

        // The first debuff is still in flight, so let it finish.
        surface.LastCastCompletion = new PluginCastCompletion(1, 70, 10, 0);
        controller.OnTick(0.25);
        controller.OnTick(0.25);

        Assert.Equal(71u, surface.LastTargetedCast.Item1);
    }

    /// <summary>
    /// Mutation: auto-select for a zero weapon column again and this fails —
    /// the rule means "no weapon, use a wand", and a melee weapon would be
    /// wielded instead.
    /// </summary>
    [Fact]
    public void AZeroWeaponColumnMeansAWandAndSelectsNothing()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems =
            [
                Equipment(
                    990u,
                    "Fixture Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
                Equipment(991u, "Fire Sword", damageType: 0x0010),
            ],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.CombatItemNames.Add("Fixture Wand");
        settings.CombatItemNames.Add("Fire Sword");
        settings.CombatItemOrderIds.Add(990u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponToUseRaw = 0,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(990u, surface.LastEquipObjectId);
    }

    /// <summary>
    /// The first wand on the Items page wins, whatever it is called. The page
    /// puts the Zephyr Wand first even though the host hands the two of them
    /// back the other way round — the host's list is alphabetical among
    /// unheld items, so this only says anything at all because the two orders
    /// disagree.
    ///
    /// Mutation: drop the page walk from <c>FindFirstProfiledWand</c> and this
    /// fails — the Acid Wand comes back instead.
    /// </summary>
    [Fact]
    public void TheFirstProfiledWandWinsWithoutAnAlphabeticalTieBreak()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [Debuff(83, "Imperil Other VII")],
            EquipmentItems =
            [
                Equipment(
                    990u,
                    "Zephyr Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
                Equipment(
                    991u,
                    "Acid Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
            ],
        };
        var settings = DebuffOnly(MonsterActionFlags.Imperil);
        settings.MaximumRange = 40d;
        settings.CombatItemNames.Add("Zephyr Wand");
        settings.CombatItemNames.Add("Acid Wand");
        settings.CombatItemOrder.Add("Zephyr Wand");
        settings.CombatItemOrder.Add("Acid Wand");
        settings.CombatItemOrderIds.Add(990u);
        settings.CombatItemOrderIds.Add(991u);
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal(990u, surface.LastEquipObjectId);
    }

    /// <summary>
    /// Mutation: walk the tiers for a rolled element and this fails — the
    /// rolled arm names its spell outright, and what it names is the first
    /// rung of the family.
    /// </summary>
    [Fact]
    public void ARolledElementThrowsTheFirstRungOfItsWarFamily()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical() with { Mode = PluginCombatMode.Magic },
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells =
            [
                MagicSpell(100, "Force Bolt I", difficulty: 50),
                MagicSpell(101, "Force Bolt VII", difficulty: 300),
            ],
            EquipmentItems = [WieldedCaster()],
        };
        var settings = new CombatSettings { MaximumRange = 40d };
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Random,
            }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
    }

    /// <summary>
    /// Mutation: drop the swing teardown from the cast and this fails — a
    /// physical attack armed a moment ago would keep running underneath the
    /// spell.
    /// </summary>
    [Fact]
    public void ACastTearsDownAnArmedSwingFirst()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            Targets = [Target(10, "Drudge", 5, 0)],
            KnownCombatSpells = [MagicSpell(100, "Flame Bolt VII", difficulty: 300)],
            EquipmentItems =
            [
                Equipment(
                    990u,
                    "Fixture Wand",
                    damageType: 0,
                    itemType: 0x00008000u),
                WieldedPlannedWeapon(),
            ],
        };
        var settings = FireAttackRule(new CombatSettings { MaximumRange = 40d });
        ProfileFixtureWeapon(settings);
        settings.CombatItemNames.Add("Fixture Wand");
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25);
        Assert.Equal(1, surface.BeginCount);
        int abortsAfterTheSwing = surface.AbortCount;

        // The rule flips to magic; the swing must be torn down as the cast
        // goes out.
        surface.CombatSnapshot = surface.CombatSnapshot with
        {
            Mode = PluginCombatMode.Magic,
        };
        surface.EquipmentItems =
        [
            Equipment(
                990u,
                "Fixture Wand",
                damageType: 0,
                itemType: 0x00008000u,
                equippedLocation: 0x00100000u),
        ];
        surface.EmitPlacement(new PluginEquipmentObservation(991u, 0u, true));
        surface.EmitPlacement(new PluginEquipmentObservation(
            990u, 0x00100000u, false));
        settings.Rules[0] = new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
                WeaponName = "Fixture Wand",
            });
        controller.OnTick(0.25);

        Assert.Equal((100u, 10u), surface.LastTargetedCast);
        Assert.True(surface.AbortCount > abortsAfterTheSwing);
    }

    private static CombatSettings FireAttackRule(CombatSettings settings)
    {
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions
            {
                Flags = MonsterActionFlags.Attack,
                DamageType = MonsterDamageType.Fire,
            }));
        return settings;
    }

    /// <summary>
    /// Mutation: delete the <c>ArmPostKillNavigationLock()</c> call from the
    /// kill arm of the cast outcome and this fails — the bot walks off the
    /// corpse it just made instead of standing still for the looting window.
    /// </summary>
    [Fact]
    public void ASpellKillHoldsNavigationForThreeSecondsWhileLootingIsOn()
    {
        var tracker = new SpellCastTracker();
        var locks = new ActionLockTable();
        var controller = new CombatController(
            new FakeHost(new FakeAutomation()),
            new CombatSettings(),
            castTracker: tracker);
        controller.BindActionLocks(locks, () => true);

        tracker.Begin(
            1u,
            "Flame Bolt VII",
            30u,
            "Drudge",
            false,
            saying: "testwords",
            school: SpellCastTracker.WarMagicSchool,
            canKill: true);
        tracker.ObserveChat(0uL, "testwords", ownSpeech: true, logTextType: 0x11u);
        tracker.ObserveChat(1uL, "You killed Drudge!");

        Assert.True(locks.IsLocked(ActionLockKind.Navigation));

        locks.Advance(2.9d);
        Assert.True(locks.IsLocked(ActionLockKind.Navigation));

        locks.Advance(0.2d);
        Assert.False(locks.IsLocked(ActionLockKind.Navigation));
    }

    /// <summary>
    /// Mutation: drop the looting term from <c>ArmPostKillNavigationLock</c>
    /// and this fails — a bot that never loots would stand still after a kill.
    /// </summary>
    [Fact]
    public void ASpellKillDoesNotHoldNavigationWhileLootingIsOff()
    {
        var tracker = new SpellCastTracker();
        var locks = new ActionLockTable();
        var controller = new CombatController(
            new FakeHost(new FakeAutomation()),
            new CombatSettings(),
            castTracker: tracker);
        controller.BindActionLocks(locks, () => false);

        tracker.Begin(
            1u,
            "Flame Bolt VII",
            30u,
            "Drudge",
            false,
            saying: "testwords",
            school: SpellCastTracker.WarMagicSchool,
            canKill: true);
        tracker.ObserveChat(0uL, "testwords", ownSpeech: true, logTextType: 0x11u);
        tracker.ObserveChat(1uL, "You killed Drudge!");

        Assert.False(locks.IsLocked(ActionLockKind.Navigation));
    }

    /// <summary>
    /// Rare-only looting looks at no corpse until this character's rare is
    /// announced, so a kill does not stand the bot still. Mutation: drop the
    /// rare-only term from <c>LootSettings.HoldsRouteAfterKill</c> and this
    /// fails.
    /// </summary>
    [Fact]
    public void ASpellKillDoesNotHoldNavigationWhileLootingOnlyRares()
    {
        var loot = new LootSettings { Enabled = true, LootOnlyRareCorpses = true };
        var tracker = new SpellCastTracker();
        var locks = new ActionLockTable();
        var controller = new CombatController(
            new FakeHost(new FakeAutomation()),
            new CombatSettings(),
            castTracker: tracker);
        controller.BindActionLocks(locks, () => loot.HoldsRouteAfterKill);

        tracker.Begin(
            1u,
            "Flame Bolt VII",
            30u,
            "Drudge",
            false,
            saying: "testwords",
            school: SpellCastTracker.WarMagicSchool,
            canKill: true);
        tracker.ObserveChat(0uL, "testwords", ownSpeech: true, logTextType: 0x11u);
        tracker.ObserveChat(1uL, "You killed Drudge!");

        Assert.False(locks.IsLocked(ActionLockKind.Navigation));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void TheKillHoldFollowsLootingAndRareOnly(
        bool enabled, bool rareOnly, bool holds)
    {
        var loot = new LootSettings { Enabled = enabled, LootOnlyRareCorpses = rareOnly };
        Assert.Equal(holds, loot.HoldsRouteAfterKill);
    }

    /// <summary>
    /// Equipping is a several-pass errand: one request goes out per pass, and
    /// the character's hands only settle once the pass clock moves. Calling
    /// the step over and over without advancing that clock leaves the swap
    /// cooldown armed, so nothing past the first request is ever sent. The
    /// host advances the same clock between passes.
    /// </summary>
    /// <returns>True once the gate reports the character ready.</returns>
    private static bool DriveEquipPasses(
        CombatController controller, int passes = 12)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            if (controller.EquipOneStepForMonster("Fixture"))
                return true;
            controller.Gate.AdvancePass(0.1d);
        }
        return false;
    }

    private static CombatSettings SecondarySettings(
        uint primary, VtankSecondaryEquip secondary)
    {
        var settings = new CombatSettings();
        // A rule may only name a weapon the Items page carries.
        settings.CombatItemObjectIds.Add(primary);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule("DEFAULT", new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack,
            DamageType = MonsterDamageType.Fire,
            WeaponObjectId = primary,
            SecondaryEquipRaw = (int)secondary,
        }));
        return settings;
    }

    private static IReadOnlyList<PluginSkillInfo> SecondarySkills(
        bool shieldTrained, bool dualWieldTrained)
    {
        var skills = new List<PluginSkillInfo>();
        if (shieldTrained)
            skills.Add(new PluginSkillInfo(48u, "Shield", PluginSkillTraining.Trained, 300u));
        if (dualWieldTrained)
            skills.Add(new PluginSkillInfo(49u, "Dual Wield", PluginSkillTraining.Trained, 300u));
        return skills;
    }

    private static CombatSettings DebuffOnly(MonsterActionFlags flag)
    {
        var settings = new CombatSettings();
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = flag }));
        return settings;
    }

    private static PluginCombatSnapshot Peaceful() => Physical() with
    {
        Mode = PluginCombatMode.Peace,
    };

    private static PluginCombatSnapshot Physical(
        bool request = false,
        bool build = false,
        float bar = 0f) => new(
            SelectedObjectId: 0,
            PluginCombatMode.Melee,
            PluginAttackHeight.Medium,
            DesiredPower: 0.5f,
            PowerBarLevel: bar,
            BuildInProgress: build,
            RequestInProgress: request,
            ServerResponsePending: false,
            RepeatAttackInProgress: false);

    /// <summary>
    /// A game database in the format of <c>gameinfodb.ugd</c>, every row of it
    /// written for the tests. Any pin whose subject is a monster's damage
    /// preferences needs one, because the built-in database is empty.
    /// </summary>
    private static readonly VtankGameInfoDatabase AmmoGameInfo =
        CreateAmmoGameInfo();

    private static VtankGameInfoDatabase CreateAmmoGameInfo()
    {
        var document = VtankDatabase.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vtank",
                "gameinfodb-excerpt.ugd")));
        VtankTable ammo = document.Find("AmmunitionOptions")!;
        var row = new VtankRow();
        row.Cells.AddRange(
        [
            VtankCell.String("Deadly Fire Arrow"),
            VtankCell.Int(5),
            VtankCell.Int(230),
            VtankCell.Int(6),
            VtankCell.Int(20),
            VtankCell.Int(0),
            VtankCell.Int(0),
            VtankCell.Int(0),
        ]);
        ammo.Rows.Add(row);
        return VtankGameInfoDatabase.Parse(document.Render());
    }

    private static readonly VtankGameInfoDatabase GameInfo =
        VtankGameInfoDatabase.Parse(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Fixtures",
                    "vtank",
                    "gameinfodb-excerpt.ugd")));

    /// <summary>
    /// The profile lists its items by object id: one wand, and one object the
    /// character no longer carries. The pack holds a weapon that merely SHARES
    /// the name of a listed row. It is a different object and the profile
    /// never asked for it, so the fight must not reach for it -- and the row
    /// for the object that is gone must not hold the pass up either.
    ///
    /// With no weapon it may use and no war or void magic to cast with the
    /// wand, the fight says what is missing once and lets the monster go, so
    /// the rules below the attack still get their turn.
    ///
    /// Mutation executed: let the item-NAME list answer for a profile that
    /// lists object ids (restore the CombatItemNames arm in the selection
    /// predicate) and the unlisted copy is chosen and wielded.
    /// </summary>
    [Fact]
    public void AnUnlistedCopyOfAListedWeaponIsNeverChosen()
    {
        var surface = new FakeAutomation
        {
            CombatSnapshot = Peaceful(),
            Targets =
            [
                Target(10, "Plasma Golem", distance: 1.8f, angle: 0),
                Target(11, "Banderling Savage", distance: 2.1f, angle: 8),
            ],
            EquipmentItems =
            [
                Equipment(700, "Wings of Rakhil", damageType: 0,
                    itemType: 0x00008000u),
                // In hand. Same name as the listed-but-absent row 999, a
                // different object, and on no list of its own.
                Equipment(900, "Decapitator's Blade", damageType: 0x0001,
                    equippedLocation: 0x02000000u,
                    validLocations: 0x02000000u),
            ],
        };
        var settings = new CombatSettings { MaximumRange = 8f };
        settings.CombatItemNames.Add("Wings of Rakhil");
        settings.CombatItemNames.Add("Decapitator's Blade");
        settings.CombatItemObjectIds.Add(700u);
        settings.CombatItemOrderIds.Add(700u);
        // Listed, and no longer in the pack.
        settings.CombatItemObjectIds.Add(999u);
        settings.CombatItemOrderIds.Add(999u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        for (int tick = 0; tick < 8; tick++)
            controller.OnTick(0.25d);

        Assert.DoesNotContain("Equip:00000384", surface.CallLog);
        Assert.DoesNotContain("Equip:000002BC", surface.CallLog);
        Assert.Equal(0, surface.BeginCount);
        Assert.Single(
            surface.PostedSystemMessages,
            message => message.Contains(
                "Add the weapon you fight with to the Items list.",
                StringComparison.Ordinal));
        // The fight steps aside; it does not stop the macro, so looting and
        // navigation still get their turn.
        Assert.True(controller.Enabled);
    }

    /// <summary>
    /// Once the weapon IS listed, the fight keeps it. A wand that is also
    /// listed is the last rung the automatic choice reaches for, and which
    /// rung answers changes with every monster -- so the character used to
    /// put its weapon away after each kill, take the wand out, and take the
    /// weapon back for the monster after that, all while the monsters it had
    /// stopped fighting were still hitting it.
    ///
    /// Mutation executed: drop the second walk that prefers something that
    /// can strike, and the wand is wielded between the two kills.
    /// </summary>
    [Fact]
    public void AListedWeaponIsKeptBetweenKillsRatherThanSwappingToTheWand()
    {
        PluginEquipmentItem RatedWand() => Equipment(
            700, "Wings of Rakhil", damageType: 0, itemType: 0x00008000u)
            with { ImbuedEffect = 0x0001 };
        PluginEquipmentItem HeldBlade() => Equipment(
            900, "Decapitator's Blade", damageType: 0x0001,
            equippedLocation: 0x02000000u, validLocations: 0x02000000u);

        var surface = new FakeAutomation
        {
            CombatSnapshot = Physical(),
            // War magic is trained, so the wand is a weapon the walk will
            // reach for: it is the last rung, and it answers for every
            // monster no listed weapon's element suits.
            CharacterSkills =
            [
                new PluginSkillInfo(34u, "War Magic",
                    PluginSkillTraining.Trained, 300u) { Base = 300u },
            ],
            Targets =
            [
                Target(10, "Plasma Golem", distance: 1.8f, angle: 0),
                Target(11, "Banderling Savage", distance: 2.1f, angle: 8),
            ],
            EquipmentItems = [RatedWand(), HeldBlade()],
        };
        var settings = new CombatSettings
        {
            MaximumRange = 8f,
            SelectionMethod = TargetSelectionMethod.Range,
            ScanIntervalSeconds = 0.05d,
        };
        settings.CombatItemObjectIds.Add(700u);
        settings.CombatItemOrderIds.Add(700u);
        settings.CombatItemObjectIds.Add(900u);
        settings.CombatItemOrderIds.Add(900u);
        settings.Rules.Clear();
        settings.Rules.Add(new MonsterRule(
            "DEFAULT",
            new MonsterRuleActions { Flags = MonsterActionFlags.Attack }));
        var controller = new CombatController(new FakeHost(surface), settings);

        controller.Toggle();
        controller.OnTick(0.25d);
        Assert.Equal(10u, surface.LastBeginTarget);

        // The first monster dies with the second still at arm's length.
        surface.Targets =
        [
            Target(10, "Plasma Golem", distance: 1.8f, angle: 0, health: 0f),
            Target(11, "Banderling Savage", distance: 2.1f, angle: 8),
        ];
        surface.UnattackableTargets.Add(10u);
        surface.BeginTargets.Clear();
        for (int tick = 0; tick < 4; tick++)
            controller.OnTick(0.5d);

        Assert.DoesNotContain("Equip:000002BC", surface.CallLog);
        Assert.DoesNotContain("EnterMode:Magic", surface.CallLog);
        Assert.Equal(11u, surface.LastBeginTarget);
    }

    private static PluginCombatTarget Target(
        uint id, string name, float distance, float angle,
        float health = 1f) => new(
            id, name, id + 1000, distance, angle, true, health);

    private static PluginSpellInfo Spell(uint id, string name) => new(
        id, name, Family: 1, Tier: 8, Difficulty: 350, ManaCost: 30,
        DurationSeconds: 0, School: 34, Description: string.Empty,
        IsSelfTargeted: false, IsBeneficial: false)
    {
        BaseRangeConstant = 80f,
        Saying = "testwords",
    };

    /// <summary>The slot a quiver of ammunition goes in, not a weapon slot.</summary>
    private const uint AmmunitionSlot = 0x00800000u;

    private static PluginEquipmentItem Equipment(
        uint id,
        string name,
        int damageType,
        int damage = 20,
        uint equippedLocation = 0,
        uint itemType = 1,
        byte combatUse = 1,
        uint ammoType = 0,
        int stackSize = 1,
        uint validLocations = 0x00100000u) => new(
            id,
            name,
            ItemType: itemType,
            ValidLocations: validLocations,
            EquippedLocation: equippedLocation,
            ContainerObjectId: 1,
            WielderObjectId: 0,
            CombatUse: combatUse,
            DamageType: damageType,
            WeaponSkill: 44,
            Damage: damage,
            DamageVariance: 0.25)
        {
            AmmoType = ammoType,
            StackSize = stackSize,
            ObjectClass = (itemType & 0x8000u) != 0u
                ? PluginObjectClass.WandStaffOrb
                : (itemType & 0x100u) != 0u
                    ? PluginObjectClass.MissileWeapon
                    : PluginObjectClass.MeleeWeapon,
        };

    private static PluginEquipmentItem WieldedCaster(uint id = 990u) =>
        Equipment(
            id,
            "Fixture Wand",
            damageType: 0,
            itemType: 0x00008000u,
            equippedLocation: 0x00100000u);

    private static PluginEquipmentItem WieldedPlannedWeapon(uint id = 991u) =>
        Equipment(
            id,
            "Fixture Weapon",
            damageType: 0x007F,
            equippedLocation: 0x00100000u);

    private static CombatSettings ProfileFixtureWeapon(CombatSettings settings)
    {
        settings.CombatItemNames.Add("Fixture Weapon");
        return settings;
    }

    private static PluginInventoryItem PetDevice(uint id, uint wcid) => new(
        id, wcid, "Frost Pet", 0, 1, 0, 0, 0, 0, 0, 0, 1, 50, 50,
        0, 0, 3, 0, false, 0, 0, 0, 0, 0, 54, 100, 0);

    private static PluginInventoryItem InventoryItem(
        uint id,
        string name,
        uint itemType,
        uint spellId,
        bool equipped) => new(
            id, 0, name, itemType, 1, 0, 0,
            equipped ? 0x00100000u : 0u,
            0, 0, 0, 1, 0, 0, spellId, 0, 0, 0, false, 0,
            0, 0, 0, 0, 0, 0, 0);

    /// <summary>A host with real storage, for profile round-trips.</summary>
    private sealed class StorageHost(
        IPluginStorage storage,
        string characterName,
        string worldName) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new FakeLogger();
        public IGameState State { get; } = new FakeState();
        public IEvents Events { get; } = new FakeEvents();
        public ISelectionService Selection { get; } = new FakeSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation { get; } = new FakeAutomation
        {
            CharacterName = characterName,
            World = worldName,
        };
        public IPluginStorage Storage => storage;
        public IPluginStorage VtankProfiles => storage;
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _text =
            new(StringComparer.Ordinal);

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

    /// <summary>
    /// The other clients on this computer, as the host would hand them over:
    /// staged casts come back above the sequence asked for, oldest first,
    /// reads never consume, and every announcement this character makes is
    /// kept.
    /// </summary>
    private sealed class FakePeerNetwork : INetworkAutomation
    {
        public bool IsAvailable => true;
        public List<PluginNetworkClient> Clients { get; } = [];
        public List<PluginPeerCast> Casts { get; } = [];

        /// <summary>The sequence handed over on each capture, in order.</summary>
        public List<long> CaptureRequests { get; } = [];
        public List<(uint Target, uint Spell, int Skill)> Attempts { get; } = [];
        public List<(uint Target, uint Spell, int Skill, double Duration)> Successes { get; } = [];

        public int ClientCaptureCount { get; private set; }
        public int ClientCaptureFailuresRemaining { get; set; }
        public IReadOnlyList<PluginNetworkClient> CaptureClients()
        {
            ClientCaptureCount++;
            if (ClientCaptureFailuresRemaining > 0)
            {
                ClientCaptureFailuresRemaining--;
                throw new IOException("Fixture peer read failed.");
            }
            return Clients;
        }

        public bool AnnounceCastAttempt(uint targetObjectId, uint spellId, int effectiveSkill)
        {
            Attempts.Add((targetObjectId, spellId, effectiveSkill));
            return true;
        }

        public bool AnnounceCastSuccess(
            uint targetObjectId,
            uint spellId,
            int effectiveSkill,
            double durationSeconds)
        {
            Successes.Add((targetObjectId, spellId, effectiveSkill, durationSeconds));
            return true;
        }

        public IReadOnlyList<PluginPeerCast> CaptureCasts(long afterSequence)
        {
            CaptureRequests.Add(afterSequence);
            return Casts
                .Where(cast => cast.Sequence > afterSequence)
                .OrderBy(static cast => cast.Sequence)
                .ToArray();
        }
    }

    /// <summary>The host's own ledger of effects, as far as reports into it go.</summary>
    private sealed class FakeEnchantmentLedger : IEnchantmentAutomation
    {
        public List<(uint Target, uint Spell, double Seconds)> Reported { get; } = [];

        public bool ReportCast(uint targetObjectId, uint spellId, double durationSeconds)
        {
            Reported.Add((targetObjectId, spellId, durationSeconds));
            return true;
        }

        public IReadOnlyList<PluginTrackedEnchantment> Capture(uint targetObjectId) => [];

        public void ForgetReported(uint targetObjectId)
        {
        }
    }

    private static PluginPeerCast PeerCast(
        long sequence,
        uint spellId,
        uint target = 10u,
        uint caster = 0x50000002u,
        uint clientId = 7u,
        double secondsRemaining = 45d,
        bool landed = true,
        int skill = 300) => new(
            sequence, clientId, caster, target, spellId, skill, secondsRemaining, landed);

    private sealed class FakeHost(FakeAutomation automation) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new FakeLogger();
        public IGameState State { get; } = new FakeState();
        public IEvents Events { get; } = new FakeEvents();
        public ISelectionService Selection { get; } = new FakeSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation => automation;
    }

    private sealed class FakeAutomation :
        IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
        IPluginChat, ICombatAutomation
        , IEquipmentAutomation, IItemAutomation, INavigationAutomation,
        IProjectileAutomation, ISelectionAutomation, IWorldObjectAutomation,
        IRecoveryAutomation
    {
        public bool IsAvailable { get; set; } = true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => this;
        public IMagicCommands Magic => this;
        public IPluginChat Chat => this;
        public FakePeerNetwork Peers { get; } = new();
        public INetworkAutomation Network => Peers;
        public FakeEnchantmentLedger Ledger { get; } = new();
        public IEnchantmentAutomation Enchantments => Ledger;
        public bool ChatInputActive { get; set; }
        bool IPluginChat.IsInputActive => ChatInputActive;
        public ICombatAutomation Combat => this;
        public IEquipmentAutomation Equipment => this;
        public IRecoveryAutomation Recovery => this;
        public PluginBusyState BusyState { get; set; }
        public PluginBusyState CaptureBusyState() => BusyState;
        public IItemAutomation Items => this;
        public INavigationAutomation Navigation => this;
        // The host's object table, as far as these tests need it: every owned
        // piece of equipment is there and already assessed, the state a
        // profiled weapon is in before the macro may wield it.
        public IWorldObjectAutomation Objects => this;
        bool IWorldObjectAutomation.IsAvailable => true;
        bool IWorldObjectAutomation.TryGet(uint objectId, out PluginWorldObject value)
        {
            foreach (PluginEquipmentItem item in EquipmentItems)
            {
                if (item.ObjectId != objectId)
                    continue;
                value = new PluginWorldObject(
                    item.ObjectId, 0u, item.Name, item.ObjectClass,
                    item.ItemType, item.ContainerObjectId, item.WielderObjectId)
                {
                    LastIdTime = 1,
                    IsOwned = !UnownedEquipmentIds.Contains(item.ObjectId),
                };
                return true;
            }
            // The monsters the tests stage are in the world too: the controller
            // drops a target the object table cannot find.
            foreach (PluginCombatTarget target in Targets)
            {
                if (target.ObjectId != objectId)
                    continue;
                value = new PluginWorldObject(
                    target.ObjectId, target.WeenieClassId, target.Name,
                    PluginObjectClass.Unknown, 0u, 0u, 0u);
                return true;
            }
            if (WorldOnlyObjectIds.Contains(objectId))
            {
                PluginInventoryItem? inventory = ItemEntries.FirstOrDefault(
                    item => item.ObjectId == objectId);
                value = new PluginWorldObject(
                    objectId,
                    0u,
                    inventory?.Name ?? "Inventory item",
                    PluginObjectClass.Unknown,
                    inventory?.ItemType ?? 0u,
                    1u,
                    0u)
                {
                    LastIdTime = 1,
                    IsOwned = true,
                };
                return true;
            }
            value = default;
            return false;
        }
        IReadOnlyList<PluginWorldObject> IWorldObjectAutomation.CaptureObjects()
        {
            var objects = new List<PluginWorldObject>();
            foreach (PluginEquipmentItem item in EquipmentItems)
            {
                objects.Add(new PluginWorldObject(
                    item.ObjectId, 0u, item.Name, item.ObjectClass,
                    item.ItemType, item.ContainerObjectId, item.WielderObjectId)
                {
                    LastIdTime = 1,
                    IsOwned = !UnownedEquipmentIds.Contains(item.ObjectId),
                });
            }
            return objects;
        }
        public IProjectileAutomation Projectiles => this;
        public ISelectionAutomation Selection => this;
        public PluginCombatSnapshot CombatSnapshot { get; set; }
        public PluginCombatSnapshot Snapshot => CombatSnapshot;
        public IReadOnlyList<PluginCombatTarget> Targets { get; set; } = [];
        public IReadOnlyList<PluginSpellInfo> KnownAttackSpells { get; set; } = [];
        private IReadOnlyList<PluginSpellInfo> _knownCombatSpells = [];
        public int KnownCombatSpellReads { get; private set; }
        public IReadOnlyList<PluginSpellInfo> KnownCombatSpells
        {
            get
            {
                KnownCombatSpellReads++;
                return _knownCombatSpells;
            }
            set => _knownCombatSpells = value;
        }
        public IReadOnlyList<PluginSpellInfo> SpellLookup { get; set; } = [];
        public PluginCastCompletion LastCastCompletion { get; set; }
        public PluginCastCompletion LastCompletion => LastCastCompletion;
        public uint LastBeginTarget { get; private set; }

        /// <summary>
        /// Monsters the host will refuse a swing at, the way it refuses one
        /// at a creature that is no longer in play: the request is answered,
        /// with the reason, and nothing is armed.
        /// </summary>
        public HashSet<uint> UnattackableTargets { get; } = [];

        /// <summary>Every target a swing was asked for, in order.</summary>
        public List<uint> BeginTargets { get; } = [];

        /// <summary>
        /// Opt-in: the fake walks the host's own attack states — the press
        /// starts the power bar, the release hands the swing to the server,
        /// a cancel ends it — so a pin can travel the whole press → build →
        /// release path instead of declaring the server already holds a swing.
        /// </summary>
        public bool TracksAttackRequests { get; set; }

        /// <summary>The power bar reaching the top, as a charging pass would.</summary>
        public void FillPowerBar() =>
            CombatSnapshot = CombatSnapshot with { PowerBarLevel = 1f };

        public int BeginCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public int AbortCount { get; private set; }
        public (uint Spell, uint Target) LastTargetedCast { get; private set; }
        public uint LastUntargetedCast { get; private set; }
        public List<uint> CastSpellIds { get; } = [];
        public IReadOnlyList<PluginEquipmentItem> EquipmentItems { get; set; } = [];
        public ISet<uint> UnownedEquipmentIds { get; } = new HashSet<uint>();
        public ISet<uint> WorldOnlyObjectIds { get; } = new HashSet<uint>();
        public uint LastEquipObjectId { get; private set; }
        public IReadOnlyList<PluginInventoryItem> ItemEntries { get; set; } = [];
        public uint LastUsedItem { get; private set; }
        public (uint Item, uint Target) LastAppliedItem { get; private set; }
        public int ApplyCount { get; private set; }
        public PluginItemUseCompletion LastItemCompletion { get; set; }
        public IReadOnlyList<PluginChatMessage> ChatMessages { get; set; } = [];
        public float LastBeginPower { get; private set; }

        public PluginAttackHeight LastBeginHeight { get; private set; }
        public PluginNavigationSnapshot NavigationSnapshot { get; set; }
        public bool IgnoreModeChanges { get; set; }
        public int ModeChangeRequests { get; private set; }
        PluginNavigationSnapshot INavigationAutomation.Snapshot =>
            NavigationSnapshot;
        public Dictionary<uint, PluginNavigationObject> NavigationObjects { get; } = [];
        public List<PluginMovementIntent> MovementIntents { get; } = [];
        public int ClearMovementCount { get; private set; }
        public PluginProjectilePathResult ProjectilePath { get; set; } =
            new(PluginProjectilePathStatus.Clear);
        public uint LastProjectileTarget { get; private set; }
        public PluginAttackHeight LastProjectileHeight { get; private set; }
        public PluginProjectilePathKind LastProjectileKind { get; private set; }
        public int ProjectilePathChecks { get; private set; }
        public float LastProjectileRadius { get; private set; }
        public float LastProjectileStepDistance { get; private set; }

        /// <summary>Per-target overrides for the flight-path check.</summary>
        public Dictionary<uint, PluginProjectilePathResult> ProjectilePaths
        { get; } = [];
        public IReadOnlyList<PluginProjectileDebugSample>
            ShownProjectileDebugSamples { get; private set; } = [];
        public List<PluginSelectionAction> SelectionActions { get; } = [];

        public List<string> CallLog { get; } = [];

        public bool SimulateAsyncEquip { get; set; }
        private uint? _pendingEquipObjectId;
        public bool EquipmentAvailable { get; set; } = true;
        bool IEquipmentAutomation.IsAvailable => EquipmentAvailable;
        bool IEquipmentAutomation.IsBusy =>
            SimulateAsyncEquip && _pendingEquipObjectId is not null;
        public event Action<PluginEquipmentObservation>? PlacementObserved;
        public void EmitPlacement(PluginEquipmentObservation observation) =>
            PlacementObserved?.Invoke(observation);
        public IReadOnlyList<PluginEquipmentPlacement> CaptureWorldPlacementsInOrder() =>
            EquipmentItems.Select(static item => new PluginEquipmentPlacement(
                item.ObjectId, item.EquippedLocation)).ToArray();
        public int CaptureOwnedEquipmentCount { get; private set; }

        /// <summary>
        /// The host hands this list out held-first, then by name, then by
        /// object id — an order that turns over the moment something is
        /// wielded. A fixture that hands back its own insertion order instead
        /// cannot see anything that goes wrong because of that.
        /// </summary>
        public IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment()
        {
            CaptureOwnedEquipmentCount++;
            var projected = new List<PluginEquipmentItem>(EquipmentItems);
            projected.Sort(static (left, right) =>
            {
                int equipped = right.IsEquipped.CompareTo(left.IsEquipped);
                if (equipped != 0)
                    return equipped;
                int name = string.CompareOrdinal(left.Name, right.Name);
                return name != 0
                    ? name
                    : left.ObjectId.CompareTo(right.ObjectId);
            });
            return projected;
        }
        public PluginEquipmentCommandResult Equip(
            uint objectId,
            uint requestedLocation = 0u)
        {
            LastEquipObjectId = objectId;
            CallLog.Add($"Equip:{objectId:X8}");
            if (SimulateAsyncEquip)
                _pendingEquipObjectId = objectId;
            else
                ApplyConfirmedEquip(objectId);
            return new(PluginEquipmentCommandStatus.Started);
        }
        public PluginEquipmentCommandResult EquipSecondary(uint objectId)
        {
            LastEquipObjectId = objectId;
            CallLog.Add($"EquipSecondary:{objectId:X8}");
            ApplyConfirmedEquip(objectId);
            return new(PluginEquipmentCommandStatus.Started);
        }

        public void ConfirmPendingEquip()
        {
            if (_pendingEquipObjectId is not { } objectId)
                return;
            ApplyConfirmedEquip(objectId);
            _pendingEquipObjectId = null;
        }

        private void ApplyConfirmedEquip(uint objectId)
        {
            // An item the equipment view does not carry yet still takes the
            // command; the placement is only reported once the view catches
            // up with it, which is not this pass.
            if (!EquipmentItems.Any(item => item.ObjectId == objectId))
                return;
            IReadOnlyList<PluginEquipmentItem> previous = EquipmentItems;
            EquipmentItems = MarkEquipped(previous, objectId);
            foreach (PluginEquipmentItem before in previous)
            {
                if (before.EquippedLocation != 0u
                    && EquipmentItems.First(item => item.ObjectId == before.ObjectId)
                        .EquippedLocation == 0u)
                {
                    PlacementObserved?.Invoke(new PluginEquipmentObservation(
                        before.ObjectId, 0u, true));
                }
            }
            PluginEquipmentItem equipped = EquipmentItems.First(
                item => item.ObjectId == objectId);
            PlacementObserved?.Invoke(new PluginEquipmentObservation(
                objectId, equipped.EquippedLocation, false));
        }

        /// <summary>
        /// A slot holds one thing: wielding this item puts it where it goes
        /// and takes whatever was already there out of the character's hands.
        /// A fixture that lets two items share the weapon slot hides every
        /// bug that turns on which of them the host calls the wielded one.
        /// </summary>
        private static IReadOnlyList<PluginEquipmentItem> MarkEquipped(
            IReadOnlyList<PluginEquipmentItem> items,
            uint objectId)
        {
            uint slot = 0u;
            foreach (PluginEquipmentItem item in items)
            {
                if (item.ObjectId == objectId)
                {
                    slot = item.ValidLocations;
                    break;
                }
            }
            return items
                .Select(item => item.ObjectId == objectId
                    ? item with { EquippedLocation = slot }
                    : (item.EquippedLocation & slot) != 0u
                        ? item with { EquippedLocation = 0u }
                        : item)
                .ToArray();
        }
        bool IItemAutomation.IsAvailable => true;
        bool IItemAutomation.IsBusy => false;
        int IItemAutomation.ActiveOwnedPetCount => 0;
        PluginItemUseCompletion IItemAutomation.LastCompletion => LastItemCompletion;
        public int CaptureOwnedItemsCount { get; private set; }
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems()
        {
            CaptureOwnedItemsCount++;
            return ItemEntries;
        }

        // A summoning essence's assessment carries its shared cooldown; the
        // pet class it summons is never sent.
        bool IItemAutomation.TryCaptureProperties(
            uint objectId, out PluginItemProperties properties)
        {
            properties = default;
            foreach (PluginInventoryItem item in ItemEntries)
            {
                if (item.ObjectId != objectId)
                    continue;
                var ints = new Dictionary<uint, int>();
                if (PetDeviceCatalog.DamageType(item.WeenieClassId) != MonsterDamageType.Auto)
                    ints[280u] = 213;
                properties = new PluginItemProperties(
                    ints,
                    new Dictionary<uint, long>(),
                    new Dictionary<uint, bool>(),
                    new Dictionary<uint, double>(),
                    new Dictionary<uint, string>(),
                    new Dictionary<uint, uint>(),
                    new Dictionary<uint, uint>());
                return true;
            }
            return false;
        }
        public PluginItemCommandResult Use(uint objectId)
        {
            LastUsedItem = objectId;
            return new(PluginItemCommandStatus.Started);
        }
        public PluginItemCommandResult MoveToContainer(
            uint objectId, uint containerObjectId, uint amount = 0u,
            int placement = 0)
        {
            CallLog.Add($"Move:{objectId:X8}");
            EquipmentItems = EquipmentItems.Select(item =>
                item.ObjectId == objectId
                    ? item with { EquippedLocation = 0u }
                    : item).ToArray();
            PlacementObserved?.Invoke(new PluginEquipmentObservation(
                objectId, 0u, true));
            return new(PluginItemCommandStatus.Started);
        }
        public PluginItemCommandResult Apply(uint objectId, uint targetObjectId)
        {
            LastAppliedItem = (objectId, targetObjectId);
            ApplyCount++;
            return new(PluginItemCommandStatus.Started);
        }

        public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(
            float maximumDistance) => Targets;
        public PluginCombatCommandResult EnterDefaultMode()
        {
            CallLog.Add("EnterDefaultMode");
            return new(PluginCombatCommandStatus.ModeChangeSent);
        }

        public bool DeferModeConfirmation { get; set; }

        /// <summary>
        /// The client reports the new mode at once but the server has not
        /// yet put the body in its stance, so no motion of this character
        /// has arrived: what the live host looks like between sending a
        /// change and the server taking it.
        /// </summary>
        public bool WithholdModeEcho { get; set; }
        private PluginCombatMode? _pendingMode;
        public bool ModeCommandUnavailable { get; set; }

        public PluginCombatCommandResult EnterMode(PluginCombatMode mode)
        {
            ModeChangeRequests++;
            CallLog.Add($"EnterMode:{mode}");
            if (ModeCommandUnavailable)
                return new(PluginCombatCommandStatus.Unavailable);
            if (IgnoreModeChanges)
                return new(PluginCombatCommandStatus.ModeChangeSent);
            if (DeferModeConfirmation)
            {
                _pendingMode = mode;
                return new(PluginCombatCommandStatus.ModeChangeSent);
            }
            if (WithholdModeEcho)
            {
                CombatSnapshot = CombatSnapshot with { Mode = mode };
                _pendingMode = mode;
                return new(PluginCombatCommandStatus.ModeChangeSent);
            }
            // The client reports the mode at once and the server's stance
            // follows on its heels, as it does when nothing is in the way.
            // A host that passes the server's word on agrees at once too.
            CombatSnapshot = CombatSnapshot with
            {
                Mode = mode,
                QualifiedSelfMotionRevision =
                    CombatSnapshot.QualifiedSelfMotionRevision + 1,
                QualifiedSelfMotionAgeSeconds = 0d,
                ServerMode = CombatSnapshot.ServerMode == PluginCombatMode.Unknown
                    ? PluginCombatMode.Unknown
                    : mode,
            };
            return new(PluginCombatCommandStatus.ModeChangeSent);
        }

        public void ConfirmPendingModeChange()
        {
            if (_pendingMode is not { } mode)
                return;
            CombatSnapshot = CombatSnapshot with
            {
                Mode = mode,
                QualifiedSelfMotionRevision =
                    CombatSnapshot.QualifiedSelfMotionRevision + 1,
                QualifiedSelfMotionAgeSeconds = 0d,
                ServerMode = CombatSnapshot.ServerMode == PluginCombatMode.Unknown
                    ? PluginCombatMode.Unknown
                    : mode,
            };
            _pendingMode = null;
        }
        /// <summary>
        /// The creature the open swing was armed at, which is what the host
        /// compares a fresh request against.
        /// </summary>
        private uint _armedTarget;

        /// <summary>
        /// A swing the host will not let out, as one at a creature that has
        /// died behaves: the request ends with nothing on the wire.
        /// </summary>
        public HashSet<uint> UnreleasableTargets { get; } = [];

        public PluginCombatCommandResult BeginPhysicalAttack(
            uint targetObjectId, PluginAttackHeight height, float power)
        {
            // The host refuses a second swing at the SAME creature while one is
            // still open - that wait is the pace of a fight. A request aimed at
            // a different creature ends the open one and arms the new one, so a
            // kill is not paid for with a wait on the corpse's swing.
            if ((CombatSnapshot.RequestInProgress
                    || CombatSnapshot.ServerResponsePending
                    || CombatSnapshot.RepeatAttackInProgress)
                && _armedTarget == targetObjectId)
            {
                return new(PluginCombatCommandStatus.Busy);
            }
            BeginTargets.Add(targetObjectId);
            if (UnattackableTargets.Contains(targetObjectId))
            {
                return new(
                    PluginCombatCommandStatus.Refused,
                    "The target cannot be attacked: it is out of sight or out of play.");
            }
            LastBeginTarget = targetObjectId;
            LastBeginPower = power;
            LastBeginHeight = height;
            BeginCount++;
            _armedTarget = targetObjectId;
            if (TracksAttackRequests)
            {
                CombatSnapshot = CombatSnapshot with
                {
                    RequestInProgress = true,
                    BuildInProgress = true,
                    PowerBarLevel = 0f,
                    ServerResponsePending = false,
                    RepeatAttackInProgress = false,
                };
            }
            return new(PluginCombatCommandStatus.Started);
        }
        public PluginCombatCommandResult ReleasePhysicalAttack()
        {
            ReleaseCount++;
            if (UnreleasableTargets.Contains(_armedTarget))
            {
                _armedTarget = 0u;
                if (TracksAttackRequests)
                {
                    CombatSnapshot = CombatSnapshot with
                    {
                        RequestInProgress = false,
                        BuildInProgress = false,
                        ServerResponsePending = false,
                        PowerBarLevel = 0f,
                    };
                }
                return new(
                    PluginCombatCommandStatus.Refused,
                    "The target cannot be attacked: it is out of sight or out of play.");
            }
            if (TracksAttackRequests)
            {
                CombatSnapshot = CombatSnapshot with
                {
                    RequestInProgress = false,
                    BuildInProgress = false,
                    ServerResponsePending = true,
                };
            }
            return new(PluginCombatCommandStatus.Released);
        }
        public PluginCombatCommandResult AbortPhysicalAttack()
        {
            AbortCount++;
            _armedTarget = 0u;
            if (TracksAttackRequests)
            {
                CombatSnapshot = CombatSnapshot with
                {
                    RequestInProgress = false,
                    BuildInProgress = false,
                    ServerResponsePending = false,
                    RepeatAttackInProgress = false,
                    PowerBarLevel = 0f,
                };
            }
            return new(PluginCombatCommandStatus.Stopped);
        }

        public List<uint> DismissedGhosts { get; } = [];
        public bool GhostDismissalAccepted { get; set; } = true;
        public PluginCombatCommandResult DismissGhostTarget(uint targetObjectId)
        {
            DismissedGhosts.Add(targetObjectId);
            return new(GhostDismissalAccepted
                ? PluginCombatCommandStatus.Stopped
                : PluginCombatCommandStatus.Unavailable);
        }

        public bool TryGetObject(
            uint objectId,
            out PluginNavigationObject value) =>
            NavigationObjects.TryGetValue(objectId, out value);
        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent)
        {
            MovementIntents.Add(intent);
            return PluginNavigationCommandStatus.Accepted;
        }
        public PluginNavigationCommandStatus ClearMovementIntent()
        {
            ClearMovementCount++;
            return PluginNavigationCommandStatus.Accepted;
        }

        public List<float> FacedHeadings { get; } = [];

        public PluginNavigationCommandStatus FaceHeading(float headingDegrees)
        {
            FacedHeadings.Add(headingDegrees);
            return PluginNavigationCommandStatus.Accepted;
        }

        bool IProjectileAutomation.IsAvailable => true;
        public PluginProjectilePathResult EvaluatePath(
            uint targetObjectId,
            PluginProjectilePathKind kind,
            PluginAttackHeight targetHeight,
            float projectileRadius,
            float stepDistance,
            int maximumCollisionChecks)
        {
            LastProjectileTarget = targetObjectId;
            LastProjectileHeight = targetHeight;
            LastProjectileKind = kind;
            LastProjectileRadius = projectileRadius;
            LastProjectileStepDistance = stepDistance;
            ProjectilePathChecks++;
            return ProjectilePaths.TryGetValue(
                targetObjectId,
                out PluginProjectilePathResult specific)
                ? specific
                : ProjectilePath;
        }

        public PluginProjectilePathResult EvaluatePathWithDiagnostics(
            uint targetObjectId,
            PluginProjectilePathKind kind,
            PluginAttackHeight targetHeight,
            float projectileRadius,
            float stepDistance,
            int maximumCollisionChecks) => EvaluatePath(
                targetObjectId,
                kind,
                targetHeight,
                projectileRadius,
                stepDistance,
                maximumCollisionChecks);

        public void ShowDebugSamples(
            IReadOnlyList<PluginProjectileDebugSample> samples) =>
            ShownProjectileDebugSamples = samples.ToArray();

        public bool Execute(PluginSelectionAction action)
        {
            SelectionActions.Add(action);
            return true;
        }

        public bool IsCasting { get; set; }

        /// <summary>
        /// Spell ids the pack cannot pay for. Everything else has components.
        /// </summary>
        public HashSet<uint> MissingComponentSpellIds { get; } = [];

        public bool HasComponents(uint spellId) =>
            !MissingComponentSpellIds.Contains(spellId);

        /// <summary>Spells the client would refuse to start right now.</summary>
        public Dictionary<uint, PluginCastGate> CastGates { get; } = [];

        public PluginCastGate EvaluateGate(uint spellId) =>
            CastGates.TryGetValue(spellId, out PluginCastGate gate)
                ? gate
                : PluginCastGate.Ready;
        public PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) =>
            EvaluateGate(spellId);
        public bool Cast(uint spellId)
        {
            LastUntargetedCast = spellId;
            CastSpellIds.Add(spellId);
            return true;
        }
        public bool Cast(uint spellId, uint targetObjectId)
        {
            LastTargetedCast = (spellId, targetObjectId);
            CastSpellIds.Add(spellId);
            return true;
        }
        public List<string> PostedSystemMessages { get; } = [];
        public void PostSystemMessage(string text) =>
            PostedSystemMessages.Add(text);
        public IReadOnlyList<PluginChatMessage> CaptureMessages(
            ulong afterSequence) => ChatMessages
                .Where(message => message.Sequence > afterSequence)
                .ToArray();

        public bool IsInWorld => IsAvailable;
        public string CharacterName { get; init; } = "Fixture";
        string ICharacterInfo.Name => CharacterName;
        public string World { get; init; } = "FixtureWorld";
        string ICharacterInfo.WorldName => World;
        public uint ObjectId => 1;
        public uint CurrentHealth => 100;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 100;
        public uint MaxStamina => 100;
        public uint CurrentMana => 100;
        public uint MaxMana => 100;
        public int SummoningMastery => 3;
        public IReadOnlyList<PluginSkillInfo> CharacterSkills { get; set; } =
            [new(54, "Summoning", PluginSkillTraining.Trained, 300)];
        public IReadOnlyList<PluginSkillInfo> Skills => CharacterSkills;
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            foreach (PluginSkillInfo candidate in CharacterSkills)
            {
                if (candidate.SkillId == skillId)
                {
                    skill = candidate;
                    return true;
                }
            }
            skill = default;
            return false;
        }
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            foreach (PluginSpellInfo spell in KnownAttackSpells)
            {
                if (spell.SpellId == spellId)
                {
                    info = spell;
                    return true;
                }
            }
            foreach (PluginSpellInfo spell in KnownCombatSpells.Concat(SpellLookup))
            {
                if (spell.SpellId == spellId)
                {
                    info = spell;
                    return true;
                }
            }
            info = default;
            return false;
        }
    }

    private static PluginNavigationSnapshot NavigationAt(float heading) => new(
        IsAvailable: true,
        IsPortalSpace: false,
        LocalObjectId: 1u,
        Position: new PluginNavigationPosition(
            0x7F7F0001,
            0d,
            0d,
            0d,
            heading,
            true),
        IsMoving: false,
        IsAirborne: false);

    private sealed class FakeLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
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
        public bool Select(uint objectId) => true;
        public bool Clear() => true;
    }
}
