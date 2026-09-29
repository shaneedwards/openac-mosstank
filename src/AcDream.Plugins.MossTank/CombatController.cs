using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class CombatController
{
    private const float PowerReleaseEpsilon = 0.005f;

    private readonly IPluginHost _host;
    private readonly CombatSettings _settings;
    private readonly VitalSettings _vitalSettings;
    private readonly DebuffTracker _debuffs = new();
    private readonly CombatFailureTracker _failures = new();
    private readonly MonsterHealthTracker _health;

    /// <summary>
    /// The highest peer-cast sequence already dealt with. Reads never
    /// consume, so this is the only thing between a cast and being applied
    /// twice. It goes back to the beginning whenever the debuff table comes
    /// to life, so every effect still in force is taken in fresh.
    /// </summary>
    private long _remoteCastCursor;
    private Func<bool> _castSharingEnabled = static () => true;
    private Func<string?> _castSharingPeerTag = static () => null;
    private IReadOnlyList<PluginCombatTarget> _targets =
        Array.Empty<PluginCombatTarget>();
    private IReadOnlyList<PluginSpellInfo>? _combatSpellSnapshot;
    private IReadOnlyList<PluginSpellInfo>? _attackSpellSnapshot;
    private AttackSpellCatalog _attackCatalog =
        AttackSpellCatalog.Build(Array.Empty<PluginSpellInfo>());
    private double _now;

    private double _lastElapsedSeconds;

    private uint _plannedWeapon;
    private uint? _plannedOffhand;

    private double _untilScan;
    private double _acquisitionRange;
    private uint _targetId;
    private ResolvedMonsterRule _targetRule;
    private string _targetName = string.Empty;
    private float _targetDistance;
    private string _targetText = "Target  —";
    private string _modeText = "Mode  Unknown";
    private PluginCombatMode _lastMode = PluginCombatMode.Unknown;
    private bool _paused;
    private long _observedPhysicalCompletion;
    private long _observedAttackCastCompletion;
    private uint _pendingPhysicalTarget;
    private uint _pendingAttackSpell;

    private readonly Dictionary<(MonsterRuleActions Actions, uint Target), MonsterDamageType>
        _passElements = [];
    private readonly Dictionary<
        (DebuffIdentity Identity, uint Target),
        IReadOnlyList<CombatDebuffSource>> _passDebuffSources = [];

    /// <summary>
    /// Every flight already tested this pass, and whether it was clear. A
    /// shape not in here has not been tried, and counts as clear until it is:
    /// that is what lets the debuff fallback drop a source the pass has
    /// already found unreachable while leaving untested sources alone.
    /// </summary>
    private readonly Dictionary<(uint Target, PluginProjectilePathKind Kind), PluginProjectilePathResult>
        _passClearance = [];
    private readonly Dictionary<DebuffIdentity, PluginSpellInfo?> _passDebuffSpells = [];
    private readonly Dictionary<(MonsterDamageType Element, uint Target), bool>
        _passDeliverable = [];

    /// <summary>
    /// Whether the pack holds what each spell's formula asks for. Answered
    /// once per pass per spell, the way the tier walk memoises it per frame.
    /// </summary>
    private readonly Dictionary<uint, bool> _passComponents = [];

    /// <summary>
    /// Monsters this pass has already found nothing to do about. They are out
    /// of the running until the next pass rebuilds the picture.
    /// </summary>
    private readonly HashSet<uint> _passInvalidTargets = [];

    /// <summary>
    /// Set while one decision turns a column off but leaves the monster worth
    /// coming back to. Without it, "nothing to cast" and "this one thing
    /// cannot be delivered" would both drop the monster.
    /// </summary>
    private bool _planKeptTheMonsterInPlay;

    /// <summary>
    /// Action columns this pass has turned off per monster, because the thing
    /// that column asks for turned out to be undeliverable against it. A
    /// monster whose remaining columns still offer something stays in the
    /// running.
    /// </summary>
    private readonly Dictionary<uint, MonsterActionFlags> _passClearedActions = [];

    /// <summary>
    /// Candidates already built this pass, so re-choosing does not re-evaluate
    /// the whole rule table per monster per attempt. Discarded when the range
    /// being asked about changes, and per monster when its columns change.
    /// </summary>
    private readonly Dictionary<uint, CombatTargetCandidate?> _passCandidates = [];
    private double _passCandidateRange = double.NaN;

    /// <summary>
    /// Why a monster went out of the running this pass, in the words the
    /// scan trace prints. Without it a monster the fight could not equip for
    /// reads as "invalidated during attack pass", which names the symptom and
    /// hides the cause.
    /// </summary>
    private readonly Dictionary<uint, string> _passTargetReasons = [];

    private void NoteTargetReason(uint objectId, string reason)
    {
        if (objectId != 0u)
            _passTargetReasons[objectId] = reason;
    }

    private void InvalidateForPass(uint objectId)
    {
        if (objectId == 0u)
            return;
        _passInvalidTargets.Add(objectId);
        _passCandidates.Remove(objectId);
    }

    /// <summary>
    /// The rule with whatever THIS pass has learned taken off it. What the
    /// pass learns — that a shot cannot reach, that a column is undeliverable
    /// — belongs to the pass and to nothing else, so it is applied where a
    /// decision reads the rule rather than written into the stored one.
    /// </summary>
    private ResolvedMonsterRule WithPassClearedActions(
        uint objectId,
        ResolvedMonsterRule rule) =>
        rule.Rule is not null
        && _passClearedActions.TryGetValue(
            objectId,
            out MonsterActionFlags cleared)
            ? rule with
            {
                Rule = rule.Rule.WithActions(
                    rule.Actions with
                    {
                        Flags = rule.Actions.Flags & ~cleared,
                    }),
            }
            : rule;

    private void ClearActionsForPass(uint objectId, MonsterActionFlags flags)
    {
        if (objectId == 0u)
            return;
        _passClearedActions[objectId] =
            (_passClearedActions.TryGetValue(objectId, out MonsterActionFlags held)
                ? held
                : MonsterActionFlags.None)
            | flags;
        _passCandidates.Remove(objectId);
    }
    private IReadOnlyList<PluginEquipmentItem>? _passEquipment;
    private IReadOnlyList<PluginInventoryItem>? _passInventory;
    private uint _pendingAttackTarget;
    private uint _lastTargetId;
    private PendingItemDebuff? _pendingItemDebuff;
    private ulong _observedChatSequence;
    private ulong _itemTransactionChatSequence;
    private long _observedItemCompletion;
    private bool _combatPolicySuspended;
    private bool _breakableTurnOwned;

    /// <summary>
    /// The walk to a monster steers through the same close-in mover as the
    /// route and the corpse walk, and owns its own instance: a mover torn
    /// down by the rule that lost a pass must not disarm another rule's.
    /// </summary>
    private readonly NavigationMover _approachMover;

    /// <summary>The same stamp for the breakable turn-to.</summary>
    private double _breakableTurnFaceHeadingStamp =
        NavigationController.NoFaceHeadingStamp;

    /// <summary>
    /// The reference client's breakable turn-to entry tolerance: two degrees,
    /// with a one-second budget.
    /// </summary>
    private const float BreakableTurnToleranceDegrees = 2f;
    private Func<CraftingPlan, bool>? _requestAmmunitionCraft;
    private Func<string, int, CraftingPlan?>? _resolveAmmunitionCraft;
    private int _randomDamageIndex;
    private long _observedJiggleCastCompletion;
    private bool _selectionJiggleActive;
    private bool _selectionJigglePreviousPlayer;
    private double _nextSelectionJiggleAt;

    /// <summary>
    /// When the current nudge window closes. The nudge is one short window
    /// per cast, not something that runs between casts.
    /// </summary>
    private double _selectionJiggleUntil;

    private static readonly MonsterDamageType[] RandomDamageCycle =
    [
        MonsterDamageType.Pierce,
        MonsterDamageType.Bludgeon,
        MonsterDamageType.Slash,
        MonsterDamageType.Acid,
        MonsterDamageType.Electric,
        MonsterDamageType.Cold,
        MonsterDamageType.Fire,
    ];

    public CombatController(
        IPluginHost host,
        CombatSettings settings,
        VitalSettings? vitalSettings = null,
        VtankGameInfoDatabase? gameInfo = null,
        SpellCastTracker? castTracker = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _approachMover = new NavigationMover(_host);
        _vitalSettings = vitalSettings ?? new VitalSettings();
        _gameInfo = gameInfo ?? VtankGameInfoDatabase.Empty;
        _health = new MonsterHealthTracker(
            () => _settings.MonsterFacts,
            objectId => !_host.Automation.Objects.IsAvailable
                || _host.Automation.Objects.TryGet(objectId, out _));
        _castTracker = castTracker ?? new SpellCastTracker();
        _castTracker.Completed += OnCastTrackerOutcome;
        // A request the server never answered is sent again rather than
        // costing a silent five seconds mid-fight.
        _castTracker.ReissueCast = (spellId, targetObjectId) =>
            targetObjectId == 0u
                ? _host.Automation.Magic.Cast(spellId)
                : _host.Automation.Magic.Cast(spellId, targetObjectId);
        // Every re-send at a silent target is one more reason to suspect the
        // monster is not really there; the answer clears the suspicion.
        _castTracker.SpellAttempted = objectId =>
        {
            if (_failures.RecordSpellAttempt(objectId, _settings))
                DeleteGhostMonster(objectId, _settings.DeleteGhostMonsters);
        };
        _castTracker.SpellAnswered = _failures.ResetSpellAttempts;
    }

    internal SpellCastTracker CastTracker => _castTracker;

    /// <summary>
    /// Is this monster one the combat pass is following and has not given up
    /// on? A monster the pass has never looked at answers false, exactly as
    /// one it has blacklisted or seen die does: both are "not something to
    /// point at right now". This is what a profile's own monster-finding
    /// expressions ask before handing back a target.
    /// </summary>
    internal bool IsTrackedAndNotBlacklisted(uint objectId) =>
        objectId != 0u
        && _failures.IsKnown(objectId)
        && _failures.Reason(objectId, _now) == CombatSuppressionReason.None;

    /// <summary>
    /// The shared cooldown table. A kill holds navigation off for three
    /// seconds so the corpse can be found and looted before the bot moves on.
    /// </summary>
    private ActionLockTable _actionLocks = new();
    private Func<bool> _holdsRouteAfterKill = static () => false;

    /// <summary>Seconds navigation is held after a kill.</summary>
    private const double PostKillNavigationLockSeconds = 3d;

    /// <summary>
    /// How long after the server closes an attack sequence the macro still
    /// treats result text as belonging to that attack.
    /// </summary>
    private const double PhysicalResultTextTailSeconds = 2d;

    /// <summary>
    /// How long a swing that has been SENT may stay outstanding with nothing
    /// coming back before the macro treats it as an attempt that will never
    /// resolve.
    ///
    /// The reference macro re-presses the attack key every quarter second and
    /// never waits on an answer, so it needs no such bound; this host refuses
    /// a second swing while the first is still open, so the wait has to end
    /// somewhere. The number is the one the reference already allows a
    /// request that produces no result at all — four and a half seconds —
    /// which sits well clear of the one-and-a-half to two-and-a-half seconds
    /// a swing that does land takes to report.
    /// </summary>
    private const double UnansweredSwingSeconds = 4.5d;

    /// <summary>
    /// When the outstanding swing was SENT, which is the release and not the
    /// press that starts the power bar: the bar takes up to a second to build
    /// and the server hears nothing at all until it is let go, so a clock
    /// started at the press would spend a fifth of the wait on our own charge.
    /// Negative infinity means nothing is in flight and the wait does not run.
    /// Re-stamped each time the wait is given up on, so a host that never
    /// reports the attack finished costs one counted attempt per bound rather
    /// than wedging the macro on one monster.
    /// </summary>
    private double _physicalSwingSentAt = double.NegativeInfinity;

    /// <summary>
    /// How much nearer the monster has to come for the wait on an outstanding
    /// swing to start over. The server runs the character in to a monster the
    /// swing cannot yet reach and only strikes on arrival, which from across a
    /// room with a slow weapon takes longer than the whole wait; while that
    /// run-in is demonstrably making ground the swing is working, not stuck.
    /// Half a metre is a step's worth of ground.
    /// </summary>
    private const float SwingClosingProgressMeters = 0.5f;

    /// <summary>
    /// When the outstanding swing was ASKED for. The host holds the request
    /// open with the power bar stopped while the character is in no position
    /// to attack — a missile reload, a shield up — and if that never clears,
    /// nothing is ever sent and there is nothing to wait on. Negative
    /// infinity once the swing is away, when the send stamp takes over.
    /// </summary>
    private double _physicalSwingArmedAt = double.NegativeInfinity;

    /// <summary>
    /// The nearest the outstanding swing's monster has been since the swing
    /// went out. Only ground actually gained counts, so a monster milling
    /// about at one range cannot hold the wait open for ever.
    /// </summary>
    private float _physicalSwingClosestDistance = float.PositiveInfinity;

    private bool _physicalResultArmed;

    private uint _physicalResultTargetId;
    private string _physicalResultTargetName = string.Empty;
    private ushort _physicalResultIncarnation;
    private double _physicalCompletedAt = double.NegativeInfinity;

    private Action _suspendPass = static () => { };
    private Action _resumePass = static () => { };
    private bool _turnHoldsPass;
    private uint _breakableTurnTargetId;

    /// <summary>
    /// Lets the controller freeze the whole rule pass while the character is
    /// turning. Unbound (a controller-only rig) the hold is a no-op.
    /// </summary>
    internal void BindPassSuspension(Action suspend, Action resume)
    {
        _suspendPass = suspend ?? throw new ArgumentNullException(nameof(suspend));
        _resumePass = resume ?? throw new ArgumentNullException(nameof(resume));
    }

    internal void BindActionLocks(ActionLockTable locks, Func<bool> holdsRouteAfterKill)
    {
        _actionLocks = locks ?? throw new ArgumentNullException(nameof(locks));
        _holdsRouteAfterKill = holdsRouteAfterKill
            ?? throw new ArgumentNullException(nameof(holdsRouteAfterKill));
        _castTracker.BindActionLocks(_actionLocks);
    }

    /// <summary>
    /// A killing blow lands: hold navigation off for the looting window. The
    /// hold is conditional on the loot settings asking for it (looting on,
    /// and not rare-only), so a bot that loots nothing here keeps moving.
    /// </summary>
    private void ArmPostKillNavigationLock()
    {
        if (!_holdsRouteAfterKill())
            return;
        _actionLocks.Arm(
            ActionLockKind.Navigation,
            PostKillNavigationLockSeconds);
    }

    private readonly SpellCastTracker _castTracker;

    private VtankGameInfoDatabase _gameInfo;

    /// <summary>The game-information database every combat decision reads.</summary>
    internal VtankGameInfoDatabase GameInfo => _gameInfo;

    /// <summary>
    /// Takes a newer game-information database. Nothing here keeps a copy of
    /// a table: every reader goes through the field, so the next decision
    /// already reads the new one.
    /// </summary>
    internal void ReplaceGameInfo(VtankGameInfoDatabase gameInfo) =>
        _gameInfo = gameInfo ?? throw new ArgumentNullException(nameof(gameInfo));

    public bool Enabled { get; private set; }
    private IDisposable? _combatControl;
    private string _status = "Combat off";
    public event Action<string>? StatusChanged;

    public string Status
    {
        get => _status;
        private set
        {
            if (string.Equals(_status, value, StringComparison.Ordinal))
                return;
            _status = value;
            StatusChanged?.Invoke(value);
        }
    }
    public string TargetText => _targetText;
    public string ModeText => _modeText;
    public bool HasTarget => _targetId != 0u;

    /// <summary>
    /// What the attack is holding the turn for: the monster it has, how far
    /// away the client has it, and what it is doing or waiting on.
    /// </summary>
    public string RunningDetail
    {
        get
        {
            if (_targetId == 0u)
                return _pendingItemDebuff is null ? Status : $"no target; {Status}";
            string distance = "not in the last scan";
            foreach (PluginCombatTarget target in _targets)
            {
                if (target.ObjectId != _targetId)
                    continue;
                distance = target.Distance.ToString(
                    "0.0", System.Globalization.CultureInfo.InvariantCulture) + " m";
                break;
            }
            return $"{_targetName} (0x{_targetId:X8}, {distance}): {Status}";
        }
    }

    internal bool HasPendingItemDebuff => _pendingItemDebuff is not null;

    /// <summary>
    /// True while a cast from a HELD ITEM is outstanding. The reference's wand
    /// cast tracker raises the global busy count for the life of such a cast,
    /// so the pass runs no rule until it resolves.
    /// </summary>
    internal bool HeldItemCastInFlight =>
        _pendingItemDebuff is { Source.Kind: CombatDebuffSourceKind.CasterItem };

    /// <summary>
    /// True while a debuff cast from a LEARNED SPELL is still unanswered.
    /// The reference makes no distinction between a wand cast and a spell
    /// cast for its busy count: both hold the whole pass until they
    /// resolve, so a walk can never start under either.
    /// </summary>
    internal bool LearnedDebuffCastInFlight => _debuffs.HasPending;

    /// <summary>
    /// Set when the attack's own turn has already driven the held item's cast
    /// this frame, so the frame driver does not drive it a second time.
    /// </summary>
    private bool _heldItemDrivenByTurn;

    public string ButtonText => Enabled ? "Stop Macro" : "Run Macro";

    public void BindAmmunitionCraftRequest(
        Func<string, int, CraftingPlan?> resolve,
        Func<CraftingPlan, bool> request)
    {
        _resolveAmmunitionCraft = resolve
            ?? throw new ArgumentNullException(nameof(resolve));
        _requestAmmunitionCraft = request
            ?? throw new ArgumentNullException(nameof(request));
    }

    public Action<MacroLogChannel, string>? Log { get; set; }

    internal CombatModeGate Gate =>
        _gate ??= BindCombatModeGate(new CombatModeGate(
            _host,
            _settings,
            _vitalSettings,
            notice => Disable(notice)));

    private CombatModeGate? _gate;
    private AmmunitionPlan? _gateAmmunitionPlan;

    private CombatModeGate BindAmmunition(CombatModeGate gate)
    {
        gate.AmmunitionStale = (weapon, element) =>
        {
            _gateAmmunitionPlan = null;
            if (!_host.Automation.Equipment.IsAvailable || weapon == 0u)
                return false;
            AmmunitionPlan plan = ResolveAmmunitionPlan(
                PassEquipment(), weapon, element);
            if (plan.Kind == AmmunitionPlanKind.Unavailable)
            {
                // Said, not swallowed: the server answers a launcher with an
                // empty quiver by dropping the character out of combat, and
                // without a word here the fight just re-enters its mode.
                Status = plan.Notice;
                PostAttackWarning("Warning: " + plan.Notice + ".");
                return false;
            }
            if (plan.Kind is not (AmmunitionPlanKind.Wield
                or AmmunitionPlanKind.Craft))
                return false;
            _gateAmmunitionPlan = plan;
            return true;
        };
        gate.WieldAmmunition = _ =>
        {
            if (!_host.Automation.Equipment.IsAvailable
                || _gateAmmunitionPlan is not { } plan)
                return false;
            _gateAmmunitionPlan = null;
            return ExecuteAmmunitionPlan(PassEquipment(), plan);
        };
        return gate;
    }

    internal CombatModeGate BindCombatModeGate(CombatModeGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = BindAmmunition(gate);
        // The walk's creep band pushes out of peace mode on arrival exactly
        // as the route's does, so it needs the same gate.
        _approachMover.BindCombatModeGate(_gate, _settings);
        return _gate;
    }

    /// <summary>
    /// The once-per-run warning about walking at a goal tight enough to be
    /// crept at while in peace mode. One warning covers the whole run across
    /// every mover, so whoever keeps that bookkeeping hands it in here.
    /// </summary>
    internal void BindLowStopDistanceWarning(Action warn)
    {
        ArgumentNullException.ThrowIfNull(warn);
        _approachMover.WarnLowStopDistance = warn;
    }

    public void ClearActionLocks()
    {
        _host.Automation.Combat.AbortPhysicalAttack();
        ResetSwingExecutor();
        StopApproachMovement();
        StopBreakableTurnMovement();
        StopSelectionJiggle();
        _pendingPhysicalTarget = 0u;
        _pendingAttackSpell = 0u;
        _pendingAttackTarget = 0u;
        _lastTargetId = 0u;
        ClearPendingItemDebuff();
        _debuffs.ClearPending();
        Gate.Reset();
        _untilScan = 0d;
        if (Enabled)
            Status = "Action locks cleared";
    }

    public bool RecordFakeImperil(uint targetObjectId)
    {
        if (targetObjectId == 0u)
            return false;
        _debuffs.RecordFakeImperil(targetObjectId, _now);
        return true;
    }

    public void Toggle()
    {
        if (Enabled)
        {
            Disable("Macro stopped");
            _host.Automation.Chat.PostSystemMessage("[MossTank] Macro stopped.");
            return;
        }

        if (!_host.Automation.IsAvailable)
        {
            Status = "Not in world";
            return;
        }

        _combatControl = _host.Automation.Combat.AcquireCombatControl();
        Enabled = true;
        _paused = false;
        _combatPolicySuspended = !_settings.Enabled;
        _remoteCastCursor = 0;
        _untilScan = 0d;
        Status = _settings.Enabled ? "Scanning for targets" : "Combat disabled";
        _host.Automation.Chat.PostSystemMessage("[MossTank] Macro started.");
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused)
            return;
        _paused = paused;
        if (paused && Enabled)
        {
            // The attack losing its turn aborts the attack and the turn it
            // was holding -- its own work. It does NOT stop the walk to a
            // monster: that is a separate rule twenty positions below, with
            // its own lost-turn teardown, and it is often the very rule the
            // attack just lost the pass to. Stopping it here cancelled the
            // walk on the pass it was armed.
            DisarmSwingExecutor();
            StopBreakableTurnMovement();
            // The status is the attack's answer to "why did you decline",
            // and losing the turn is not an answer to that: whatever the
            // pass just worked out -- no target, waiting on the mode, the
            // bar still charging -- is the true reason and stands. Writing
            // one here renamed every decline after the fact.
        }
        else if (Enabled)
        {
            _untilScan = 0d;
        }
    }

    public bool EquipOneStepForMonster(string monsterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monsterName);
        ClearPassMemos();
        var target = new PluginCombatTarget(
            0u,
            monsterName.Trim(),
            0u,
            0f,
            0f,
            false,
            1f);
        _targetName = target.Name;
        _targetRule = _settings.ResolveRule(target);
        if (TickEquipment())
            return false;
        return TryPrepareAttack();
    }

    public void OnTick(double elapsedSeconds)
    {
        if (!Enabled)
            return;
        if (!_host.Automation.IsAvailable)
        {
            Disable("Session ended");
            return;
        }
        if (_paused)
            return;
        if (!_settings.Enabled)
        {
            if (_combatPolicySuspended)
                return;
            _combatPolicySuspended = true;
            if (_targetId != 0u || _pendingPhysicalTarget != 0u)
                _host.Automation.Combat.AbortPhysicalAttack();
            ResetSwingExecutor();
            _pendingPhysicalTarget = 0u;
            _pendingAttackSpell = 0u;
            _pendingAttackTarget = 0u;
            ClearPendingItemDebuff();
            _debuffs.Reset();
            _failures.Reset();
            ClearTarget();
            Status = "Combat disabled";
            return;
        }
        if (_combatPolicySuspended)
        {
            _combatPolicySuspended = false;
            _remoteCastCursor = 0;
            _untilScan = 0d;
            Status = "Scanning for targets";
        }

        ClearPassMemos();

        // The clock is wall time as this controller sees it: the turn hands
        // over the time since the rule was last asked, and whatever a frame
        // driver already added while the turn was away is not added twice.
        _lastElapsedSeconds = Math.Max(0d, elapsedSeconds);
        _now += Math.Max(0d, _lastElapsedSeconds - _frameAdvancedSinceTick);
        _frameAdvancedSinceTick = 0d;
        PluginCastCompletion castCompletion =
            _host.Automation.Magic.LastCompletion;
        ObserveSelectionJiggle(castCompletion);
        TickSelectionJiggle();
        OnLearnedDebuffCompleted(_debuffs.Observe(castCompletion, _now));
        _debuffs.ExpirePending(_now);

        PluginCombatSnapshot current = _host.Automation.Combat.Snapshot;
        ObserveItemDebuffReceipts();
        ObserveItemTransaction();
        ObserveAttackReceipts(current, castCompletion);
        if (current.Mode != _lastMode)
        {
            _lastMode = current.Mode;
            _modeText = $"Mode  {current.Mode}";
        }

        _untilGhostSweep -= Math.Max(0d, elapsedSeconds);
        if (_untilGhostSweep <= 0d)
        {
            _untilGhostSweep = GhostSweepIntervalSeconds;
            CheckStalledHealthGhost();
        }

        _untilScan -= Math.Max(0d, elapsedSeconds);
        if (_untilScan <= 0d)
        {
            // The attack's candidate pool is what the character can HIT. A
            // monster it would have to walk to is not a candidate here at all
            // — walking to one is a separate, much lower-priority job, so an
            // unreachable monster must not starve everything below the attack.
            _acquisitionRange = _settings.MaximumRange;
            _targets = _host.Automation.Combat.CaptureHostileTargets(
                (float)_acquisitionRange);
            Log?.Invoke(
                MacroLogChannel.Timers,
                $"Combat scan: {_targets.Count} hostile(s) within {_acquisitionRange:0.0}m");
            _failures.ObserveTargets(_targets, _now, _settings);
            foreach (PluginCombatTarget scanned in _targets)
            {
                _health.Observe(scanned, _now);
                // A live monster is where the species word for its name comes
                // from when the database does not list it.
                _settings.MonsterFacts.Learn(
                    scanned.SpeciesId,
                    scanned.SpeciesName);
            }
            _debuffs.RetainTargets(
                _targets.Select(static target => target.ObjectId).ToHashSet());
            _untilScan = Math.Max(0.05d, _settings.ScanIntervalSeconds);
            RefreshTarget();
        }

        if (_pendingItemDebuff is not null)
        {
            Status = $"Waiting on {_pendingItemDebuff.Source.Kind} debuff";
            _heldItemDrivenByTurn =
                _pendingItemDebuff.Source.Kind == CombatDebuffSourceKind.CasterItem;
            TickPendingItemDebuff(current);
            return;
        }

        // One monster the character cannot act against must not cost the whole
        // pass. When a decision turns out to be undeliverable, the monster's
        // offending action column is turned off (or the monster is dropped
        // outright) for the rest of THIS pass and the choice is made again
        // from what is left, until something can be carried out or nothing is
        // left to try.
        // With the projectile awareness off there is nothing to learn from an
        // undeliverable decision, so the pass gets one attempt: this coupling
        // is the reference's, not a convenience.
        int budget = _settings.UseProjectileAwareness
            ? Math.Max(1, _settings.MaximumCollisionChecksPerTick)
            : 1;
        try
        {
            RunAttackLoop(budget);
        }
        finally
        {
            // "The monster I picked last time" is a per-PASS memory, set once
            // when the pass is over. Inside the loop the pass has no opinion
            // yet, and the tie-break must not lose the term after the first
            // attempt clears the target.
            _lastTargetId = _targetId;
        }
    }

    /// <summary>
    /// Everything a pass learns and forgets again: what the character is
    /// carrying, which flights are blocked, which action columns this pass
    /// turned off, and the candidate pool built for one range. Both passes
    /// that pick a monster start from an empty memory of all of it.
    /// </summary>
    private void ClearPassMemos()
    {
        _passElements.Clear();
        _passDebuffSources.Clear();
        _passDebuffSpells.Clear();
        _passDeliverable.Clear();
        _passComponents.Clear();
        _passSpellRefusals.Clear();
        _passClearance.Clear();
        _passAmmunitionAvailability = null;
        _gateAmmunitionPlan = null;
        _passInvalidTargets.Clear();
        _passClearedActions.Clear();
        _passCandidates.Clear();
        _passTargetReasons.Clear();
        _passCandidateRange = double.NaN;
        _passEquipment = null;
        _passInventory = null;
        _passSelectionOrder = null;
    }

    private void RunAttackLoop(int budget)
    {
        for (int attempt = 0; attempt < budget; attempt++)
        {
            // The debuff choice is remade from scratch every time the pass
            // chooses again; what carries over between attempts is what the
            // pass LEARNED — which flights are blocked and which columns are
            // off.
            _passDebuffSources.Clear();
            if (attempt > 0)
                RefreshTarget();

            if (_targetId == 0u)
            {
                StopApproachMovement();
                Status = "Waiting for a target";
                return;
            }
            _cannotAttackReason = null;
            if (RunAttackAttempt() != AttackPassOutcome.Retry)
            {
                Log?.Invoke(
                    MacroLogChannel.Timers,
                    $"Attack evaluation complete. Loop iterations: {attempt + 1}");
                return;
            }
            Log?.Invoke(
                MacroLogChannel.RuleInfo,
                $"Attack: {_targetName} yielded nothing this pass ({Status}), choosing again");
            if (_cannotAttackReason is { } cannot)
                ReportGivenUpTarget(_targetId, _targetName, cannot);
            ClearTarget();
        }
        Log?.Invoke(
            MacroLogChannel.Timers,
            $"Attack evaluation complete. Loop iterations: {budget}");
    }

    /// <summary>
    /// What one turn of the decision did with the pass.
    /// </summary>
    private enum AttackPassOutcome
    {
        /// <summary>Something was issued, or is being waited on.</summary>
        Claimed,

        /// <summary>
        /// Nothing can be carried out against this monster; the pass should
        /// choose again from what is left.
        /// </summary>
        Retry,
    }

    /// <summary>
    /// The rule columns this one decision works from. A rolled element is
    /// rolled ONCE per decision, before the debuff chain, so the vulnerability
    /// the chain asks for and the spell the attack throws are the same element.
    /// </summary>
    private MonsterRuleActions DecisionActions =>
        _decisionActions ?? PassActions;

    /// <summary>
    /// The target's rule as THIS pass sees it: the stored rule minus whatever
    /// the pass has learned cannot be carried out.
    /// </summary>
    private MonsterRuleActions PassActions =>
        WithPassClearedActions(_targetId, _targetRule).Actions;

    private MonsterRuleActions? _decisionActions;

    private AttackPassOutcome RunAttackAttempt()
    {
        if (IsKnownDead(FindTarget(_targetId)))
        {
            // It died under the pass. The choice is made again from what is
            // left rather than at the next scan, which is a fight away.
            Status = $"{_targetName} is dead";
            InvalidateForPass(_targetId);
            return AttackPassOutcome.Retry;
        }
        StopApproachMovement();
        _decisionActions = ResolveRandomDamage(PassActions);

        PluginCombatSnapshot combat = _host.Automation.Combat.Snapshot;
        switch (TickDebuffs(combat))
        {
            case DebuffArmOutcome.Claimed:
                if (Status == "Waiting for a target")
                    Status = $"Debuffing {_targetName}";
                return AttackPassOutcome.Claimed;
            case DebuffArmOutcome.Retry:
                return AttackPassOutcome.Retry;
        }

        if (TickEquipment())
        {
            if (Status == "Waiting for a target")
                Status = $"Changing equipment for {_targetName}";
            return AttackPassOutcome.Claimed;
        }

        if (!DecisionActions.Attacks && !DecisionActions.UsesStreak)
        {
            Status = $"Debuffs complete for {_targetName}";
            InvalidateForPass(_targetId);
            return AttackPassOutcome.Retry;
        }

        // A shot the path cannot reach must not wait behind mode prep or a
        // busy inventory: those claim the turn every pass while the monster
        // stays behind its wall. Memoised per pass, so the check below is free.
        if (PlannedAttackMode(out _) == PluginCombatMode.Missile
            && YieldIfMissilePathBlocked())
            return AttackPassOutcome.Retry;

        if (!TryPrepareAttack())
        {
            // Equipment that cannot be settled is something to wait on; a
            // fight this character is not equipped for at all is not. The
            // second yields the monster so the rest of the pass -- and the
            // rules under the attack -- still get their turn.
            return _attackHasNoUsableWeapon
                ? AttackPassOutcome.Retry
                : AttackPassOutcome.Claimed;
        }

        combat = _host.Automation.Combat.Snapshot;

        if (combat.Mode == PluginCombatMode.Magic)
            return TickMagic();

        if (combat.Mode is not (PluginCombatMode.Melee or PluginCombatMode.Missile))
        {
            Status = $"Unsupported mode: {combat.Mode}";
            return AttackPassOutcome.Claimed;
        }

        return TickPhysical(combat);
    }

    private AttackPassOutcome TickPhysical(PluginCombatSnapshot combat)
    {
        // The repeat arm is inert while the macro drives combat: the host
        // only repeats an attack of its own accord when nothing holds combat
        // control, and the macro holds it for as long as it is running.
        if ((combat.ServerResponsePending || combat.RepeatAttackInProgress)
            && _pendingPhysicalTarget == 0u
            && !combat.ServerResponsePending
            && combat.SelectedObjectId != _targetId)
        {
            // The character repeats a swing of its own while the option
            // for it is on, and it repeats at whatever it was last
            // pointed at. This pass is waiting on no swing of its own,
            // and what is being swung at is not what it is fighting, so
            // the repeat is ended rather than waited on - otherwise the
            // pass stands still behind a monster it has finished with.
            _host.Automation.Combat.AbortPhysicalAttack();
            Status = "Ending a repeat at another monster";
            return AttackPassOutcome.Claimed;
        }

        // A swing already in the air belongs to the swing executor, which
        // runs on its own clock beside the pass. The pass does not touch it:
        // it steps the executor and gets out of the way.
        if (combat.RequestInProgress
            || combat.ServerResponsePending
            || combat.RepeatAttackInProgress)
        {
            if (!_swingTimerRunning)
            {
                // The client is still holding an attack, and nothing here is
                // steering it: it was begun at a monster that has since been
                // dropped -- killed while the next swing was still building.
                // Waiting on it waits for ever, so it is ended and this
                // monster gets its own swing on the next step.
                _host.Automation.Combat.AbortPhysicalAttack();
                Log?.Invoke(
                    MacroLogChannel.CastInfo,
                    "Swing: ended an attack left over from a dropped monster");
                Status = $"Ending a leftover attack before {_targetName}";
                return AttackPassOutcome.Claimed;
            }
            return TranslateSwingOutcome(PumpSwingExecutor());
        }

        IReadOnlyList<PluginInventoryItem> inventory =
            PassInventory();
        PluginCombatTarget physicalTarget = FindTarget(_targetId);
        MonsterRuleActions physicalActions = ResolvePhysicalActions(
            DecisionActions,
            physicalTarget,
            inventory);
        if (combat.Mode == PluginCombatMode.Missile
            && YieldIfMissilePathBlocked())
            return AttackPassOutcome.Retry;
        // The power table reads the element the attack actually resolved to,
        // so the bar and the wield plan cannot disagree. With the automatic
        // power off nothing is written to the bar at all: the player's own
        // setting stands.
        float desiredPower = AutoAttackPower.Resolve(
                physicalActions,
                ResolveAttackElement(physicalActions, physicalTarget),
                _settings,
                _host.Automation.Character,
                inventory)
            ?? combat.DesiredPower;
        // Every arm tears the turn down before it issues: a swing and a turn
        // both want the character, and the swing wins once it is armed.
        StopBreakableTurnMovement();
        // The pass's whole part in a physical attack: point the executor at
        // this monster, at this height and this power. When and how often the
        // swing actually goes out is the executor's business.
        ArmSwingExecutor(
            _targetId,
            _targetName,
            _settings.AttackHeight,
            desiredPower);
        return TranslateSwingOutcome(PumpSwingExecutor());
    }

    private static AttackPassOutcome TranslateSwingOutcome(
        SwingExecutorOutcome outcome) =>
        outcome == SwingExecutorOutcome.Retry
            ? AttackPassOutcome.Retry
            : AttackPassOutcome.Claimed;

    /// <summary>
    /// What one step of the swing executor leaves for the rule pass to do.
    /// </summary>
    private enum SwingExecutorOutcome
    {
        /// <summary>Nothing; the swing is the executor's business.</summary>
        None,

        /// <summary>
        /// The monster is out of play. The pass that hears this should choose
        /// again from what is left.
        /// </summary>
        Retry,
    }

    /// <summary>
    /// How often the swing executor may ask for a swing: roughly a quarter of
    /// a second, the reference's own repeat period.
    /// </summary>
    /// <remarks>
    /// There is no timer behind this. The executor is stepped by the host's
    /// fixed 15 ms plugin tick and by the rule pass, and it fires on the first
    /// step at or past its due instant — so the true period is this number
    /// rounded UP to a whole number of host ticks, 270 ms rather than 263 ms,
    /// and it can never fire twice in one tick.
    /// </remarks>
    private const double SwingRepeatSeconds = 0.263d;

    /// <summary>
    /// How long asking for a swing holds the shot slot. It is the floor on how
    /// fast the character may be asked to swing again, and it exists so the
    /// executor cannot press into the animation of the swing it just asked
    /// for.
    /// </summary>
    private const double SwingShotFloorSeconds = 0.75d;

    /// <summary>
    /// How long calling a swing OFF holds the shot slot — longer than a swing
    /// does, because the character has to come out of what it was doing before
    /// it can be asked for anything else.
    /// </summary>
    private const double SwingStopFloorSeconds = 1d;

    /// <summary>True while the executor has a monster to swing at.</summary>
    private bool _swingArmed;

    /// <summary>
    /// True while the executor is being stepped at all. The teardown stops it,
    /// the way the reference's own repeat timer is stopped, so a torn-down
    /// executor costs nothing per host tick.
    /// </summary>
    private bool _swingTimerRunning;

    private uint _swingTargetId;
    private string _swingTargetName = string.Empty;
    private PluginAttackHeight _swingHeight;
    private float _swingPower;

    /// <summary>The next instant the executor may ask for a swing.</summary>
    private double _nextSwingAt;

    /// <summary>
    /// How many times the teardown has run to completion. The teardown is not
    /// finished the first time: the call off is made, the shot slot goes up
    /// for a second, and only a second call off past that slot says the
    /// character is really standing still again.
    /// </summary>
    private int _swingStopCount;

    /// <summary>
    /// Point the executor at a monster. A monster it is already pointed at
    /// leaves the repeat clock alone — the whole value of the clock is that it
    /// keeps its own pace whatever the pass is doing — and a new one starts it
    /// over.
    /// </summary>
    private void ArmSwingExecutor(
        uint targetObjectId,
        string targetName,
        PluginAttackHeight height,
        float power)
    {
        if (_swingTargetId != targetObjectId)
        {
            // The host's own attack command selects the monster and swings at
            // it in one step, so there is nothing to spend a first step on:
            // the swing at a monster just taken up goes out at once, and the
            // repeat period governs every swing after it.
            _nextSwingAt = _now;
        }
        _swingTargetId = targetObjectId;
        _swingTargetName = targetName ?? string.Empty;
        _swingHeight = height;
        _swingPower = power;
        _swingArmed = true;
        _swingTimerRunning = true;
        _swingStopCount = 0;
    }

    /// <summary>
    /// One step of the executor: let go of a charged swing, watch one already
    /// on the wire, or ask for the next one. Stepped by the host frame and by
    /// the rule pass; it advances no clock of its own, so both callers can
    /// step it without the time being counted twice.
    /// </summary>
    private SwingExecutorOutcome PumpSwingExecutor()
    {
        if (!_swingTimerRunning || !Enabled || !_host.Automation.IsAvailable)
        {
            Status = "Swing timer is not running";
            return SwingExecutorOutcome.None;
        }

        _actionLocks.AdvanceTo(_now);
        PluginCombatSnapshot combat = _host.Automation.Combat.Snapshot;
        if (combat.RequestInProgress)
        {
            // The bar is ours to watch: the swing reaches the server when it
            // is let go, and letting it go a whole rule pass after it was
            // ready cost a fraction of a second on every swing.
            if (combat.BuildInProgress
                && combat.PowerBarLevel + PowerReleaseEpsilon
                    >= combat.DesiredPower)
            {
                return ReleaseChargedSwing();
            }
            Status = $"Charging {combat.PowerBarLevel * 100f:0}%";
            GiveUpOnSwingThatNeverWentOut();
            return SwingExecutorOutcome.None;
        }

        if (combat.ServerResponsePending || combat.RepeatAttackInProgress)
        {
            // The first step that finds the server holding the swing is the
            // latest moment it can have gone out, so a swing released by any
            // route but the branch above still starts its wait here.
            StampSwingSent();
            GiveUpOnUnansweredSwing();
            Status = $"Attacking {_swingTargetName}";
            return SwingExecutorOutcome.None;
        }

        if (!_swingArmed)
        {
            Status = "Swing is not armed";
            StopSwingExecutor();
            return SwingExecutorOutcome.None;
        }

        if (_now < _nextSwingAt)
        {
            Status = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Next swing at {_swingTargetName} in {_nextSwingAt - _now:0.0} s");
            return SwingExecutorOutcome.None;
        }
        _nextSwingAt = _now + SwingRepeatSeconds;
        return AskForSwing();
    }

    private SwingExecutorOutcome ReleaseChargedSwing()
    {
        PluginCombatCommandResult release =
            _host.Automation.Combat.ReleasePhysicalAttack();
        if (release.Status == PluginCombatCommandStatus.Released)
        {
            StampSwingSent();
            Status = $"Attacking {_swingTargetName}";
            return SwingExecutorOutcome.None;
        }
        Status = $"Attack release: {release.Status}";
        if (release.Status is not (PluginCombatCommandStatus.InvalidTarget
            or PluginCombatCommandStatus.Refused))
        {
            GiveUpOnSwingThatNeverWentOut();
            return SwingExecutorOutcome.None;
        }
        // The swing never left the client and the answer names the monster as
        // the reason - it has died, or it has gone out of play. There is
        // nothing to wait for, so it leaves the running and the choice is made
        // again from what is left. No attempt is charged: nothing reached the
        // server.
        Log?.Invoke(
            MacroLogChannel.CastInfo,
            $"Swing: {release.Status} releasing at {_swingTargetName} "
                + $"(0x{_swingTargetId:X8})"
                + (string.IsNullOrWhiteSpace(release.Notice)
                    ? string.Empty
                    : $" - {release.Notice}"));
        InvalidateForPass(_swingTargetId);
        ClearTarget();
        return SwingExecutorOutcome.Retry;
    }

    private SwingExecutorOutcome AskForSwing()
    {
        // The shot slot is the floor between one swing and the next. While it
        // is up the executor keeps its pace and asks for nothing.
        if (_actionLocks.IsLocked(ActionLockKind.MeleeAttackShot))
        {
            Status = $"Waiting out the interval between shots at {_swingTargetName}";
            return SwingExecutorOutcome.None;
        }

        PluginCombatCommandResult begin =
            _host.Automation.Combat.BeginPhysicalAttack(
                _swingTargetId,
                _swingHeight,
                _swingPower);
        Status = begin.Status switch
        {
            PluginCombatCommandStatus.Started => $"Charging {_swingTargetName}",
            PluginCombatCommandStatus.Busy => $"Waiting on {_swingTargetName}",
            PluginCombatCommandStatus.InvalidTarget => "Target disappeared",
            PluginCombatCommandStatus.WrongMode => "Waiting for combat mode",
            _ => $"Attack refused: {begin.Status}",
        };
        Log?.Invoke(
            MacroLogChannel.CastInfo,
            $"Swing: {begin.Status} at {_swingTargetName} (0x{_swingTargetId:X8})"
            + (string.IsNullOrWhiteSpace(begin.Notice)
                ? string.Empty
                : $" - {begin.Notice}"));
        if (begin.Status is PluginCombatCommandStatus.InvalidTarget
            or PluginCombatCommandStatus.Refused)
        {
            // Nothing armed, and the answer names the monster as the reason
            // as often as not: it has died, or it has gone out of play. There
            // is nothing to wait for either way, so it leaves the running and
            // the choice is made again from what is left. No attempt is
            // charged against it - an attempt counts for a request that
            // reached the server, and this one never did.
            _host.Automation.Combat.AbortPhysicalAttack();
            InvalidateForPass(_swingTargetId);
            ClearTarget();
            return SwingExecutorOutcome.Retry;
        }
        if (begin.Status == PluginCombatCommandStatus.Started)
        {
            // The floor starts here, where the reference starts it: at the
            // moment the swing is ASKED for. The charge that follows is this
            // client's own step, and putting the floor after it would let the
            // pace drift with the power setting.
            _actionLocks.Arm(
                ActionLockKind.MeleeAttackShot,
                SwingShotFloorSeconds);
            _pendingPhysicalTarget = _swingTargetId;
            // The press only starts the power bar: nothing has reached the
            // server yet, so the wait on an answer has not begun — the wait
            // on the bar moving at all does.
            _physicalSwingArmedAt = _now;
            _physicalSwingSentAt = double.NegativeInfinity;
            _physicalSwingClosestDistance = float.PositiveInfinity;
            ArmPhysicalResultText(_swingTargetId, _swingTargetName);
        }
        return SwingExecutorOutcome.None;
    }

    /// <summary>
    /// The executor's own teardown: call the swing off, put the stop floor up
    /// and stop stepping. It refuses to run while a swing is armed, and while
    /// the shot slot is up — which is what makes the second call off land a
    /// second later rather than on the same step.
    /// </summary>
    private void StopSwingExecutor()
    {
        if (_swingArmed
            || _actionLocks.IsLocked(ActionLockKind.MeleeAttackShot))
        {
            return;
        }
        _swingStopCount++;
        _swingTargetId = 0u;
        _swingTargetName = string.Empty;
        _host.Automation.Combat.AbortPhysicalAttack();
        _pendingPhysicalTarget = 0u;
        DisarmPhysicalResultText();
        _physicalSwingSentAt = double.NegativeInfinity;
        _physicalSwingArmedAt = double.NegativeInfinity;
        _physicalSwingClosestDistance = float.PositiveInfinity;
        _actionLocks.Arm(
            ActionLockKind.MeleeAttackShot,
            SwingStopFloorSeconds);
        _swingTimerRunning = false;
    }

    /// <summary>
    /// The attack rule losing its turn. The executor lets the monster go, the
    /// shot floor from its last swing comes down, and the teardown begins.
    /// Answers true once the character is really standing still again, which
    /// takes a second call off past the stop floor.
    /// </summary>
    private bool DisarmSwingExecutor()
    {
        if (_swingArmed)
        {
            _swingArmed = false;
            _actionLocks.Release(ActionLockKind.MeleeAttackShot);
            _swingStopCount = 0;
            _swingTimerRunning = true;
        }
        if (_swingStopCount <= 1)
            StopSwingExecutor();
        return _swingStopCount > 1;
    }

    /// <summary>
    /// The monster the executor was pointed at is finished with — killed, or
    /// out of play — and another one is about to be chosen. This is a hand
    /// over, not a teardown: no stop floor is charged for it, because the
    /// reference pays that floor only for standing down from the fight, never
    /// for moving from one monster to the next.
    /// </summary>
    private void ReleaseSwingExecutorForRetarget()
    {
        _swingArmed = false;
        _swingTimerRunning = false;
        _swingStopCount = 0;
        _swingTargetId = 0u;
        _swingTargetName = string.Empty;
    }

    /// <summary>
    /// The executor put down entirely, with no call off and no floor: the
    /// macro is stopping, or the whole action-lock table has been cleared.
    /// </summary>
    private void ResetSwingExecutor()
    {
        ReleaseSwingExecutorForRetarget();
        _nextSwingAt = 0d;
    }

    /// <summary>
    /// Steps the swing executor on the host's own frame, which is what makes
    /// it independent of the rule pass: a swing is let go the moment its bar
    /// is full and the next one is asked for on the executor's own period,
    /// whether or not the attack won the pass in between.
    /// </summary>
    internal void DriveSwingExecutor(double elapsedSeconds)
    {
        if (!_swingTimerRunning
            || !Enabled
            || _paused
            || !_host.Automation.IsAvailable)
        {
            return;
        }
        AdvanceClockFromFrame(elapsedSeconds);
        _ = PumpSwingExecutor();
    }

    /// <summary>
    /// Marks the instant the outstanding swing reached the server, which is
    /// what the wait below is measured from.
    /// </summary>
    private void StampSwingSent()
    {
        if (_pendingPhysicalTarget == 0u
            || !double.IsNegativeInfinity(_physicalSwingSentAt))
        {
            return;
        }
        _physicalSwingSentAt = _now;
        _physicalSwingArmedAt = double.NegativeInfinity;
        _physicalSwingClosestDistance = float.PositiveInfinity;
    }

    /// <summary>
    /// A swing that was asked for and never got out. The host stops the power
    /// bar while the character is in no position to attack, and if that never
    /// clears the request stands with nothing sent — no server answer to wait
    /// for, and no second swing allowed either. It is torn down so the next
    /// pass can ask again.
    ///
    /// No miss is charged for it. The reference counts an attempt against a
    /// monster when a request that went out produced no result, or when the
    /// server says the shot hit the environment; nothing here ever reached
    /// the monster, so it has failed nothing and must not be given up as
    /// unhittable for a stall on our own side.
    /// </summary>
    private void GiveUpOnSwingThatNeverWentOut()
    {
        if (double.IsNegativeInfinity(_physicalSwingArmedAt)
            || _now - _physicalSwingArmedAt < UnansweredSwingSeconds)
        {
            return;
        }
        _physicalSwingArmedAt = double.NegativeInfinity;
        _pendingPhysicalTarget = 0u;
        DisarmPhysicalResultText();
        _host.Automation.Combat.AbortPhysicalAttack();
        Log?.Invoke(
            MacroLogChannel.CastInfo,
            $"Swing: never left the client for {_targetName} "
                + $"(0x{_targetId:X8}) after {UnansweredSwingSeconds:0.0}s, "
                + "asking again");
    }

    private void GiveUpOnUnansweredSwing()
    {
        if (_pendingPhysicalTarget == 0u
            || double.IsNegativeInfinity(_physicalSwingSentAt))
        {
            return;
        }
        // Ground gained towards the monster is the swing working rather than
        // stalling — the server walks the character in before it strikes — so
        // the wait starts over on it. Only a range nearer than the nearest yet
        // seen counts, which bounds how often that can happen.
        float distance = FindTarget(_pendingPhysicalTarget).Distance;
        if (distance > 0f && distance < _physicalSwingClosestDistance)
        {
            // The first range seen is only the mark to measure from.
            bool closed = !float.IsPositiveInfinity(_physicalSwingClosestDistance)
                && distance + SwingClosingProgressMeters
                    <= _physicalSwingClosestDistance;
            _physicalSwingClosestDistance = distance;
            if (closed)
            {
                _physicalSwingSentAt = _now;
                return;
            }
        }
        if (_now - _physicalSwingSentAt < UnansweredSwingSeconds)
            return;
        uint stalled = _pendingPhysicalTarget;
        // The wait starts over whether or not the cancel is answered, so a
        // host that never reports the attack finished still lets the next
        // pass ask again rather than holding the macro here for good.
        _physicalSwingSentAt = _now;
        _host.Automation.Combat.AbortPhysicalAttack();
        Log?.Invoke(
            MacroLogChannel.CastInfo,
            $"Swing: no result from {_targetName} (0x{stalled:X8}) after "
                + $"{UnansweredSwingSeconds:0.0}s, cancelling");
    }

    private AttackPassOutcome TickMagic()
    {
        IMagicCommands magic = _host.Automation.Magic;
        if (magic.IsCasting)
        {
            Status = $"Casting at {_targetName}";
            return AttackPassOutcome.Claimed;
        }

        RefreshSpellCatalogs();
        _planKeptTheMonsterInPlay = false;
        PluginCombatTarget target = FindTarget(_targetId);
        MonsterRuleActions actions = DecisionActions;
        MonsterDamageType element = ResolveAttackElement(actions, target);

        if (actions.DamageType == MonsterDamageType.Fists
            && _attackCatalog.ResolveTuskerFists() is { } fists
            && IsUsableAttackSpell(target)(fists))
        {
            if (fists.IsProjectile && !ProjectilePathIsClear(_targetId,
                PluginProjectilePathKind.Straight, PluginAttackHeight.Medium,
                out PluginProjectilePathResult fistsPath))
            {
                Status = ProjectileStatus(fistsPath, _targetName);
                InvalidateForPass(_targetId);
                return AttackPassOutcome.Retry;
            }
            // Fists is the one arm that turns whatever the turning option
            // says, and it aims a shade off the monster's bearing.
            if (!FaceForFists(_targetId))
                return AttackPassOutcome.Claimed;
            CastAttackSpell(
                new AttackSpellChoice(
                    fists,
                    VtankCombatSpellType.War,
                    MonsterDamageType.Fists,
                    CastWithoutTarget: false),
                target);
            return AttackPassOutcome.Claimed;
        }

        bool flag3 = actions.UsesPrimaryAttack;
        bool flag4 = actions.UsesRing;
        bool flag5 = actions.UsesStreak;
        int ringCount = CountNearbyRingTargets();
        AttackSpellChoice? plan;

        if ((flag4 && ringCount >= _settings.MinimumRingTargets)
            || (flag4 && !flag3 && !flag5 && ringCount > 0))
        {
            plan = element == MonsterDamageType.DrainAuto
                ? PlanDrain(target, ring: true)
                : PlanRing(element, target) ?? PlanBoltOrArc(element, target);
        }
        else if ((flag3 && !flag5) || (!flag3 && !flag5 && flag4))
        {
            plan = element == MonsterDamageType.DrainAuto
                ? PlanDrain(target, ring: false)
                // A rolled element names its war spell outright instead of
                // walking the tiers, and what it names is the first rung.
                : _targetRule.Actions.DamageType == MonsterDamageType.Random
                    ? PlanRolledWar(element)
                    : PlanBoltOrArc(element, target);
        }
        else if (!flag3 && flag5)
        {
            plan = element == MonsterDamageType.DrainAuto
                ? PlanDrain(target, ring: false)
                : PlanStreak(element, target)
                    ?? WarnNoStreak(element, target);
        }
        else
        {
            if (!flag3 || !flag5)
            {
                Status = $"No attack configured for {_targetName}";
                _cannotAttackReason = "no attack is configured for it on the Monsters tab";
                // No action was decided at all: this monster is out of the
                // running for the rest of the pass.
                InvalidateForPass(_targetId);
                return AttackPassOutcome.Retry;
            }
            if (element == MonsterDamageType.DrainAuto)
            {
                plan = PlanDrain(target, ring: false);
            }
            else
            {
                AttackSpellChoice? streak = PlanStreak(element, target);
                plan = IsFinishingBlow(target, streak)
                    ? streak ?? WarnNoStreak(element, target)
                    : PlanBoltOrArc(element, target);
            }
        }

        if (plan is not { } chosen)
        {
            if (!Status.StartsWith(NoUsableAttackSpell, StringComparison.Ordinal)
                && _passSpellRefusals.Count > 0)
            {
                Status = NoUsableAttackSpellReason();
            }
            if (Status.StartsWith(NoUsableAttackSpell, StringComparison.Ordinal))
                _cannotAttackReason = Status;
            // No action could be decided at all, so the monster is out of the
            // running for the rest of the pass — unless a planner turned a
            // column off and left something else it might still be owed.
            if (!_planKeptTheMonsterInPlay)
                InvalidateForPass(_targetId);
            return AttackPassOutcome.Retry;
        }

        // A streak flies the way a bolt flies, so it is tested the way a bolt
        // is tested. Without this a streak is cast into a wall over and over
        // and the pass never learns the monster is unreachable.
        if (chosen.Type == VtankCombatSpellType.Streak
            && !ProjectilePathIsClear(
                _targetId,
                PluginProjectilePathKind.Straight,
                PluginAttackHeight.Medium,
                out PluginProjectilePathResult streakPath))
        {
            Status = ProjectileStatus(streakPath, _targetName);
            MonsterActionFlags off = MonsterActionFlags.Streak;
            // Nothing straight can reach it, and the arc could not either:
            // the attack column goes off with the streak column.
            if (!KnownClear(_targetId, PluginProjectilePathKind.Arc))
                off |= MonsterActionFlags.Attack;
            ClearActionsForPass(_targetId, off);
            return AttackPassOutcome.Retry;
        }

        CastAttackSpell(chosen, target);
        return AttackPassOutcome.Claimed;
    }

    /// <summary>
    /// The war spell a rolled element throws: the family's first rung, named
    /// outright rather than walked for the best the character can cast.
    /// </summary>
    private AttackSpellChoice? PlanRolledWar(MonsterDamageType element) =>
        _attackCatalog.ResolveBaseTier(element, VtankCombatSpellType.War)
            is { } spell
            ? new AttackSpellChoice(
                spell,
                VtankCombatSpellType.War,
                element,
                CastWithoutTarget: false)
            : null;

    /// <summary>
    /// The ring arm. Unlike every other arm it is admitted on ONE question —
    /// are the components for the family's first rung in the pack — with no
    /// skill or castability test; the rung actually thrown is the best the
    /// character can cast, and when that is nothing the cast is refused where
    /// every other refusal is reported.
    /// </summary>
    private AttackSpellChoice? PlanRing(
        MonsterDamageType element,
        in PluginCombatTarget target)
    {
        PluginSpellInfo? baseTier = _attackCatalog.ResolveBaseTier(
            element,
            VtankCombatSpellType.Ring);
        bool voidRing = element is MonsterDamageType.Nether
            or MonsterDamageType.VoidBasic;
        if (baseTier is null
            && voidRing
            && _host.Automation.Spells.TryGet(
                AttackSpellCatalog.VoidRingSpellId,
                out PluginSpellInfo catalogBase))
        {
            baseTier = catalogBase;
        }
        if (baseTier is not { } family
            || !HasCastingComponents(family.SpellId))
        {
            return null;
        }
        Func<PluginSpellInfo, bool> usable = IsUsableAttackSpell(target);
        PluginSpellInfo spell = (voidRing
            ? _attackCatalog.ResolveFamilyOf(family, usable)
            : _attackCatalog.Resolve(
                element,
                VtankCombatSpellType.Ring,
                usable)) ?? family;
        return new AttackSpellChoice(
            spell,
            VtankCombatSpellType.Ring,
            element,
            CastWithoutTarget: true);
    }

    /// <summary>
    /// The quality-walked streak line for this element.
    /// </summary>
    private AttackSpellChoice? PlanStreak(
        MonsterDamageType element,
        in PluginCombatTarget target)
    {
        PluginSpellInfo? streak = _attackCatalog.Resolve(
            element,
            VtankCombatSpellType.Streak,
            IsUsableAttackSpell(target));
        return streak is { } spell
            ? new AttackSpellChoice(
                spell,
                VtankCombatSpellType.Streak,
                element,
                CastWithoutTarget: false)
            : null;
    }

    /// <summary>
    /// The reference client's own warning text, then bolt/arc.
    /// </summary>
    private AttackSpellChoice? WarnNoStreak(
        MonsterDamageType element,
        in PluginCombatTarget target)
    {
        PostAttackWarning(
            $"No streak spell usable for element '{ElementName(element)}', "
            + "using bolt/arc instead.");
        return PlanBoltOrArc(element, target);
    }

    /// <summary>
    /// Is the monster hurt enough for the streak to be the finishing move?
    /// The bar is set by the size of the LAST blow, not by the streak's own
    /// difficulty — a character hitting for 200 finishes far earlier than one
    /// hitting for 20 — and the difficulty is only the stand-in until a real
    /// blow has been seen.
    /// </summary>
    private bool IsFinishingBlow(
        in PluginCombatTarget target,
        AttackSpellChoice? streak)
    {
        if (streak is not { } choice)
            return false;
        int threshold = _health.LastDamage <= 0
            ? choice.Spell.Difficulty / 7
            : _health.LastDamage / 7;
        int remaining = _health.RemainingHealth;
        return _health.TargetObjectId == target.ObjectId
            && remaining > 0
            && remaining < threshold;
    }

    private AttackSpellChoice? PlanBoltOrArc(
        MonsterDamageType element,
        in PluginCombatTarget target)
    {
        // These two are the decision's own record of which shape it has just
        // found blocked. They deliberately do NOT start from what the pass
        // learned earlier: a shape is offered once per decision and ruled out
        // by its own test, exactly as the reference macro does it.
        bool boltBlocked = false;
        bool arcBlocked = false;
        string? projectileRefusal = null;
        Func<PluginSpellInfo, bool> usable = IsUsableAttackSpell(target);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            PluginSpellInfo? bolt = boltBlocked
                ? null
                : _attackCatalog.Resolve(element, VtankCombatSpellType.War, usable);
            PluginSpellInfo? arc = arcBlocked
                ? null
                : _attackCatalog.Resolve(element, VtankCombatSpellType.Arc, usable);

            // Neither line exists for this element: warn and give up.
            if (bolt is null && arc is null)
            {
                PostAttackWarning(
                    "Warning: no usable attack spell detected for element \""
                    + ElementName(element) + "\"");
                Status = projectileRefusal ?? NoUsableAttackSpellReason();
                return null;
            }

            VtankCombatSpellType type;
            PluginSpellInfo spell;
            if (bolt is null)
            {
                type = VtankCombatSpellType.Arc;              // only an arc
                spell = arc!.Value;
            }
            else if (arc is null)
            {
                type = VtankCombatSpellType.War;              // only a bolt
                spell = bolt.Value;
            }
            else if (bolt.Value.Quality > arc.Value.Quality)
            {
                type = VtankCombatSpellType.War;              // better bolt
                spell = bolt.Value;
            }
            else if (arc.Value.Quality > bolt.Value.Quality)
            {
                type = VtankCombatSpellType.Arc;              // better arc
                spell = arc.Value;
            }
            else
            {
                // The setting decides ONLY on an exact quality tie.
                switch (_settings.UseArcs)
                {
                    case UseArcsMode.AtRange:
                        if (target.Distance >= _settings.ArcRange)
                        {
                            type = VtankCombatSpellType.Arc;
                            spell = arc.Value;
                        }
                        else
                        {
                            type = VtankCombatSpellType.War;
                            spell = bolt.Value;
                        }
                        break;
                    case UseArcsMode.Yes:
                        type = VtankCombatSpellType.Arc;
                        spell = arc.Value;
                        break;
                    default: // UseArcsMode.No, and the reference's own default
                        type = VtankCombatSpellType.War;
                        spell = bolt.Value;
                        break;
                }
            }

            bool arcShape = type == VtankCombatSpellType.Arc;
            PluginProjectilePathKind shape = arcShape
                ? PluginProjectilePathKind.Arc
                : PluginProjectilePathKind.Straight;
            if (spell.IsProjectile
                && !ProjectilePathIsClear(
                    _targetId,
                    shape,
                    HeightForShape(shape),
                    out PluginProjectilePathResult path))
            {
                projectileRefusal = ProjectileStatus(path, _targetName);
                Status = projectileRefusal;
                if (arcShape)
                {
                    arcBlocked = true;
                }
                else
                {
                    boltBlocked = true;
                    // A streak flies the same way a bolt does, so a bolt that
                    // cannot reach settles the streak too.
                    ClearActionsForPass(_targetId, MonsterActionFlags.Streak);
                }
                if (arcBlocked && boltBlocked)
                {
                    // Neither shape can reach: the attack column is off for
                    // this monster for the rest of the pass. The monster is
                    // NOT out of the running — a debuff or a ring may still be
                    // owed against it.
                    ClearActionsForPass(_targetId, MonsterActionFlags.Attack);
                    _planKeptTheMonsterInPlay = true;
                    return null;
                }
                continue;
            }
            return new AttackSpellChoice(
                spell,
                type,
                element,
                CastWithoutTarget: false);
        }
        return null;
    }

    /// <summary>
    /// A void caster's drain arm. The pick is not a preference list: it is the
    /// first step of the cheapest sequence of drains, martyrs and self-heals
    /// that finishes this monster off without dropping the caster below the
    /// health the recharge settings call normal.
    /// </summary>
    private AttackSpellChoice? PlanDrain(
        in PluginCombatTarget target,
        bool ring)
    {
        if (_health.TargetObjectId != target.ObjectId)
            return null;
        ICharacterInfo character = _host.Automation.Character;
        int health = (int)Math.Min(int.MaxValue, character.CurrentHealth);
        int maximumHealth = (int)Math.Min(int.MaxValue, character.MaxHealth);
        int targetHealth = _health.RemainingHealth;
        if (health == 0 || maximumHealth == 0 || targetHealth == 0)
            return null;
        // One point below the health the recharge rule calls normal, so a plan
        // that lands exactly on the threshold still counts as safe.
        int floor = (int)Math.Ceiling(
            maximumHealth * Math.Clamp(_vitalSettings.NormalHealth, 0d, 1d)) - 1;
        // A monster nothing magical can touch cannot be drained, and neither
        // can one whose health is not a knowable number.
        bool canDrain = !_settings.MonsterFacts.IsImmuneToMagic(target.Name)
            && targetHealth != int.MaxValue;

        PluginCombatTarget planTarget = target;
        Func<PluginSpellInfo, bool> usable = IsUsableAttackSpell(planTarget);
        uint spellId = CorpseDrainPlan.SelectSpell(
            health,
            maximumHealth,
            floor,
            targetHealth,
            canDrain,
            ring,
            _gameInfo.DrainSpellOptions,
            _gameInfo.MartyrSpellOptions,
            candidate => FindKnownSpell(candidate) is { } spell
                && usable(spell));
        if (spellId == 0u || FindKnownSpell(spellId) is not { } chosen)
        {
            Log?.Invoke(
                MacroLogChannel.DebuffChoice,
                $"Drain: nothing better than a heal against {target.Name}");
            return null;
        }
        Log?.Invoke(
            MacroLogChannel.DebuffChoice,
            $"Drain: {chosen.Name} ({targetHealth} left, floor {floor})");
        return new AttackSpellChoice(
            chosen,
            ring ? VtankCombatSpellType.Ring : VtankCombatSpellType.War,
            MonsterDamageType.DrainAuto,
            CastWithoutTarget: false);
    }

    /// <summary>A spell of the character's own, by id.</summary>
    private PluginSpellInfo? FindKnownSpell(uint spellId)
    {
        if (spellId == 0u)
            return null;
        foreach (PluginSpellInfo spell in _host.Automation.Spells.KnownCombatSpells)
        {
            if (spell.SpellId == spellId)
                return spell;
        }
        foreach (PluginSpellInfo spell in _host.Automation.Spells.KnownAttackSpells)
        {
            if (spell.SpellId == spellId)
                return spell;
        }
        return null;
    }

    private Func<PluginSpellInfo, bool> IsUsableAttackSpell(
        PluginCombatTarget target) => spell =>
        {
            string? refusal =
                SpellComponentPolicy.UsesBlacklistedComponent(
                    _host.Automation.Spells,
                    spell,
                    _settings.BlacklistedSpellComponents)
                    ? "uses a blacklisted component"
                : !HasCastingComponents(spell.SpellId)
                    ? "missing components"
                : !CanCastHuntSpell(spell)
                    ? "skill too low"
                : null;
            if (refusal is null)
                return true;
            _passSpellRefusals[spell.SpellId] = (spell.Name, spell.Quality, refusal);
            return false;
        };

    private const string NoUsableAttackSpell = "No usable attack spell";

    /// <summary>
    /// Every attack spell this pass looked at and turned down, and why.
    /// </summary>
    private readonly Dictionary<uint, (string Name, int Quality, string Reason)>
        _passSpellRefusals = [];

    /// <summary>
    /// Why the pass found no attack spell, in the player's terms: one clause
    /// per reason, naming the best spell turned down for it. With nothing
    /// turned down, no spell of the wanted kind is known at all.
    /// </summary>
    private string NoUsableAttackSpellReason()
    {
        if (_passSpellRefusals.Count == 0)
            return NoUsableAttackSpell + ": none of the wanted kind is known";
        IEnumerable<string> clauses = _passSpellRefusals.Values
            .GroupBy(static refusal => refusal.Reason, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group =>
            {
                string best = group
                    .OrderByDescending(static refusal => refusal.Quality)
                    .ThenBy(static refusal => refusal.Name, StringComparer.Ordinal)
                    .First().Name;
                int others = group.Count() - 1;
                return others == 0
                    ? $"{best}: {group.Key}"
                    : $"{best} and {others} lower: {group.Key}";
            });
        return NoUsableAttackSpell + " (" + string.Join("; ", clauses) + ")";
    }

    /// <summary>
    /// Says in chat why a monster was given up on: once per monster and
    /// reason, so every monster gets its answer and a standing one is not
    /// repeated each pass.
    /// </summary>
    private void ReportGivenUpTarget(uint targetId, string? name, string reason)
    {
        if (targetId == 0u
            || (_reportedGiveUps.TryGetValue(targetId, out string? said)
                && said.Equals(reason, StringComparison.Ordinal)))
        {
            return;
        }
        if (_reportedGiveUps.Count >= 256)
            _reportedGiveUps.Clear();
        _reportedGiveUps[targetId] = reason;
        _host.Automation.Chat.PostSystemMessage(
            $"[MossTank] Not attacking {name}: {reason}");
    }

    private readonly Dictionary<uint, string> _reportedGiveUps = [];

    /// <summary>
    /// Set by an attempt that gave a monster up because the character CANNOT
    /// attack it, as opposed to one that is merely finished with it.
    /// </summary>
    private string? _cannotAttackReason;

    /// <summary>
    /// A tier the pack cannot pay for is not a candidate. Without this the
    /// pick lands on the best spell known, the client refuses the cast, and
    /// the next pass picks the same spell again.
    /// </summary>
    private bool HasCastingComponents(uint spellId)
    {
        if (_passComponents.TryGetValue(spellId, out bool cached))
            return cached;
        bool answer = _host.Automation.Magic.HasComponents(spellId);
        _passComponents[spellId] = answer;
        return answer;
    }

    /// <summary>VTank's own element word in its warning text.</summary>
    private static string ElementName(MonsterDamageType element) => element switch
    {
        MonsterDamageType.Electric => "Lightning",
        MonsterDamageType.VoidBasic or MonsterDamageType.Nether => "Void",
        _ => element.ToString(),
    };

    private void PostAttackWarning(string text)
    {
        if (!_postedAttackWarnings.Add(text))
            return;
        _host.Automation.Chat.PostSystemMessage("[MossTank] " + text);
    }

    private readonly HashSet<string> _postedAttackWarnings =
        new(StringComparer.Ordinal);

    private void CastAttackSpell(
        AttackSpellChoice choice,
        in PluginCombatTarget target)
    {
        IMagicCommands magic = _host.Automation.Magic;
        if (!choice.CastWithoutTarget && choice.Spell.IsProjectile
            && !ProjectilePathIsClear(_targetId,
                choice.Type == VtankCombatSpellType.Arc
                    ? PluginProjectilePathKind.Arc : PluginProjectilePathKind.Straight,
                HeightForShape(choice.Type == VtankCombatSpellType.Arc
                    ? PluginProjectilePathKind.Arc : PluginProjectilePathKind.Straight),
                out PluginProjectilePathResult path))
        {
            Status = ProjectileStatus(path, _targetName);
            return;
        }
        // Finishing a cast of one attack school holds the other off for a few
        // seconds; a hybrid that fires inside that window is simply refused.
        if (_castTracker.IsSchoolLockedOut(choice.Spell.School))
        {
            Status = $"Waiting to cast at {_targetName}";
            return;
        }
        if (!choice.CastWithoutTarget
            && !ReadyForBreakableTurn(choice.Spell, _targetId))
        {
            return;
        }
        PluginCastGate gate = choice.CastWithoutTarget
            ? magic.EvaluateGate(choice.Spell.SpellId)
            : magic.EvaluateGate(choice.Spell.SpellId, _targetId);
        if (gate != PluginCastGate.Ready)
        {
            Status = gate == PluginCastGate.Busy
                ? $"Waiting to cast at {_targetName}"
                : $"Cannot cast {choice.Spell.Name}";
            return;
        }
        // A swing and a cast both want the character. Every magic arm tears
        // the swing loop down before it issues, so a leftover physical attack
        // cannot keep running underneath the cast.
        if (_physicalResultArmed || _pendingPhysicalTarget != 0u)
        {
            _host.Automation.Combat.AbortPhysicalAttack();
            DisarmPhysicalResultText();
            _pendingPhysicalTarget = 0u;
        }
        bool dispatched = choice.CastWithoutTarget
            ? magic.Cast(choice.Spell.SpellId)
            : magic.Cast(choice.Spell.SpellId, _targetId);
        if (!dispatched)
        {
            Status = $"Could not start {choice.Spell.Name}";
            return;
        }

        // The reference client's own SpellCast log line.
        Log?.Invoke(
            MacroLogChannel.SpellCast,
            $"Casting: {choice.Spell.Name} on {_targetId} ({_targetName})");
        Status = choice.Type == VtankCombatSpellType.Ring
            ? $"{choice.Spell.Name} around {_targetName}"
            : $"{choice.Spell.Name} → {_targetName}";

        bool selfCast = _targetId != 0u
            && _targetId == _host.Automation.Character.ObjectId;
        _castTracker.Begin(
            choice.Spell.SpellId,
            choice.Spell.Name,
            choice.CastWithoutTarget ? 0u : _targetId,
            choice.CastWithoutTarget
                ? string.Empty
                : selfCast ? "yourself" : _targetName,
            HitsMultipleTargets(choice.Spell),
            choice.Spell.Saying,
            choice.Spell.School,
            SpellCastTracker.CanKillFor(choice.Spell),
            checked((int)Math.Min(
                int.MaxValue,
                _host.Automation.Character.CurrentMana)),
            targetIncarnation: choice.CastWithoutTarget ? (ushort)0 : FindTarget(_targetId).Incarnation);
        Log?.Invoke(MacroLogChannel.CastInfo, "SpellCaster: Begin");
        if (!choice.CastWithoutTarget)
        {
            _pendingAttackSpell = choice.Spell.SpellId;
            _pendingAttackTarget = _targetId;
        }
    }

    private MonsterRuleActions ResolveRandomDamage(MonsterRuleActions actions)
    {
        if (actions.DamageType != MonsterDamageType.Random)
            return actions;

        MonsterDamageType damage = RandomDamageCycle[_randomDamageIndex];
        _randomDamageIndex = (_randomDamageIndex + 1) % RandomDamageCycle.Length;
        return actions with { DamageType = damage };
    }

    /// <summary>
    /// The skill margin the attack-spell tier walk asks for. Range is
    /// deliberately NOT part of this: the attack pick never asks how far a
    /// spell reaches — only the debuff choice does.
    /// </summary>
    private bool CanCastHuntSpell(in PluginSpellInfo spell)
    {
        if (spell.School == 0u
            || !_host.Automation.Character.TryGetSkill(
                spell.School,
                out PluginSkillInfo skill))
        {
            return true;
        }
        return skill.Current >= spell.Difficulty
            + _settings.HuntSkillExcessOverDifficulty;
    }

    /// <summary>
    /// How many monsters a ring would actually catch. Only monsters the pass
    /// has accepted as candidates are counted — one that is blacklisted, too
    /// near, ignored, or refusing to be attacked before it is debuffed is not
    /// going to be hit and must not push the tally over the threshold — and
    /// the ring boundary itself is outside the ring.
    /// </summary>
    private int _ringCandidateCount;

    private int CountNearbyRingTargets() => _ringCandidateCount;

    /// <summary>
    /// Set when the weapon walk was asked for an automatic choice and came
    /// back with nothing at all: no listed item the character owns could be
    /// wielded against this monster.
    /// </summary>
    private bool _plannedWeaponSearchFoundNothing;

    private bool TickEquipment()
    {
        _plannedWeapon = 0u;
        _plannedOffhand = null;
        _plannedWeaponSearchFoundNothing = false;

        MonsterRuleActions actions = _targetRule.Actions;
        bool primaryRequiresWeapon = actions.UsesPrimaryAttack
            || actions.UsesRing;
        if (!primaryRequiresWeapon && !_settings.SwitchWandsToDebuff)
            return false;
        IEquipmentAutomation equipment = _host.Automation.Equipment;
        if (!equipment.IsAvailable)
        {
            return false;
        }
        IReadOnlyList<PluginEquipmentItem> items = PassEquipment();
        uint desiredWeapon;
        if (actions.WeaponToUseRaw == 0)
        {
            // A weapon column spelled as zero means "no weapon": the rule
            // wants a wand, and nothing is auto-selected for it.
            desiredWeapon = 0u;
        }
        else
        {
            desiredWeapon = ResolveRuleWeaponObjectId(
                actions,
                items,
                out bool unlisted);
            if (unlisted)
            {
                PostAttackWarning(
                    "Warning: this rule's Weapon column names an item that is "
                    + "not in the Items list, so it will not be wielded. Add "
                    + "it to the Items list, or set the column back to "
                    + "<AUTO>.");
            }
            if (desiredWeapon == 0u)
            {
                desiredWeapon = SelectAutomaticWeapon(
                    items,
                    actions,
                    FindTarget(_targetId));
                _plannedWeaponSearchFoundNothing =
                    NothingListedCanFight(items, desiredWeapon);
            }
        }
        _plannedWeapon = desiredWeapon;
        _plannedOffhand = PlannedSecondaryFor(
            actions, items, desiredWeapon, PassInventory);
        return false;
    }

    private enum AmmunitionPlanKind
    {
        /// <summary>Not a launcher, or the right stack is already wielded.</summary>
        Satisfied,
        Wield,
        Craft,
        Unavailable,
    }

    private readonly record struct AmmunitionPlan(
        AmmunitionPlanKind Kind,
        uint ObjectId,
        string Name,
        string Notice,
        CraftingPlan? Craft = null)
    {
        public static AmmunitionPlan Satisfied { get; } = new(
            AmmunitionPlanKind.Satisfied,
            0u,
            string.Empty,
            string.Empty);
    }

    private AmmunitionPlan ResolveAmmunitionPlan(
        IReadOnlyList<PluginEquipmentItem> equipmentItems,
        uint desiredWeapon,
        MonsterDamageType configuredDamage)
    {
        PluginEquipmentItem launcher = equipmentItems.FirstOrDefault(
            item => item.ObjectId == desiredWeapon);
        int launcherType = VtankAmmunitionDatabase.LauncherType(in launcher);
        if (launcherType == 0)
            return AmmunitionPlan.Satisfied;
        if (!_gameInfo.IsLoaded)
            return new AmmunitionPlan(
                AmmunitionPlanKind.Unavailable, 0u, string.Empty,
                "Ammunition database is unavailable");

        MonsterDamageType damage = configuredDamage;
        VtankPrismaticAmmoPolicy prismatic = VtankPrismaticAmmoPolicy.NoPrismatic;
        if (damage == MonsterDamageType.Auto)
        {
            damage = ResolveAttackElement(
                _targetRule.Actions,
                FindTarget(_targetId));
            prismatic = VtankPrismaticAmmoPolicy.Any;
        }
        else if (damage == MonsterDamageType.Prismatic)
        {
            prismatic = VtankPrismaticAmmoPolicy.ForcePrismatic;
        }
        IReadOnlyList<PluginInventoryItem> inventory =
            PassInventory();
        var counts = inventory
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(item => item.StackSize),
                StringComparer.Ordinal);
        var craftable = new Dictionary<string, CraftingPlan?>(StringComparer.Ordinal);
        bool IsAvailable(string name)
        {
            if (counts.GetValueOrDefault(name) >= 1)
                return true;
            if (!craftable.TryGetValue(name, out CraftingPlan? cached))
            {
                cached = _resolveAmmunitionCraft?.Invoke(name, 1);
                craftable[name] = cached;
            }
            return cached is not null;
        }

        // The one AmmunitionOptions table is the game database's, read the
        // way VTank reads it; there is no other to fall back on.
        VtankAmmunitionOption? selected = VtankAmmunitionDatabase.Select(
            _gameInfo.AmmunitionOptions,
            launcherType,
            damage,
            prismatic,
            _settings.UseSpecialAmmo,
            _host.Automation.Character,
            IsAvailable);
        if (selected is not { } option)
        {
            return new AmmunitionPlan(
                AmmunitionPlanKind.Unavailable,
                0u,
                string.Empty,
                $"No {damage} ammunition is available");
        }

        // The wielded stack already IS the winning row.
        // The stack already in the quiver only satisfies the row while it
        // still holds something: an empty quiver of the right name is not
        // ammunition.
        PluginEquipmentItem currentAmmo = equipmentItems.FirstOrDefault(
            static item => item.EquippedLocation == 0x00800000u);
        if (currentAmmo.StackSize > 0
            && string.Equals(currentAmmo.Name, option.Name, StringComparison.Ordinal))
        {
            return AmmunitionPlan.Satisfied;
        }

        PluginEquipmentItem desiredAmmo = default;
        foreach (PluginEquipmentItem item in equipmentItems)
        {
            if (item.Name.Equals(option.Name, StringComparison.Ordinal)
                && item.StackSize > desiredAmmo.StackSize)
                desiredAmmo = item;
        }
        return desiredAmmo.ObjectId != 0u
            ? new AmmunitionPlan(
                AmmunitionPlanKind.Wield,
                desiredAmmo.ObjectId,
                option.Name,
                string.Empty)
            : new AmmunitionPlan(
                AmmunitionPlanKind.Craft,
                0u,
                option.Name,
                string.Empty,
                craftable.GetValueOrDefault(option.Name));
    }

    private bool ExecuteAmmunitionPlan(
        IReadOnlyList<PluginEquipmentItem> equipmentItems,
        in AmmunitionPlan plan)
    {
        switch (plan.Kind)
        {
            case AmmunitionPlanKind.Satisfied:
                return false;
            case AmmunitionPlanKind.Unavailable:
                Status = plan.Notice;
                return true;
        }

        if (!Gate.TryDropToPeace(equipmentItems, plan.Name))
            return true;

        if (plan.Kind == AmmunitionPlanKind.Wield)
        {
            if (!Gate.TryArmAmmunitionSwap(plan.ObjectId))
            {
                Status = "Waiting for equipment";
                return true;
            }
            PluginEquipmentCommandResult equip =
                _host.Automation.Equipment.Equip(plan.ObjectId);
            Status = equip.Status == PluginEquipmentCommandStatus.Refused
                ? equip.Notice ?? $"Cannot equip {plan.Name}."
                : $"Equipping {plan.Name}";
            return true;
        }

        if (plan.Craft is { } craft
            && _requestAmmunitionCraft?.Invoke(craft) == true)
        {
            Status = "Crafting " + plan.Name;
            return true;
        }
        Status = $"Waiting to craft {plan.Name}";
        return true;
    }

    private MonsterRuleActions ResolvePhysicalActions(
        MonsterRuleActions actions,
        in PluginCombatTarget target,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        if (actions.DamageType != MonsterDamageType.Auto)
            return actions;

        IReadOnlyList<MonsterDamageType> preferences =
            _gameInfo.DamagePreferences(target.Name);
        foreach (MonsterDamageType damage in preferences)
        {
            int mask = RawDamageType(damage);
            if (mask == 0)
                continue;
            foreach (PluginInventoryItem item in inventory)
            {
                if (item.IsEquipped && (item.DamageType & mask) != 0)
                    return actions with { DamageType = damage };
            }
        }
        return preferences.Count == 0
            ? actions
            : actions with { DamageType = preferences[0] };
    }

    /// <summary>
    /// The element the attack would strike this monster with. The summon rule
    /// ranks essences by it.
    /// </summary>
    internal MonsterDamageType AttackElementFor(PluginCombatTarget target) =>
        ResolveAttackElement(_settings.ResolveRule(target).Actions, in target);

    /// <summary>Posts a warning once, beside the attack's own.</summary>
    internal void PostWarningOnce(string text) => PostAttackWarning(text);

    internal bool ReadyToActInPeace() => Gate.TryDropToPeace(
        _host.Automation.Equipment.IsAvailable
            ? _host.Automation.Equipment.CaptureOwnedEquipment()
            : [],
        "the combat pet");

    /// <summary>
    /// Set when the pass found nothing it could fight this monster WITH, as
    /// opposed to equipment that simply has not settled yet.
    /// </summary>
    private bool _attackHasNoUsableWeapon;

    /// <summary>
    /// The combat mode the planned weapon needs, Magic until an owned item
    /// backs the plan; <paramref name="plannedWeapon"/> is that item's id, or
    /// zero when nothing backs it.
    /// </summary>
    private PluginCombatMode PlannedAttackMode(out uint plannedWeapon)
    {
        plannedWeapon = 0u;
        if (_plannedWeapon == 0u || !_host.Automation.Equipment.IsAvailable)
            return PluginCombatMode.Magic;
        foreach (PluginEquipmentItem item in PassEquipment())
        {
            if (item.ObjectId != _plannedWeapon)
                continue;
            plannedWeapon = _plannedWeapon;
            return CombatModeGate.ModeFor(in item);
        }
        return PluginCombatMode.Magic;
    }

    /// <summary>
    /// A missile shot the path cannot reach: this monster is not attackable
    /// this pass, so the choice is made again from what is left.
    /// </summary>
    private bool YieldIfMissilePathBlocked()
    {
        if (ProjectilePathIsClear(
                _targetId,
                PluginProjectilePathKind.Missile,
                _settings.AttackHeight,
                out PluginProjectilePathResult missilePath))
            return false;
        Status = ProjectileStatus(missilePath, _targetName);
        ClearActionsForPass(
            _targetId,
            MonsterActionFlags.Attack | MonsterActionFlags.Streak);
        return true;
    }

    private bool TryPrepareAttack()
    {
        _attackHasNoUsableWeapon = false;
        // The plan is Magic until an owned item backs it. Without an
        // equipment projection nothing can back it, but the combat mode is
        // still a hard requirement: answering "ready" here would let the pass
        // try to swing or cast out of peace mode and quietly do nothing.
        IEquipmentAutomation equipment = _host.Automation.Equipment;
        PluginCombatMode wanted = PlannedAttackMode(out uint plannedWeapon);

        // Nothing in the profile's item list can fight this monster: the
        // walk that picks a weapon found no candidate at all. Wielding a wand
        // on the strength of that would put the fight into magic mode with
        // nothing to throw, and every monster in reach would be written off
        // for the pass with no word said about why. The fight says what is
        // missing instead, once, and leaves the pass to the rules below it.
        if (_plannedWeaponSearchFoundNothing)
        {
            const string notice =
                "No weapon in the profile's item list can be used against "
                + "this monster. Add the weapon you fight with to the Items "
                + "list.";
            PostAttackWarning("Warning: " + notice);
            Status = notice;
            _attackHasNoUsableWeapon = true;
            NoteTargetReason(_targetId, "no usable listed weapon");
            ClearActionsForPass(
                _targetId,
                MonsterActionFlags.Attack | MonsterActionFlags.Streak
                    | MonsterActionFlags.Ring);
            return false;
        }

        if (Gate.TryPrepare(
                wanted,
                overrideItemId: plannedWeapon,
                autoSelect: plannedWeapon == 0u,
                element: _targetRule.Rule is null
                    ? MonsterDamageType.None
                    : _targetRule.Actions.DamageType,
                captured: PassEquipment(),
                secondaryItemId: _plannedOffhand))
        {
            return true;
        }
        Status = Gate.Status;
        return false;
    }

    /// <summary>
    /// The weapon automatic selection reaches for against this monster.
    /// </summary>
    private uint SelectAutomaticWeapon(
        IReadOnlyList<PluginEquipmentItem> items,
        MonsterRuleActions actions,
        in PluginCombatTarget target)
    {
        // An automatic rule wants whatever the monster is weak to; a rule that
        // spells its element out wants only that one.
        IReadOnlyList<MonsterDamageType> wanted =
            actions.DamageType is MonsterDamageType.Auto
                or MonsterDamageType.Prismatic
                ? _gameInfo.DamagePreferences(target.Name)
                : [actions.DamageType];
        PluginCombatTarget subject = target;
        ICharacterInfo character = _host.Automation.Character;

        bool IsSelectable(PluginEquipmentItem item) =>
            CombatProfileItems.IsProfiled(_settings, item.ObjectId, item.Name)
            && (VtankItemUseSpecifiers.UsesFor(_settings, item.ObjectId) & 1) != 0
            && ConfiguredSupplyReadiness.IsAssessed(_host.Automation, item.ObjectId);

        uint Walk(Func<PluginEquipmentItem, bool> selectable, Action? lastResort) =>
            VtankWeaponLadder.Select(
                InProfileOrder(items),
                selectable,
                wanted,
                SpeciesOf(in subject),
                (item, element) => CanWeaponDeliver(in item, element),
                element => IsAlreadyVulnerable(in subject, element),
                warTrained: IsTrained(character, WarMagicSkill),
                voidTrained: IsTrained(character, VoidMagicSkill),
                excludeObjectId: actions.OffhandObjectId,
                onLastResort: lastResort);

        uint chosen = Walk(IsSelectable, () => PostAttackWarning(
            "Warning: no weapons found that can be autoselected for current "
            + "target. Add some weapons to the items list!"));

        // A wand is what a rule that casts for a living asks for outright. It
        // is not what a rule that SWINGS should be handed because the monster
        // in front of it happens not to match any listed weapon's element:
        // that answer changes with every monster, so the character put its
        // weapon away after each kill, took the wand out, and took the weapon
        // back for the monster after that. Whenever something that can
        // actually strike is listed and in the pack, the fight keeps that.
        if (chosen != 0u && IsProfiledCaster(items, chosen))
        {
            uint striking = Walk(
                item => IsSelectable(item) && !CombatModeGate.IsCaster(in item),
                lastResort: null);
            if (striking != 0u)
                chosen = striking;
        }
        return chosen;
    }

    /// <summary>
    /// Whether the automatic choice has left the fight with nothing it can
    /// strike or cast with.
    ///
    /// The choice coming back empty, or coming back with a wand this
    /// character can cast no war or void magic from, is not by itself the
    /// answer: a character holding nothing, or holding a wand, still has the
    /// magic arm to fall back on, and that is what the fight has always done.
    /// It IS the answer when the character is standing there holding a weapon
    /// the profile does not list: putting that weapon away for a wand it
    /// cannot throw anything with leaves it swinging at nothing, and the
    /// choice flips back and forth as each new monster is measured.
    /// </summary>
    private bool NothingListedCanFight(
        IReadOnlyList<PluginEquipmentItem> items, uint chosen)
    {
        if (chosen != 0u && !IsProfiledCaster(items, chosen))
            return false;
        ICharacterInfo character = _host.Automation.Character;
        if (IsTrained(character, WarMagicSkill)
            || IsTrained(character, VoidMagicSkill))
        {
            return false;
        }
        return CombatModeGate.FindWielded(items) is { } worn
            && !CombatModeGate.IsCaster(in worn);
    }

    private static bool IsProfiledCaster(
        IReadOnlyList<PluginEquipmentItem> items, uint objectId)
    {
        foreach (PluginEquipmentItem item in items)
        {
            if (item.ObjectId == objectId)
                return CombatModeGate.IsCaster(in item);
        }
        return false;
    }

    /// <summary>
    /// The owned items in the order the Items page fixes: what the page names
    /// first, in page order, then whatever it does not name, and the object id
    /// inside a group the page cannot tell apart.
    ///
    /// The walk that picks a weapon keeps the LAST candidate it saw at every
    /// rung, so its answer is only as steady as the order it walks. What the
    /// host hands out is sorted with the held items first, and that order
    /// turns over the instant something is wielded: two equally rated items of
    /// the same name pass the pick back and forth, and the character swaps
    /// weapons for ever instead of fighting. The page's order is the one thing
    /// in this list that a fight cannot move.
    /// </summary>
    private IReadOnlyList<PluginEquipmentItem> InProfileOrder(
        IReadOnlyList<PluginEquipmentItem> items)
    {
        if (_passSelectionOrder is { } memo && ReferenceEquals(memo.Source, items))
            return memo.Ordered;
        if (items.Count < 2)
            return items;

        IList<string> page = _settings.CombatItemOrder;
        int PageIndex(string name)
        {
            for (int index = 0; index < page.Count; index++)
            {
                if (string.Equals(page[index], name, StringComparison.Ordinal))
                    return index;
            }
            return int.MaxValue;
        }

        var ordered = new List<PluginEquipmentItem>(items);
        ordered.Sort((left, right) =>
        {
            int placed = PageIndex(left.Name).CompareTo(PageIndex(right.Name));
            if (placed != 0)
                return placed;
            int name = string.CompareOrdinal(left.Name, right.Name);
            return name != 0
                ? name
                : left.ObjectId.CompareTo(right.ObjectId);
        });
        _passSelectionOrder = (items, ordered);
        return ordered;
    }

    private (IReadOnlyList<PluginEquipmentItem> Source,
        IReadOnlyList<PluginEquipmentItem> Ordered)? _passSelectionOrder;

    /// <summary>
    /// The monster's species, or -1 when the game-info database does not name
    /// it — which no weapon's slayer type can match.
    /// </summary>
    private int SpeciesOf(in PluginCombatTarget target)
    {
        if (target.SpeciesId > 0)
            return target.SpeciesId;
        return _gameInfo.SpeciesMembers.TryGetValue(
            target.Name ?? string.Empty,
            out VtankSpeciesMember member)
            ? member.Species
            : -1;
    }

    private bool IsAlreadyVulnerable(
        in PluginCombatTarget target,
        MonsterDamageType element)
    {
        var identity = new DebuffIdentity(
            MonsterActionFlags.Vulnerability,
            element);
        if (FindDebuffSpell(identity) is not { } vulnerability)
            return false;
        // "Still up in half a second's time", which is the same question as
        // "not due within half a second".
        return !_debuffs.IsDue(
            target.ObjectId,
            identity,
            vulnerability,
            _now,
            0.5d);
    }

    private bool CanWeaponDeliver(
        in PluginEquipmentItem item,
        MonsterDamageType element)
    {
        int launcherType = VtankAmmunitionDatabase.LauncherType(in item);
        if (launcherType == 0)
            return true;
        if (!_gameInfo.IsLoaded)
            return false;
        (MonsterDamageType, uint) key = (element, item.ObjectId);
        if (_passDeliverable.TryGetValue(key, out bool cached))
            return cached;
        bool deliverable = VtankAmmunitionDatabase.Select(
            _gameInfo.AmmunitionOptions,
            launcherType,
            element,
            VtankPrismaticAmmoPolicy.Any,
            _settings.UseSpecialAmmo,
            _host.Automation.Character,
            AmmunitionAvailability()) is not null;
        _passDeliverable[key] = deliverable;
        return deliverable;
    }

    private static int RawDamageType(MonsterDamageType damageType) =>
        damageType switch
        {
            MonsterDamageType.Slash => 0x0001,
            MonsterDamageType.Pierce => 0x0002,
            MonsterDamageType.Bludgeon => 0x0004,
            MonsterDamageType.Cold => 0x0008,
            MonsterDamageType.Fire => 0x0010,
            MonsterDamageType.Acid => 0x0020,
            MonsterDamageType.Electric => 0x0040,
            MonsterDamageType.Nether => 0x0400,
            _ => 0,
        };

    /// <summary>
    /// The debuff arm of one decision.
    /// </summary>
    /// <returns>
    /// What the arm did with the pass: nothing (fall through to the attack),
    /// claimed it, or turned a debuff column off — in which case the whole
    /// choice is made again, because a monster whose chain just lost a step
    /// may no longer be the one worth acting on.
    /// </returns>
    private DebuffArmOutcome TickDebuffs(PluginCombatSnapshot combat)
    {
        if (_debuffs.HasPending)
        {
            Status = _host.Automation.Magic.IsCasting
                ? $"Casting {_debuffs.PendingName}"
                : $"Waiting for {_debuffs.PendingName}";
            return DebuffArmOutcome.Claimed;
        }

        // The server refused the last one because the character was already
        // doing something. The cast is still owed, and it holds the arm the
        // same way an unanswered one does — asking again on the very next pass
        // is how a single refusal turns into fifty requests a second.
        if (_debuffs.RetryHeld(_now))
        {
            Status = "Waiting after a busy refusal";
            return DebuffArmOutcome.Claimed;
        }

        RefreshSpellCatalogs();
        IReadOnlyList<PluginInventoryItem> items =
            PassInventory();
        PluginCombatTarget target = FindTarget(_targetId);
        if (target.ObjectId == 0u)
            return DebuffArmOutcome.Idle;

        MonsterRuleActions actions = DecisionActions;
        IReadOnlyList<CombatDebuffStep> steps = CombatDebuffChain.Build(
            actions,
            ResolveAttackElement(actions, target),
            ResolveExtraVulnerability(actions, target));

        if (CombatDebuffChain.Choose(
                steps,
                step => IsDebuffStepDue(step, in target, items))
            is not { } due)
        {
            return DebuffArmOutcome.Idle;
        }
        DebuffPassResult result = TickDebuffStep(
            due,
            actions,
            target,
            combat,
            items);
        if (result != DebuffPassResult.ColumnDisabled)
        {
            return result == DebuffPassResult.Claimed
                ? DebuffArmOutcome.Claimed
                : DebuffArmOutcome.Idle;
        }

        // The step cannot be delivered: its column goes off for the rest of
        // this pass and the whole choice is made again, rather than walking
        // on to the next step against a monster that may no longer be the one
        // worth acting on.
        ClearActionsForPass(_targetId, due.Identity.Flag);
        return DebuffArmOutcome.Retry;
    }

    /// <summary>What the debuff arm did with the pass.</summary>
    private enum DebuffArmOutcome
    {
        Idle,
        Claimed,
        Retry,
    }

    /// <summary>
    /// What one turn of the debuff arm did with the pass.
    /// </summary>
    private enum DebuffPassResult
    {
        Idle,

        /// <summary>Something was issued (or is being waited on).</summary>
        Claimed,

        ColumnDisabled,
    }

    private DebuffPassResult TickDebuffStep(
        CombatDebuffStep due,
        MonsterRuleActions actions,
        PluginCombatTarget target,
        PluginCombatSnapshot combat,
        IReadOnlyList<PluginInventoryItem> items)
    {
        CombatDebuffSource choice;
        while (true)
        {
            if (ChooseDebuffSource(
                    due.Identity,
                    in target,
                    items,
                    message => Log?.Invoke(MacroLogChannel.DebuffChoice, message))
                is not { } winner)
            {
                return DebuffPassResult.Idle;
            }
            if (winner.PathKind is not { } shape
                || ProjectilePathIsClear(
                    target.ObjectId,
                    shape,
                    HeightForShape(shape),
                    out PluginProjectilePathResult debuffPath))
            {
                choice = winner;
                break;
            }
            Status = ProjectileStatus(debuffPath, target.Name);
            // Without the fallback the winner stands and its column goes off
            // for the pass. With it, the shape is now a KNOWN-blocked one, so
            // the choice made again lands on the next-best source.
            if (!_settings.AllowDebuffFallback)
                return DebuffPassResult.ColumnDisabled;
        }

        if (SpellComponentPolicy.UsesBlacklistedComponent(
                _host.Automation.Spells,
                choice.Spell,
                _settings.BlacklistedSpellComponents))
        {
            return DebuffPassResult.Idle;
        }
        if (!ReadyForBreakableTurn(choice.Spell, target.ObjectId))
            return DebuffPassResult.Claimed;
        if (choice.Kind != CombatDebuffSourceKind.LearnedSpell)
        {
            DebuffStartResult itemResult = TryStartItemDebuff(
                choice,
                target,
                combat,
                items,
                ResolveInventoryOffhandObjectId(actions, items));
            return itemResult == DebuffStartResult.Handled
                ? DebuffPassResult.Claimed
                : DebuffPassResult.Idle;
        }

        // A learned debuff is cast from a wand, not from whatever the last
        // swing left in hand: the full wield gate runs first, so the wand's
        // own spellcraft and mana are what pay for the debuff.
        if (!PrepareForLearnedDebuff(actions, in target, items))
            return DebuffPassResult.Claimed;

        PluginCastGate gate = _host.Automation.Magic.EvaluateGate(
            choice.Spell.SpellId,
            target.ObjectId);
        if (gate == PluginCastGate.Busy)
        {
            Status = "Waiting to debuff";
            return DebuffPassResult.Claimed;
        }
        if (gate != PluginCastGate.Ready
            || !_host.Automation.Magic.Cast(
                choice.Spell.SpellId,
                target.ObjectId))
        {
            return DebuffPassResult.Idle;
        }

        _debuffs.Begin(
            target.ObjectId,
            choice.Identity,
            choice.Spell,
            _now,
            _host.Automation.Magic.LastCompletion.Revision);
        AnnounceCastAttempt(target.ObjectId, choice.Spell);
        string targetName = string.IsNullOrWhiteSpace(target.Name)
            ? $"0x{target.ObjectId:X8}"
            : target.Name;
        Log?.Invoke(
            MacroLogChannel.SpellCast,
            $"Casting: {choice.Spell.Name} on {target.ObjectId} ({targetName})");
        Status = $"{choice.Spell.Name} → {targetName}";
        return DebuffPassResult.Claimed;
    }

    /// <summary>
    /// The wield gate a learned-spell debuff runs through. With
    /// <c>SwitchWandsToDebuff</c> on and the attack weapon already a caster,
    /// that weapon is kept; every other case falls through to the first
    /// profiled wand.
    /// </summary>
    /// <returns>True once the character is holding what it needs.</returns>
    private bool PrepareForLearnedDebuff(
        MonsterRuleActions actions,
        in PluginCombatTarget target,
        IReadOnlyList<PluginInventoryItem> items)
    {
        IReadOnlyList<PluginEquipmentItem> equipment = PassEquipment();
        uint attackWeapon = 0u;
        if (_settings.SwitchWandsToDebuff)
        {
            (uint weapon, _, _) = ResolveWieldPlan(
                actions,
                in target,
                items,
                equipment);
            foreach (PluginEquipmentItem item in equipment)
            {
                if (weapon == 0u || item.ObjectId != weapon)
                    continue;
                if (CombatModeGate.ModeFor(in item) == PluginCombatMode.Magic)
                    attackWeapon = weapon;
                break;
            }
        }

        if (Gate.TryPrepare(
                PluginCombatMode.Magic,
                overrideItemId: attackWeapon,
                autoSelect: attackWeapon == 0u,
                captured: equipment))
        {
            return true;
        }
        Status = Gate.Status;
        return false;
    }

    /// <summary>
    /// The height the way to the monster is tested at. It belongs to the
    /// SHAPE of the flight, not to the swing: an arc is thrown high, a bolt
    /// goes out level, and anything else — a shot, a thrown weapon — is tested
    /// at the height the profile swings at.
    /// </summary>
    private PluginAttackHeight HeightForShape(PluginProjectilePathKind kind) =>
        kind switch
        {
            PluginProjectilePathKind.Arc => PluginAttackHeight.High,
            PluginProjectilePathKind.Straight => PluginAttackHeight.Medium,
            _ => _settings.AttackHeight,
        };

    private bool ProjectilePathIsClear(
        uint targetObjectId,
        PluginProjectilePathKind kind,
        PluginAttackHeight height,
        out PluginProjectilePathResult result)
    {
        if (!_settings.UseProjectileAwareness)
        {
            result = new(PluginProjectilePathStatus.Clear);
            return true;
        }
        if (_passClearance.TryGetValue((targetObjectId, kind), out PluginProjectilePathResult memo))
        {
            result = memo;
            return memo.IsClear;
        }
        result = _settings.ShowCollisionDebug
            ? _host.Automation.Projectiles.EvaluatePathWithDiagnostics(
                targetObjectId,
                kind,
                height,
                (float)_settings.CollisionProjectileRadius,
                (float)_settings.CollisionStepDistance,
                _settings.CollisionSampleBudget)
            : _host.Automation.Projectiles.EvaluatePath(
                targetObjectId,
                kind,
                height,
                (float)_settings.CollisionProjectileRadius,
                (float)_settings.CollisionStepDistance,
                _settings.CollisionSampleBudget);
        if (_settings.ShowCollisionDebug && result.DebugSamples.Count > 0)
        {
            _host.Automation.Projectiles.ShowDebugSamples(result.DebugSamples);
            _host.Log.Info(
                $"MossTank collision {kind}: {result.Status}, "
                + $"{result.DebugSamples.Count} marker(s), "
                + $"{result.CollisionChecks} check(s)");
        }
        if (result.Status == PluginProjectilePathStatus.Unavailable)
        {
            // A client that cannot test a flight has said nothing about the
            // flight. The shot goes ahead untested, as it does with the
            // option off; refusing it would leave the character standing
            // beside a monster it never attacks.
            PostAttackWarning(
                "Warning: this client cannot test projectile paths, so "
                + "\"Don't Shoot at Walls\" has no effect.");
            result = new(PluginProjectilePathStatus.Clear);
        }
        _passClearance[(targetObjectId, kind)] = result;
        return result.IsClear;
    }

    /// <summary>
    /// What the pass already knows about a flight, without testing it. An
    /// untested shape reads as clear.
    /// </summary>
    private bool KnownClear(uint targetObjectId, PluginProjectilePathKind? kind) =>
        kind is not { } shape
        || !_passClearance.TryGetValue((targetObjectId, shape), out PluginProjectilePathResult result)
        || result.IsClear;

    private static string ProjectileStatus(
        in PluginProjectilePathResult result,
        string targetName)
    {
        string target = string.IsNullOrWhiteSpace(targetName)
            ? "target"
            : targetName;
        return result.Status switch
        {
            PluginProjectilePathStatus.Blocked when result.BlockingObjectId != 0u =>
                $"Projectile path to {target} blocked by 0x{result.BlockingObjectId:X8}",
            PluginProjectilePathStatus.Blocked =>
                $"Projectile path to {target} is blocked",
            PluginProjectilePathStatus.Unavailable =>
                "Projectile collision data is unavailable",
            PluginProjectilePathStatus.BudgetExceeded =>
                "Projectile collision-check budget exhausted",
            PluginProjectilePathStatus.InvalidTarget =>
                $"Cannot resolve projectile path to {target}",
            PluginProjectilePathStatus.Error =>
                result.Notice ?? "Projectile collision check failed",
            _ => $"Cannot fire at {target}",
        };
    }

    /// <summary>The two attack schools, by skill id.</summary>
    private const uint WarMagicSkill = 34u;
    private const uint VoidMagicSkill = 43u;

    private static bool IsTrained(ICharacterInfo character, uint skillId) =>
        character.TryGetSkill(skillId, out PluginSkillInfo skill)
        && skill.Training is PluginSkillTraining.Trained
            or PluginSkillTraining.Specialized;

    private DebuffStartResult TryStartItemDebuff(
        CombatDebuffSource source,
        PluginCombatTarget target,
        PluginCombatSnapshot combat,
        IReadOnlyList<PluginInventoryItem> inventory,
        uint desiredOffhand)
    {
        IEquipmentAutomation equipment = _host.Automation.Equipment;
        if (!equipment.IsAvailable)
            return DebuffStartResult.Skipped;
        if (equipment.IsBusy)
        {
            Status = $"Equipping {ItemName(source.ItemObjectId, inventory)}";
            return DebuffStartResult.Handled;
        }

        PluginInventoryItem item = default;
        bool found = false;
        foreach (PluginInventoryItem candidate in inventory)
        {
            if (candidate.ObjectId == source.ItemObjectId)
            {
                item = candidate;
                found = true;
                break;
            }
        }
        if (!found)
            return DebuffStartResult.Skipped;

        if (source.Kind == CombatDebuffSourceKind.Grenade
            && desiredOffhand != 0u)
        {
            IReadOnlyList<PluginEquipmentItem> equipmentItems = PassEquipment();
            PluginEquipmentItem? offhand = null;
            foreach (PluginEquipmentItem candidate in equipmentItems)
            {
                if (candidate.ObjectId == desiredOffhand)
                {
                    offhand = candidate;
                    break;
                }
            }
            if (offhand is { IsEquipped: false } selectedOffhand)
            {
                PluginEquipmentCommandResult offhandResult =
                    equipment.Equip(selectedOffhand.ObjectId);
                if (offhandResult.Accepted
                    || offhandResult.Status == PluginEquipmentCommandStatus.Busy)
                {
                    Status = $"Equipping {selectedOffhand.Name}";
                    return DebuffStartResult.Handled;
                }
                return DebuffStartResult.Skipped;
            }
        }

        if (!item.IsEquipped)
        {
            PluginEquipmentCommandResult equip = equipment.Equip(item.ObjectId);
            if (equip.Status is PluginEquipmentCommandStatus.Started
                or PluginEquipmentCommandStatus.Busy)
            {
                Status = $"Equipping {item.Name}";
                return DebuffStartResult.Handled;
            }
            return DebuffStartResult.Skipped;
        }

        PluginCombatMode desiredMode = source.Kind switch
        {
            CombatDebuffSourceKind.CasterItem => PluginCombatMode.Magic,
            CombatDebuffSourceKind.Grenade => PluginCombatMode.Missile,
            _ when (item.ItemType & 0x00000100u) != 0u =>
                PluginCombatMode.Missile,
            _ => PluginCombatMode.Melee,
        };
        if (combat.Mode != desiredMode)
        {
            EnterDebuffMode(desiredMode);
            return DebuffStartResult.Handled;
        }

        string targetName = string.IsNullOrWhiteSpace(target.Name)
            ? $"0x{target.ObjectId:X8}"
            : target.Name;
        if (source.Kind == CombatDebuffSourceKind.CasterItem)
        {
            IItemAutomation itemCommands = _host.Automation.Items;
            if (!itemCommands.IsAvailable || itemCommands.IsBusy)
            {
                Status = $"Waiting to use {item.Name}";
                return DebuffStartResult.Handled;
            }
            PluginItemCommandResult apply = itemCommands.Apply(
                item.ObjectId,
                target.ObjectId);
            if (!apply.Accepted)
                return DebuffStartResult.Skipped;
            // The wand now owns the character until its cast is over: nothing
            // else may use an item, and the attack may not swing, inside that
            // window.
            _actionLocks.Arm(
                ActionLockKind.ItemUse,
                ItemUseLock.HeldItemCastSeconds);
            _pendingItemDebuff = new PendingItemDebuff(
                source,
                target.ObjectId,
                targetName,
                item.Name,
                _now,
                itemCommands.LastCompletion.Revision,
                combat.CompletionRevision,
                desiredMode,
                0f);
            AnnounceCastAttempt(target.ObjectId, source.Spell);
            Status = $"{source.Spell.Name} via {item.Name} → {targetName}";
            return DebuffStartResult.Handled;
        }

        if (combat.RequestInProgress
            || combat.ServerResponsePending
            || combat.RepeatAttackInProgress)
        {
            Status = $"Waiting to fire {item.Name}";
            return DebuffStartResult.Handled;
        }
        float power = desiredMode == PluginCombatMode.Missile ? 1f : 0f;
        PluginCombatCommandResult begin =
            _host.Automation.Combat.BeginPhysicalAttack(
                target.ObjectId,
                PluginAttackHeight.Medium,
                power);
        if (begin.Status != PluginCombatCommandStatus.Started)
            return begin.Status == PluginCombatCommandStatus.Busy
                ? DebuffStartResult.Handled
                : DebuffStartResult.Skipped;
        _pendingItemDebuff = new PendingItemDebuff(
            source,
            target.ObjectId,
            targetName,
            item.Name,
            _now,
            _host.Automation.Items.LastCompletion.Revision,
            combat.CompletionRevision,
            desiredMode,
            power);
        AnnounceCastAttempt(target.ObjectId, source.Spell);
        Status = $"Charging {item.Name} for {targetName}";
        return DebuffStartResult.Handled;
    }

    /// <summary>
    /// The weapon a monster rule names, honoured only while that item is one
    /// of the profile's items.
    ///
    /// The Items page is the whole roster the fight may reach for, and the
    /// weapon column is a pick from it. A rule can still be carrying a pick
    /// made before the item left the page - or written by an editor that
    /// offered more than the page - and fighting with gear the profile was
    /// never set up for is worse than fighting with none: nothing was buffed
    /// for it, and nobody asked for it. So an unlisted pick is dropped and
    /// automatic selection, which only ever walks listed items, answers
    /// instead.
    /// </summary>
    private uint ResolveRuleWeaponObjectId(
        MonsterRuleActions actions,
        IReadOnlyList<PluginEquipmentItem> items,
        out bool unlisted)
    {
        unlisted = false;
        uint resolved = ResolveEquipmentObjectId(
            actions.WeaponObjectId,
            actions.WeaponName,
            items);
        if (resolved == 0u)
            return 0u;
        foreach (PluginEquipmentItem item in items)
        {
            if (item.ObjectId != resolved)
                continue;
            if (CombatProfileItems.IsProfiled(_settings, item.ObjectId, item.Name))
                return resolved;
            unlisted = true;
            return 0u;
        }
        return 0u;
    }

    private uint ResolveRuleWeaponObjectId(
        MonsterRuleActions actions,
        IReadOnlyList<PluginEquipmentItem> items) =>
        ResolveRuleWeaponObjectId(actions, items, out _);

    private static uint ResolveEquipmentObjectId(
        uint sessionObjectId,
        string durableName,
        IReadOnlyList<PluginEquipmentItem> items)
    {
        if (sessionObjectId != 0u
            && items.Any(item => item.ObjectId == sessionObjectId))
        {
            return sessionObjectId;
        }
        if (string.IsNullOrWhiteSpace(durableName))
            return 0u;
        foreach (PluginEquipmentItem item in items)
        {
            if (item.Name.Equals(durableName, StringComparison.Ordinal))
                return item.ObjectId;
        }
        return 0u;
    }

    /// <summary>Where an item has to fit before it can serve as an offhand.</summary>
    private const uint OffHandLocations =
        ItemEnchantDefaults.Shield | ItemEnchantDefaults.MeleeWeapon;

    /// <summary>
    /// The pack item a rule's named offhand means. An id names one object and
    /// is taken at its word, but a name belongs just as easily to something
    /// that cannot be held at all, and asking for that one costs everything:
    /// the request goes out on every pass, nothing ever reaches the hand, and
    /// the character never becomes ready to attack. So only an item that could
    /// occupy the off hand answers to the name.
    /// </summary>
    private static uint ResolveInventoryOffhandObjectId(
        MonsterRuleActions actions,
        IReadOnlyList<PluginInventoryItem> items)
    {
        if (actions.OffhandObjectId != 0u
            && items.Any(item => item.ObjectId == actions.OffhandObjectId))
        {
            return actions.OffhandObjectId;
        }
        if (string.IsNullOrWhiteSpace(actions.OffhandName))
            return 0u;
        foreach (PluginInventoryItem item in items)
        {
            if (item.Name.Equals(actions.OffhandName, StringComparison.Ordinal)
                && CanFillOffHand(in item))
            {
                return item.ObjectId;
            }
        }
        return 0u;
    }

    /// <summary>
    /// Whether the item could be held in the off hand at all: a shield, or a
    /// melee weapon that does not already claim both hands.
    /// </summary>
    private static bool CanFillOffHand(in PluginInventoryItem item) =>
        (item.ValidLocations & OffHandLocations) != 0u
        && (item.ValidLocations & ItemEnchantDefaults.TwoHanded) == 0u;

    private void EnterDebuffMode(PluginCombatMode mode)
    {
        PluginCombatCommandResult result =
            _host.Automation.Combat.EnterMode(mode);
        Status = result.Status == PluginCombatCommandStatus.Refused
            ? result.Notice ?? $"Cannot enter {mode} mode"
            : $"Entering {mode} mode";
    }

    private void ClearPendingItemDebuff()
    {
        // The item is finished with, so the slot goes down early rather than
        // costing the rest of its window.
        if (_pendingItemDebuff?.Source.Kind == CombatDebuffSourceKind.CasterItem)
            _actionLocks.Release(ActionLockKind.ItemUse);
        _pendingItemDebuff = null;
    }

    private void TickPendingItemDebuff(PluginCombatSnapshot combat)
    {
        if (_pendingItemDebuff is not { } pending)
            return;
        if (_now - pending.DispatchedAt >= 15d)
        {
            _host.Automation.Combat.AbortPhysicalAttack();
            Status = $"{pending.Source.Spell.Name} timed out";
            ClearPendingItemDebuff();
            return;
        }
        if (pending.Source.Kind == CombatDebuffSourceKind.CasterItem)
        {
            TickWandCastRecovery(pending);
            Status = $"Waiting for {pending.Source.Spell.Name}";
            return;
        }

        if (combat.RequestInProgress)
        {
            if (combat.BuildInProgress
                && combat.PowerBarLevel + PowerReleaseEpsilon
                    >= pending.Power)
            {
                PluginCombatCommandResult release =
                    _host.Automation.Combat.ReleasePhysicalAttack();
                Status = release.Status == PluginCombatCommandStatus.Released
                    ? $"Firing {pending.ItemName}"
                    : $"Attack release: {release.Status}";
            }
            else
            {
                Status = $"Charging {pending.ItemName}";
            }
            return;
        }
        if (combat.ServerResponsePending || combat.RepeatAttackInProgress)
        {
            Status = $"Waiting for {pending.Source.Spell.Name}";
            return;
        }
        if (combat.CompletionRevision > pending.PhysicalCompletionRevision)
        {
            pending.PhysicalCompletionRevision = combat.CompletionRevision;
            pending.AttackCompletedAt ??= _now;
            if (combat.CompletionWeenieError != 0u)
            {
                Status = $"{pending.ItemName} failed (0x{combat.CompletionWeenieError:X})";
                ClearPendingItemDebuff();
                return;
            }
        }
        if (pending.AttackCompletedAt is { } completed
            && _now - completed >= 1d)
        {
            ClearPendingItemDebuff();
            Status = $"Retrying {pending.Source.Spell.Name}";
            return;
        }
        Status = $"Waiting for {pending.Source.Spell.Name}";
    }

    private void TickWandCastRecovery(PendingItemDebuff pending)
    {
        double age = _now - pending.DispatchedAt;
        INavigationAutomation movement = _host.Automation.Navigation;
        if (_settings.JumpOutWandCasting
            && !pending.RecoverySent
            && age >= 0.2d)
        {
            _ = movement.SetMovementIntent(new PluginMovementIntent(Jump: true));
            _ = movement.ClearMovementIntent();
            pending.RecoverySent = true;
            return;
        }
        if (!_settings.DoJiggle || _settings.JumpOutWandCasting)
            return;
    }

    /// <summary>
    /// A physical attack is running at <paramref name="targetObjectId"/>, so
    /// its result text is ours to read.
    /// </summary>
    private void ArmPhysicalResultText(uint targetObjectId, string targetName)
    {
        _physicalResultArmed = true;
        _physicalResultTargetId = targetObjectId;
        _physicalResultTargetName = targetName ?? string.Empty;
        _physicalResultIncarnation = FindTarget(targetObjectId).Incarnation;
    }

    private void DisarmPhysicalResultText()
    {
        if (!_physicalResultArmed)
            return;
        _physicalResultArmed = false;
        _physicalCompletedAt = _now;
    }

    /// <summary>
    /// The melee/missile half of result reading. A swing produces no cast
    /// receipt, so the outcome of a physical attack is only ever visible in
    /// chat: this is what tells the macro the monster is dead, that a shot
    /// flew into the scenery, or that a swing landed.
    /// </summary>
    private void ObservePhysicalResultText(in PluginChatMessage message)
    {
        // Read only while a swing is armed at our own target, or for a brief
        // moment after the sequence ended — the last swing's outcome line can
        // still arrive after the server has closed the attack. What the line
        // belongs to is the target the swing was ARMED at: a killing blow
        // takes the selection away, so asking what is selected now would
        // throw away the sentence that says the monster died. The incarnation
        // check below is what keeps the line off a different creature.
        if (!_physicalResultArmed
            && _now - _physicalCompletedAt > PhysicalResultTextTailSeconds)
        {
            return;
        }
        if (_physicalResultTargetId == 0u)
            return;
        PluginCombatTarget currentPhysicalTarget = FindTarget(_physicalResultTargetId);
        if (currentPhysicalTarget.ObjectId != 0u
            && currentPhysicalTarget.Incarnation != _physicalResultIncarnation)
            return;

        // Which log the line came from decides which of these arms may read
        // it at all: the miss notice and the kill sentence are plain lines,
        // and the damage report is the character's own combat log. A player
        // typing any of those sentences in chat carries a different type and
        // is ignored.
        string text = message.Text ?? string.Empty;
        if (message.LogTextType == CombatLogTextType.Default
            && string.Equals(
                text.Trim(),
                CombatResultText.MissileHitEnvironment,
                StringComparison.Ordinal))
        {
            AnnounceBlacklist(
                _failures.RecordMiss(
                    _physicalResultTargetId, _now, _settings),
                _physicalResultTargetId,
                _physicalResultTargetName);
        }
        else if (message.LogTextType == CombatLogTextType.OwnCombat
            && CombatResultText.IsDamageReport(text))
        {
            _failures.ResetAttempts(_physicalResultTargetId);
        }

        if (message.LogTextType != CombatLogTextType.Default
            || !CombatResultText.IsKillingBlow(text, out string slain))
        {
            return;
        }

        // The looting hold goes up on the killing blow itself, before the
        // sentence is matched against our own target's name.
        ArmPostKillNavigationLock();

        uint slainObjectId = _physicalResultTargetId;
        string slainName = _physicalResultTargetName;
        if (slain.Length > 0 && WieldingCleavingWeapon())
        {
            // A cleaving weapon can fell a creature standing beside the one
            // the swing was aimed at, so here the sentence, not the aim, says
            // who died — and something did die, which is worth knowing even
            // when it was not the target. The sentence has to say so
            // unambiguously: with two live monsters of one name it cannot
            // tell them apart, and the wrong one would be given up for dead.
            if (FindLiveTargetNamed(slain) is not uint cleaved)
                return;
            slainObjectId = cleaved;
            slainName = slain;
        }
        else if (slain.Length > 0
            && !slain.Equals(
                _physicalResultTargetName,
                StringComparison.Ordinal))
        {
            // The sentence has to name OUR monster, letter for letter. An
            // unnamed stored target still refuses a sentence that names
            // someone else.
            return;
        }

        Log?.Invoke(
            MacroLogChannel.CastInfo,
            $"AttackExecutor: Kill blow ({text})");
        _failures.ResetAttempts(slainObjectId);
        _host.Log.Info($"Target death attribution: physical target=0x{slainObjectId:X8}, name={slainName}, message={text}");
        // A bystander the cleave took does not end the swing that is still
        // running at our own monster.
        if (slainObjectId == _physicalResultTargetId)
            DisarmPhysicalResultText();
        EndKilledTarget(slainObjectId);
    }

    /// <summary>
    /// The one creature still standing that carries <paramref name="name"/>,
    /// or null when none does or when more than one does and the name cannot
    /// tell them apart.
    /// </summary>
    private uint? FindLiveTargetNamed(string name)
    {
        uint found = 0u;
        foreach (PluginCombatTarget target in _targets)
        {
            if (target.ObjectId == 0u
                || !string.Equals(target.Name, name, StringComparison.Ordinal)
                || IsKnownDead(in target))
            {
                continue;
            }
            if (found != 0u)
                return null;
            found = target.ObjectId;
        }
        return found == 0u ? null : found;
    }

    /// <summary>
    /// True when the wielded weapon (or a melee off-hand) cleaves, i.e. one
    /// swing can strike more than the creature it was aimed at.
    /// </summary>
    private bool WieldingCleavingWeapon()
    {
        IEquipmentAutomation equipment = _host.Automation.Equipment;
        if (!equipment.IsAvailable)
            return false;
        // The question is about the WIELDED weapon, not about everything worn:
        // a cleaving belt buckle does not make a kill sentence ambiguous. (The
        // shield slot has an arm of its own in the reference, but it re-reads
        // this same weapon's count, so it cannot change the answer.)
        IReadOnlyList<PluginEquipmentItem> items = PassEquipment();
        (uint weapon, _) = WieldedPair(items);
        if (weapon == 0u)
            return false;
        foreach (PluginEquipmentItem item in items)
        {
            if (item.ObjectId == weapon)
                return item.Cleaving > 1;
        }
        return false;
    }

    private void ObserveItemDebuffReceipts()
    {
        foreach (PluginChatMessage message in
            _host.Automation.Chat.CaptureMessages(_observedChatSequence))
        {
            _observedChatSequence = Math.Max(
                _observedChatSequence,
                message.Sequence);
            ObservePhysicalResultText(in message);
            _castTracker.ObserveChat(
                message.Sequence,
                message.Text,
                // LOCAL speech only; the same test as
                // MossTankPanel.ObserveCastTrackerChat.
                ownSpeech: message.Kind == SpellCastTracker.LocalSpeechChatKind
                    && message.SenderObjectId != 0u
                    && message.SenderObjectId
                        == _host.Automation.Character.ObjectId,
                logTextType: (uint)message.LogTextType);
        }
    }

    /// <summary>
    /// The in-flight item transaction's own watcher. It reads the log and the
    /// item receipt on a reading position of its own, because the transaction
    /// outlives the passes the attack wins: it has to be able to see its own
    /// confirmation on a pass where the attack has no turn at all.
    /// </summary>
    private void ObserveItemTransaction()
    {
        foreach (PluginChatMessage message in
            _host.Automation.Chat.CaptureMessages(_itemTransactionChatSequence))
        {
            _itemTransactionChatSequence = Math.Max(
                _itemTransactionChatSequence,
                message.Sequence);
            // The wand's own confirmation is a magic-log line like any other
            // spell result.
            if (_pendingItemDebuff is not { } pending
                || message.LogTextType != CombatLogTextType.Magic
                || !IsMatchingCastLine(message.Text, pending.Source.Spell.Name))
            {
                continue;
            }
            _debuffs.RecordApplied(
                pending.TargetObjectId,
                pending.Source.Identity,
                pending.Source.Spell,
                _now);
            AnnounceCastSuccess(pending.TargetObjectId, pending.Source.Spell);
            _failures.ResetAttempts(pending.TargetObjectId);
            Status = $"{pending.Source.Spell.Name} applied to {pending.TargetName}";
            if (!_settings.JumpOutWandCasting)
                StartSelectionJiggle(pending.Source.Spell);
            ClearPendingItemDebuff();
        }

        PluginItemUseCompletion itemCompletion =
            _host.Automation.Items.LastCompletion;
        if (itemCompletion.Revision <= _observedItemCompletion)
            return;
        _observedItemCompletion = itemCompletion.Revision;
        if (_pendingItemDebuff is not { } itemPending
            || itemPending.Source.Kind != CombatDebuffSourceKind.CasterItem
            || itemCompletion.SourceObjectId != itemPending.Source.ItemObjectId
            || itemCompletion.TargetObjectId != itemPending.TargetObjectId
            || itemCompletion.IsSuccess)
        {
            return;
        }
        Status = $"{itemPending.ItemName} failed (0x{itemCompletion.WeenieError:X})";
        ClearPendingItemDebuff();
    }

    private void OnCastTrackerOutcome(SpellCastOutcomeInfo info)
    {
        uint objectId = info.TargetObjectId;
        PluginCombatTarget currentTarget = FindTarget(objectId);
        if (objectId != 0u && currentTarget.ObjectId != 0u
            && currentTarget.Incarnation != info.TargetIncarnation)
            return;
        switch (info.Outcome)
        {
            case SpellCastOutcome.Kill:
                Log?.Invoke(
                    MacroLogChannel.CastInfo,
                    $"SpellCaster: Spell kill reset ({info.Text})");
                ArmPostKillNavigationLock();
                if (objectId == 0u)
                    return;
                // The health tracker lets the monster go the moment a killing
                // blow is credited to it.
                if (!info.HitsMultipleTargets
                    && objectId == _health.TargetObjectId)
                {
                    _health.Clear(_now);
                }
                _failures.ResetAttempts(objectId);
                // A spell that strikes several creatures cannot say WHICH one
                // the sentence is about, so the blow is recorded but the
                // target is not ended.
                if (!info.HitsMultipleTargets)
                {
                    _host.Log.Info($"Target death attribution: spell target=0x{objectId:X8}, message={info.Text}");
                    EndKilledTarget(objectId);
                }
                return;

            case SpellCastOutcome.PermanentFail:
                // The multiple-targets gate is applied by the tracker, which
                // owns that flag; reaching here means it passed.
                Log?.Invoke(
                    MacroLogChannel.CastInfo,
                    $"SpellCaster: Spell permanent fail reset ({info.Text})");
                if (objectId != 0u)
                    _failures.ForceBlacklist(objectId, _now, _settings);
                return;

            case SpellCastOutcome.Fail:
                Log?.Invoke(
                    MacroLogChannel.CastInfo,
                    $"SpellCaster: Spell fail reset ({info.Text})");
                return;

            case SpellCastOutcome.Success:
                Log?.Invoke(
                    MacroLogChannel.CastInfo,
                    $"SpellCaster: Spell success reset ({info.Text})");
                if (objectId == 0u)
                    return;
                // How big the blow was, taken off the running estimate. A
                // spell that strikes several creatures cannot say which one
                // the figure belongs to.
                if (!info.HitsMultipleTargets
                    && CombatResultText.TryReadSpellDamage(
                        info.Text,
                        out int points))
                {
                    _health.RecordDamage(objectId, points);
                }
                _failures.ResetAttempts(objectId);
                return;

            case SpellCastOutcome.ResultTimeout:
                Log?.Invoke(
                    MacroLogChannel.CastInfo,
                    "SpellCaster: gave up waiting for the cast result");
                if (objectId != 0u && !info.HitsMultipleTargets)
                {
                    AnnounceBlacklist(
                        _failures.RecordMiss(objectId, _now, _settings),
                        objectId,
                        info.TargetName);
                }
                return;

            case SpellCastOutcome.LaunchTimeout:
                Log?.Invoke(
                    MacroLogChannel.CastInfo,
                    "SpellCaster: gave up waiting for the cast to start");
                return;

            case SpellCastOutcome.Rejected:
                Log?.Invoke(
                    MacroLogChannel.CastInfo,
                    $"SpellCaster: Cast refused (0x{info.WeenieError:X4})");
                return;
        }
    }

    private void EndKilledTarget(uint objectId)
    {
        PluginCombatTarget observed = FindTarget(objectId);
        _host.Log.Info($"Target marked dead: 0x{objectId:X8}, name={observed.Name}, healthKnown={observed.IsHealthKnown}, health={observed.HealthFraction}, healthRevision={observed.HealthRevision}, incarnation={observed.Incarnation}");
        _failures.ReportDeath(objectId, _now, observed.HealthRevision);
        if (_pendingAttackTarget == objectId)
        {
            _pendingAttackSpell = 0u;
            _pendingAttackTarget = 0u;
        }
        if (_targetId != objectId)
            return;
        // Dropping the target is what cancels its swing; one owner does it.
        ClearTarget();
        _untilScan = 0d;
        Status = "Waiting for a target";
    }

    /// <summary>
    /// Whether a cast hits more than one target. VTank hangs this off the
    /// spell it is casting; here it lives on the tracker that owns the
    /// in-flight spell.
    /// </summary>
    private static bool HitsMultipleTargets(in PluginSpellInfo spell) =>
        SpellCastTracker.HitsMultipleTargetsFor(spell);

    private static bool IsMatchingCastLine(string text, string spellName) =>
        text.StartsWith($"You cast {spellName} on ", StringComparison.Ordinal);

    private void ObserveSelectionJiggle(in PluginCastCompletion completion)
    {
        if (_host.Automation.Magic.IsCasting)
        {
            StopSelectionJiggle();
            return;
        }
        if (completion.Revision <= _observedJiggleCastCompletion)
            return;
        _observedJiggleCastCompletion = completion.Revision;
        if (completion.IsSuccess
            && _host.Automation.Spells.TryGet(
                completion.SpellId,
                out PluginSpellInfo spell))
        {
            StartSelectionJiggle(spell);
        }
    }

    private void StartSelectionJiggle(in PluginSpellInfo spell)
    {
        if (!_settings.DoJiggle
            || (IsVtankInstantCast(spell)
                && spell.School is 34u or 43u))
        {
            return;
        }
        ISelectionAutomation selection = _host.Automation.Selection;
        if (!selection.Execute(PluginSelectionAction.PreviousSelection))
            return;
        _selectionJiggleActive = true;
        _selectionJigglePreviousPlayer = false;
        _nextSelectionJiggleAt = _now;
        _selectionJiggleUntil = _now + SpellCastTracker.ResultTickSeconds;
    }

    private void TickSelectionJiggle()
    {
        if (!_selectionJiggleActive)
            return;
        if (_now >= _selectionJiggleUntil)
        {
            StopSelectionJiggle();
            return;
        }
        if (_now < _nextSelectionJiggleAt)
            return;
        ISelectionAutomation selection = _host.Automation.Selection;
        int pulses = 0;
        do
        {
            PluginSelectionAction action = _selectionJigglePreviousPlayer
                ? PluginSelectionAction.PreviousPlayer
                : PluginSelectionAction.NextPlayer;
            if (!selection.Execute(action))
            {
                StopSelectionJiggle();
                return;
            }
            _selectionJigglePreviousPlayer = !_selectionJigglePreviousPlayer;
            _nextSelectionJiggleAt += 0.131d;
        }
        while (_now >= _nextSelectionJiggleAt && ++pulses < 8);
    }

    private void StopSelectionJiggle()
    {
        _selectionJiggleActive = false;
        _selectionJigglePreviousPlayer = false;
        _nextSelectionJiggleAt = 0d;
        _selectionJiggleUntil = 0d;
    }

    private static bool IsVtankInstantCast(in PluginSpellInfo spell)
    {
        if (spell.Difficulty < 50)
            return true;
        if (spell.IsUntargeted
            && !spell.IsFellowship
            && spell.DurationSeconds >= 60f
            && spell.School is 31u or 33u)
        {
            return true;
        }
        return spell.Family is >= 243u and <= 249u or 639u;
    }

    private static string ItemName(
        uint objectId,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        foreach (PluginInventoryItem item in inventory)
        {
            if (item.ObjectId == objectId)
                return item.Name;
        }
        return $"0x{objectId:X8}";
    }

    private void RefreshSpellCatalogs()
    {
        ISpellCatalog catalog = _host.Automation.Spells;
        IReadOnlyList<PluginSpellInfo> spells = catalog.KnownCombatSpells;
        IReadOnlyList<PluginSpellInfo> attacks = catalog.KnownAttackSpells;
        if (ReferenceEquals(spells, _combatSpellSnapshot)
            && ReferenceEquals(attacks, _attackSpellSnapshot))
        {
            return;
        }
        _combatSpellSnapshot = spells;
        _attackSpellSnapshot = attacks;

        if (attacks.Count == 0)
        {
            _attackCatalog = AttackSpellCatalog.Build(spells);
            return;
        }
        var union = new List<PluginSpellInfo>(spells.Count + attacks.Count);
        var seen = new HashSet<uint>();
        foreach (PluginSpellInfo spell in spells)
        {
            if (seen.Add(spell.SpellId))
                union.Add(spell);
        }
        foreach (PluginSpellInfo spell in attacks)
        {
            if (seen.Add(spell.SpellId))
                union.Add(spell);
        }
        _attackCatalog = AttackSpellCatalog.Build(union);
    }

    /// <summary>
    /// Nothing left to kill. The capture carries a creature on for a while
    /// after it dies, so "it is still in the list" does not mean it can be
    /// fought. The host saying the creature died settles it; a health reading
    /// of zero is the older, weaker answer and is still accepted.
    /// </summary>
    private static bool IsKnownDead(in PluginCombatTarget target) =>
        target.ObjectId != 0u
        && (target.IsDead
            || (target.IsHealthKnown && target.HealthFraction <= 0f));

    private PluginCombatTarget FindTarget(uint objectId)
    {
        foreach (PluginCombatTarget target in _targets)
        {
            if (target.ObjectId == objectId)
                return target;
        }
        return default;
    }

    private double _nextTargetDiagnostic;

    private void TraceTargetSelection(IReadOnlyList<CombatTargetCandidate> candidates,
        uint chosen)
    {
        if (chosen != 0u || _now < _nextTargetDiagnostic)
            return;
        _nextTargetDiagnostic = _now + 5d;
        IReadOnlyList<PluginCombatTarget> visible =
            _host.Automation.Combat.CaptureHostileTargets(float.MaxValue);
        Log?.Invoke(MacroLogChannel.RuleInfo, $"Target scan: no selection, hostiles={visible.Count}, candidates={candidates.Count}, range={_settings.MinimumRange:F2}..{_acquisitionRange:F2}, selected=0x{(_host.Selection.SelectedObjectId ?? 0u):X8}");
        foreach (PluginCombatTarget target in visible)
        {
            ResolvedMonsterRule rule = _settings.ResolveRule(target);
            CombatSuppressionReason suppression = _failures.Reason(target.ObjectId, _now);
            string reason = target.Distance > _acquisitionRange ? "outside maximum range"
                : target.Distance < _settings.MinimumRange ? "inside minimum range"
                : suppression != CombatSuppressionReason.None ? suppression.ToString()
                : _passTargetReasons.TryGetValue(target.ObjectId, out string? noted) ? noted
                : _passInvalidTargets.Contains(target.ObjectId) ? "invalidated during attack pass"
                : rule.Priority < 0 ? "negative rule priority"
                : _passCandidates.TryGetValue(target.ObjectId, out CombatTargetCandidate? candidate)
                    ? candidate is null ? "no attack or due debuff after pass filtering" : "eligible candidate"
                : "absent from current acquisition snapshot";
            Log?.Invoke(MacroLogChannel.RuleInfo, $"Target check: {target.Name} (0x{target.ObjectId:X8}), distance={target.Distance:F2}, angle={target.RelativeAngleDegrees:F1}, reason={reason}, rule={rule.Rule.Expression}, priority={rule.Priority}, attacks={rule.Actions.Attacks}, streak={rule.Actions.UsesStreak}");
        }
    }

    private void RefreshTarget()
    {
        if (_targetId != 0u
            && _failures.Reason(_targetId, _now)
                != CombatSuppressionReason.None)
        {
            _host.Automation.Combat.AbortPhysicalAttack();
            ClearTarget();
        }

        PluginCombatSnapshot combat = _host.Automation.Combat.Snapshot;

        // Selection is NOT frozen for the life of an engagement. The only
        // things that stop the macro re-picking are the pass hold (a cast or a
        // turn in flight) and the item-use cooldown, both of which sit above
        // this rule; a swing loop runs beside the pass and never blocks it. A
        // higher-priority monster arriving mid-fight has to be able to win.
        RefreshSpellCatalogs();
        IReadOnlyList<PluginInventoryItem> inventory =
            PassInventory();
        IReadOnlyList<PluginEquipmentItem> equipment = PassEquipment();
        (uint wieldedWeapon, uint wieldedOffhand) = WieldedPair(equipment);

        uint lastTarget = _lastTargetId;
        var candidates = new List<CombatTargetCandidate>();
        _ringCandidateCount = 0;
        foreach (PluginCombatTarget target in _targets)
        {
            if (!TryBuildCandidate(
                    target,
                    combat,
                    lastTarget,
                    inventory,
                    equipment,
                    _acquisitionRange,
                    out CombatTargetCandidate candidate))
            {
                continue;
            }
            candidates.Add(candidate);
            if (candidate.Distance < _settings.RingDistance
                && candidate.Rule.Actions.UsesRing)
            {
                _ringCandidateCount++;
            }
        }

        CombatTargetCandidate chosen = CombatTargetSelector.Select(
            candidates,
            _settings.DebuffEachFirst,
            _settings.SelectionMethod,
            _settings.TargetSelectAngleRange,
            wieldedWeapon,
            wieldedOffhand);

        // No target chosen: drop whatever the pass was holding.
        TraceTargetSelection(candidates, chosen.ObjectId);
        if (chosen.ObjectId == 0u)
        {
            if (_targetId != 0u)
                _host.Automation.Combat.AbortPhysicalAttack();
            ClearTarget();
            return;
        }
        // The STORED rule is the authored one: what this pass learned about
        // the monster is applied where the decision reads it, so it cannot
        // outlive the pass and hold a column off between scans.
        SetTarget(chosen.Target, _settings.ResolveRule(chosen.Target));
    }

    private static (uint Weapon, uint Offhand) WieldedPair(
        IReadOnlyList<PluginEquipmentItem> equipment)
    {
        const uint shieldSlot = 0x00200000u;
        uint weapon = 0u;
        uint offhand = 0u;
        foreach (PluginEquipmentItem item in equipment)
        {
            if (weapon == 0u
                && CombatModeGate.IsWeaponSlot(item.EquippedLocation))
                weapon = item.ObjectId;
            else if (offhand == 0u && item.EquippedLocation == shieldSlot)
                offhand = item.ObjectId;
        }
        return (weapon, offhand);
    }

    /// <summary>
    /// The six ordered rejection gates, then the fill.
    /// </summary>
    private bool TryBuildCandidate(
        in PluginCombatTarget target,
        in PluginCombatSnapshot combat,
        uint lastTarget,
        IReadOnlyList<PluginInventoryItem> inventory,
        IReadOnlyList<PluginEquipmentItem> equipment,
        double maximumRange,
        out CombatTargetCandidate candidate)
    {
        candidate = default;

        if (_passCandidateRange != maximumRange)
        {
            _passCandidateRange = maximumRange;
            _passCandidates.Clear();
        }
        if (_passCandidates.TryGetValue(
                target.ObjectId,
                out CombatTargetCandidate? memo))
        {
            if (memo is not { } built)
                return false;
            candidate = built;
            return true;
        }
        if (_passInvalidTargets.Contains(target.ObjectId))
            return false;

        // A corpse is not a candidate. Nothing below this gate can tell the
        // difference on its own, so without it the character stands in front
        // of the thing it has just killed while everything still alive is
        // beside it.
        if (IsKnownDead(in target))
        {
            _passCandidates[target.ObjectId] = null;
            return false;
        }

        if (_failures.Reason(target.ObjectId, _now)
            != CombatSuppressionReason.None)
        {
            _passCandidates[target.ObjectId] = null;
            return false;
        }

        // Gate 3: the monster's rule must want it attacked at all.
        ResolvedMonsterRule rule = WithPassClearedActions(
            target.ObjectId,
            _settings.ResolveRule(target));
        if (rule.Priority < 0)
        {
            _passCandidates[target.ObjectId] = null;
            return false;
        }

        if (target.Distance > maximumRange
            || target.Distance < _settings.MinimumRange)
        {
            // Range is the one gate that depends on which range was asked
            // about, so it is not memoised.
            return false;
        }

        MonsterRuleActions actions = rule.Actions;
        (uint weapon, uint offhand, MonsterDamageType element) = ResolveWieldPlan(
            actions,
            target,
            inventory,
            equipment);
        IReadOnlyList<CombatDebuffStep> steps = CombatDebuffChain.Build(
            actions,
            element,
            ResolveExtraVulnerability(actions, target));
        PluginCombatTarget candidateTarget = target;
        bool needsDebuff = CombatDebuffChain.NeedsDebuff(
            steps,
            step => IsDebuffStepDue(step, in candidateTarget, inventory));

        if (!needsDebuff && !actions.Attacks && !actions.UsesStreak)
        {
            _passCandidates[target.ObjectId] = null;
            return false;
        }

        candidate = new CombatTargetCandidate(
            target,
            rule,
            rule.Priority,
            target.Distance,
            Math.Abs(target.RelativeAngleDegrees),
            DebuffUrgency(target, element, weapon, equipment),
            needsDebuff,
            _settings.TargetLock
                && combat.SelectedObjectId != 0u
                && combat.SelectedObjectId == target.ObjectId,
            lastTarget != 0u && target.ObjectId == lastTarget,
            weapon,
            offhand);
        _passCandidates[target.ObjectId] = candidate;
        return true;
    }

    private (uint Weapon, uint Offhand, MonsterDamageType Element) ResolveWieldPlan(
        MonsterRuleActions actions,
        in PluginCombatTarget target,
        IReadOnlyList<PluginInventoryItem> inventory,
        IReadOnlyList<PluginEquipmentItem> equipment)
    {
        MonsterDamageType element = ResolveAttackElement(actions, target);
        uint weapon = actions.WeaponToUseRaw == 0
            ? 0u
            : ResolveRuleWeaponObjectId(actions, equipment);
        if (weapon == 0u && actions.WeaponToUseRaw != 0)
            weapon = SelectAutomaticWeapon(equipment, actions, target);
        uint offhand = PlannedSecondaryFor(
            actions, equipment, weapon, inventory) ?? 0u;
        return (weapon, offhand, element);
    }

    /// <summary>
    /// The offhand this rule will actually ask for. A rule that names its
    /// offhand by id or by name means that one item even when the equipment
    /// view has not caught up with it, so a named offhand missing from that
    /// view falls back to what the pack knows: the request has to go out
    /// either way. Null means "no particular item" and leaves the choice of
    /// shield to the wield gate.
    /// </summary>
    private uint? PlannedSecondaryFor(
        MonsterRuleActions actions,
        IReadOnlyList<PluginEquipmentItem> items,
        uint primary,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        uint? selected = ResolveSecondaryEquipment(actions, items, primary);
        return NeedsInventoryOffhand(actions, selected)
            ? ResolveInventoryOffhandObjectId(actions, inventory)
            : selected;
    }

    /// <summary>
    /// The same plan for a caller that has no pack listing in hand yet, and
    /// would rather not pay for one on a pass that does not need it.
    /// </summary>
    private uint? PlannedSecondaryFor(
        MonsterRuleActions actions,
        IReadOnlyList<PluginEquipmentItem> items,
        uint primary,
        Func<IReadOnlyList<PluginInventoryItem>> inventory)
    {
        uint? selected = ResolveSecondaryEquipment(actions, items, primary);
        return NeedsInventoryOffhand(actions, selected)
            ? ResolveInventoryOffhandObjectId(actions, inventory())
            : selected;
    }

    private static bool NeedsInventoryOffhand(
        MonsterRuleActions actions, uint? selected) =>
        selected == 0u
        && (actions.OffhandObjectId != 0u || actions.OffhandName.Length != 0);

    private uint? ResolveSecondaryEquipment(MonsterRuleActions actions,
        IReadOnlyList<PluginEquipmentItem> items, uint primary)
    {
        if (actions.OffhandObjectId != 0u || actions.OffhandName.Length != 0)
            return ResolveEquipmentObjectId(actions.OffhandObjectId, actions.OffhandName, items);
        return actions.SecondaryEquipRaw switch
        {
            (int)VtankSecondaryEquip.None => 0u,
            (int)VtankSecondaryEquip.AutoWeapon => SelectAutomaticSecondaryWeapon(items, primary),
            (int)VtankSecondaryEquip.AutoShield => null,
            (int)VtankSecondaryEquip.Auto => ResolveAutomaticSecondary(items, primary),
            _ => 0u,
        };
    }

    private uint? ResolveAutomaticSecondary(IReadOnlyList<PluginEquipmentItem> items, uint primary)
    {
        bool shield = IsTrained(_host.Automation.Character, 48u);
        bool dualWield = IsTrained(_host.Automation.Character, 49u);
        uint shieldItem = SelectAutomaticShield(items);
        if (shield && !dualWield)
            return shieldItem;
        uint weapon = SelectAutomaticSecondaryWeapon(items, primary);
        if (!shield && dualWield)
            return weapon;
        return shieldItem != 0u ? shieldItem : weapon;
    }
    private uint SelectAutomaticShield(IReadOnlyList<PluginEquipmentItem> items)
    {
        foreach (PluginEquipmentItem item in InProfileOrder(items))
        {
            if (item.ValidLocations == 0x00200000u
                && item.ObjectClass is not (PluginObjectClass.MeleeWeapon or PluginObjectClass.MissileWeapon or PluginObjectClass.WandStaffOrb)
                && CombatProfileItems.IsProfiled(_settings, item.ObjectId, item.Name))
                return item.ObjectId;
        }
        return 0u;
    }
    private uint SelectAutomaticSecondaryWeapon(IReadOnlyList<PluginEquipmentItem> items, uint primary)
    {
        foreach (PluginEquipmentItem item in InProfileOrder(items))
        {
            if (item.ObjectId == primary || item.ObjectClass != PluginObjectClass.MeleeWeapon
                || (item.ValidLocations & 0x02000000u) != 0u
                || (VtankItemUseSpecifiers.UsesFor(_settings, item.ObjectId) & 2) == 0
                || !CombatProfileItems.IsProfiled(_settings, item.ObjectId, item.Name)
                || !ConfiguredSupplyReadiness.IsAssessed(_host.Automation, item.ObjectId))
                continue;
            return item.ObjectId;
        }
        return 0u;
    }
    private MonsterDamageType ResolveAttackElement(
        MonsterRuleActions actions,
        in PluginCombatTarget target)
    {
        (MonsterRuleActions, uint) key = (actions, target.ObjectId);
        if (_passElements.TryGetValue(key, out MonsterDamageType cached))
            return cached;
        MonsterDamageType resolved = ResolveAttackElementCore(actions, target);
        _passElements[key] = resolved;
        return resolved;
    }

    private MonsterDamageType ResolveAttackElementCore(
        MonsterRuleActions actions,
        in PluginCombatTarget target)
    {
        MonsterDamageType requested = actions.DamageType;
        if (requested == MonsterDamageType.Fists)
            return MonsterDamageType.Bludgeon;
        // Auto and Prismatic both resolve the element; Prismatic differs only
        // in which ammunition it will accept.
        if (requested is not (MonsterDamageType.Auto or MonsterDamageType.Prismatic))
            return requested;

        IReadOnlyList<PluginEquipmentItem> owned = PassEquipment();
        uint weapon = PlannedWeaponFor(actions, in target, owned);
        PluginCombatMode kind = WeaponStance(actions, weapon, owned);

        // The weapon's OWN element: what its imbue rends, then what it cleaves,
        // then the damage it plainly deals.
        MonsterDamageType element = WeaponElement(weapon, owned);

        // Only a wand's element falls back to the caster's training. A melee
        // or missile build with no war magic must not be handed Void or drain.
        if (kind == PluginCombatMode.Magic)
        {
            MonsterDamageType cascade =
                AttackSpellCatalog.ResolveMagicDamageMode(
                    MonsterDamageType.Auto,
                    _host.Automation.Character);
            if (cascade == MonsterDamageType.VoidBasic)
                return cascade;
            if (cascade == MonsterDamageType.DrainAuto)
            {
                PostAttackWarning(
                    "Warning: autoselecting drain as damage type. If you are "
                    + "not a martyr mage, you probably need to add your "
                    + "weapons to the items tab.");
                return cascade;
            }
        }
        if (element != MonsterDamageType.None)
            return element;

        IReadOnlyList<MonsterDamageType> preferences =
            _gameInfo.DamagePreferences(target.Name);
        foreach (MonsterDamageType preference in preferences)
        {
            if (preference != MonsterDamageType.None
                && CanDeliverElement(preference, weapon, owned))
            {
                return preference;
            }
        }

        foreach (MonsterDamageType unlisted in VtankDamageDatabase.UnlistedElementOrder)
        {
            if (Contains(preferences, unlisted)
                || !CanDeliverElement(unlisted, weapon, owned))
            {
                continue;
            }
            PostAttackWarning(
                "Warning: no ammunition available for any of target's possible "
                + "damage types! Using unlisted damage type: "
                + ElementName(unlisted));
            return unlisted;
        }
        PostAttackWarning("No ammunition available at all!");
        return MonsterDamageType.None;
    }

    /// <summary>
    /// The weapon this rule will fight with: the one it names, else the one
    /// automatic selection would reach for. A rule that spells the weapon
    /// column as zero means "no weapon, use a wand" and names none.
    /// </summary>
    private uint PlannedWeaponFor(
        MonsterRuleActions actions,
        in PluginCombatTarget target,
        IReadOnlyList<PluginEquipmentItem> owned)
    {
        if (actions.WeaponToUseRaw == 0)
            return 0u;
        uint named = ResolveRuleWeaponObjectId(actions, owned);
        if (named != 0u)
            return named;
        uint automatic = SelectAutomaticWeapon(owned, actions, in target);
        if (automatic != 0u)
            return automatic;
        // Nothing was named and nothing could be picked for the element the
        // rule asked for, so the weapon already in hand is what the fight will
        // be had with.
        PluginEquipmentItem? wielded = CombatModeGate.FindWielded(owned);
        return wielded is { } current
            && (VtankItemUseSpecifiers.UsesFor(_settings, current.ObjectId) & 1) != 0
                ? current.ObjectId
                : 0u;
    }

    /// <summary>
    /// The stance the planned weapon implies. With no weapon at all the rule
    /// means a wand, so the stance is Magic.
    /// </summary>
    private static PluginCombatMode WeaponStance(
        MonsterRuleActions actions,
        uint weapon,
        IReadOnlyList<PluginEquipmentItem> owned)
    {
        if (weapon == 0u)
            return PluginCombatMode.Magic;
        foreach (PluginEquipmentItem item in owned)
        {
            if (item.ObjectId == weapon)
                return CombatModeGate.ModeFor(in item);
        }
        return actions.WeaponToUseRaw == 0
            ? PluginCombatMode.Magic
            : PluginCombatMode.Melee;
    }

    private static MonsterDamageType WeaponElement(
        uint weapon,
        IReadOnlyList<PluginEquipmentItem> owned)
    {
        if (weapon == 0u)
            return MonsterDamageType.None;
        foreach (PluginEquipmentItem item in owned)
        {
            if (item.ObjectId == weapon)
            {
                return VtankWeaponElement.Resolve(
                    item.ImbuedEffect,
                    item.ResistanceCleaving,
                    item.DamageType);
            }
        }
        return MonsterDamageType.None;
    }

    /// <summary>
    /// The character's equipment as this pass sees it. The host builds that
    /// projection by walking every object it knows and sorting the result, so
    /// it is read once per pass and shared: a pass that has to choose again
    /// ten times over unreachable monsters must not walk the world ten times.
    /// Anything that changes what is worn ends the pass, so the pass can
    /// never act on a stale answer.
    /// </summary>
    private IReadOnlyList<PluginEquipmentItem> PassEquipment()
    {
        if (_passEquipment is not null)
            return _passEquipment;
        IEquipmentAutomation equipment = _host.Automation.Equipment;
        _passEquipment = equipment.IsAvailable
            ? equipment.CaptureOwnedEquipment()
            : Array.Empty<PluginEquipmentItem>();
        return _passEquipment;
    }

    /// <summary>
    /// The character's carried items as this pass sees it, on the same terms
    /// as <see cref="PassEquipment"/>.
    /// </summary>
    private IReadOnlyList<PluginInventoryItem> PassInventory() =>
        _passInventory ??= _host.Automation.Items.CaptureOwnedItems();

    private static bool Contains(
        IReadOnlyList<MonsterDamageType> elements,
        MonsterDamageType element)
    {
        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i] == element)
                return true;
        }
        return false;
    }

    private MonsterDamageType? RuleWeaponElement(MonsterRuleActions actions)
    {
        IReadOnlyList<PluginEquipmentItem> owned = PassEquipment();
        if (owned.Count == 0)
            return null;
        uint weapon = ResolveRuleWeaponObjectId(actions, owned);
        if (weapon == 0u)
            return null;
        foreach (PluginEquipmentItem item in owned)
        {
            if (item.ObjectId == weapon)
                return ProtocolDamageElement(item.DamageType);
        }
        return null;
    }

    private static MonsterDamageType? ProtocolDamageElement(int damageType) =>
        (damageType & 0x0020) != 0 ? MonsterDamageType.Acid
        : (damageType & 0x0004) != 0 ? MonsterDamageType.Bludgeon
        : (damageType & 0x0008) != 0 ? MonsterDamageType.Cold
        : (damageType & 0x0010) != 0 ? MonsterDamageType.Fire
        : (damageType & 0x0040) != 0 ? MonsterDamageType.Electric
        : (damageType & 0x0001) != 0 ? MonsterDamageType.Slash
        : (damageType & 0x0002) != 0 ? MonsterDamageType.Pierce
        : (damageType & 0x0400) != 0 ? MonsterDamageType.VoidBasic
        : null;

    /// <summary>
    /// Whether the weapon in hand can actually put this element on a monster.
    /// Only a launcher can fail: it needs ammunition of that element. Every
    /// other weapon, and a wand, can always deliver.
    /// </summary>
    private bool CanDeliverElement(
        MonsterDamageType element,
        uint weapon,
        IReadOnlyList<PluginEquipmentItem> owned)
    {
        (MonsterDamageType, uint) key = (element, weapon);
        if (_passDeliverable.TryGetValue(key, out bool cached))
            return cached;
        bool deliverable = CanDeliverElementCore(element, weapon, owned);
        _passDeliverable[key] = deliverable;
        if (!deliverable)
        {
            PostAttackWarning(
                "Skipping the bow element " + ElementName(element)
                + ": no ammunition for it.");
        }
        return deliverable;
    }

    private bool CanDeliverElementCore(
        MonsterDamageType element,
        uint weapon,
        IReadOnlyList<PluginEquipmentItem> owned)
    {
        int launcherType = 0;
        foreach (PluginEquipmentItem item in owned)
        {
            if (item.ObjectId != weapon)
                continue;
            launcherType = VtankAmmunitionDatabase.LauncherType(in item);
            break;
        }
        if (launcherType == 0)
            return true;
        if (!_gameInfo.IsLoaded)
            return false;

        return VtankAmmunitionDatabase.Select(
            _gameInfo.AmmunitionOptions,
            launcherType,
            element,
            VtankPrismaticAmmoPolicy.Any,
            _settings.UseSpecialAmmo,
            _host.Automation.Character,
            AmmunitionAvailability()) is not null;
    }

    private Func<string, bool>? _passAmmunitionAvailability;

    /// <summary>
    /// Whether a named stack of ammunition is in the pack, or could be made.
    /// Answered once per pass per name.
    /// </summary>
    private Func<string, bool> AmmunitionAvailability()
    {
        if (_passAmmunitionAvailability is not null)
            return _passAmmunitionAvailability;

        Dictionary<string, int>? counts = null;
        var answers = new Dictionary<string, bool>(StringComparer.Ordinal);
        _passAmmunitionAvailability = name =>
        {
            if (answers.TryGetValue(name, out bool cached))
                return cached;
            counts ??= PassInventory()
                .GroupBy(static item => item.Name, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Sum(item => item.StackSize),
                    StringComparer.Ordinal);
            bool answer = counts.GetValueOrDefault(name) >= 1
                || _resolveAmmunitionCraft?.Invoke(name, 1) is not null;
            answers[name] = answer;
            return answer;
        };
        return _passAmmunitionAvailability;
    }

    private MonsterDamageType ResolveExtraVulnerability(
        MonsterRuleActions actions,
        in PluginCombatTarget target)
    {
        switch (actions.ExtraVulnerability)
        {
            case MonsterDamageType.Pierce:
            case MonsterDamageType.Bludgeon:
            case MonsterDamageType.Slash:
            case MonsterDamageType.Acid:
            case MonsterDamageType.Electric:
            case MonsterDamageType.Cold:
            case MonsterDamageType.Fire:
                return actions.ExtraVulnerability;
            case MonsterDamageType.Auto:
                IReadOnlyList<MonsterDamageType> preferences =
                    _gameInfo.DamagePreferences(target.Name);
                return preferences.Count > 0
                    ? preferences[0]
                    : MonsterDamageType.None;
            default:
                return MonsterDamageType.None;
        }
    }

    private int DebuffUrgency(
        in PluginCombatTarget target,
        MonsterDamageType element,
        uint plannedWeapon,
        IReadOnlyList<PluginEquipmentItem> equipment)
    {
        const uint meleeWeapon = 0x00000001u;
        const uint missileWeapon = 0x00000100u;
        int score = 0;
        var vulnerability = new DebuffIdentity(
            MonsterActionFlags.Vulnerability,
            element);
        if (_debuffs.IsApplied(
                target.ObjectId,
                vulnerability,
                FindDebuffSpell(vulnerability),
                _now))
        {
            score++;
        }

        bool physical = false;
        if (plannedWeapon != 0u)
        {
            foreach (PluginEquipmentItem item in equipment)
            {
                if (item.ObjectId != plannedWeapon)
                    continue;
                physical = (item.ItemType & (meleeWeapon | missileWeapon)) != 0u;
                break;
            }
        }
        var identity = new DebuffIdentity(
            physical ? MonsterActionFlags.Imperil : MonsterActionFlags.Yield,
            MonsterDamageType.Auto);
        if (_debuffs.IsApplied(
                target.ObjectId,
                identity,
                FindDebuffSpell(identity),
                _now))
        {
            score += 2;
        }
        return score;
    }

    private PluginSpellInfo? FindDebuffSpell(DebuffIdentity identity)
    {
        if (_passDebuffSpells.TryGetValue(identity, out PluginSpellInfo? cached))
            return cached;
        PluginSpellInfo? found = FindDebuffSpellCore(identity);
        _passDebuffSpells[identity] = found;
        return found;
    }

    private PluginSpellInfo? FindDebuffSpellCore(DebuffIdentity identity)
    {
        PluginSpellInfo? best = null;
        foreach (PluginSpellInfo spell in _host.Automation.Spells.KnownCombatSpells)
        {
            if (!DebuffSpellCatalog.TryClassify(spell, out DebuffIdentity found, out _)
                || found != identity)
            {
                continue;
            }
            if (best is not { } current || spell.Tier > current.Tier)
                best = spell;
        }
        return best;
    }

    private IReadOnlyList<CombatDebuffSource> DebuffSources(
        DebuffIdentity identity,
        in PluginCombatTarget target,
        IReadOnlyList<PluginInventoryItem> inventory,
        Action<string>? log)
    {
        (DebuffIdentity, uint) key = (identity, target.ObjectId);
        if (_passDebuffSources.TryGetValue(
                key,
                out IReadOnlyList<CombatDebuffSource>? cached))
        {
            return cached;
        }
        IReadOnlyList<CombatDebuffSource> sources = CombatItemDebuffPlanner.Sources(
            identity,
            _settings,
            _host.Automation.Character,
            _host.Automation.Spells,
            inventory,
            _gameInfo.GrenadeOptions,
            target.Distance,
            log);
        _passDebuffSources[key] = sources;
        return sources;
    }

    /// <summary>
    /// The one source this step will be applied from, or null when there is
    /// none. Exactly one wins: a source the character cannot use is not
    /// quietly replaced by the next-best one inside a single decision.
    /// </summary>
    private CombatDebuffSource? ChooseDebuffSource(
        DebuffIdentity identity,
        in PluginCombatTarget target,
        IReadOnlyList<PluginInventoryItem> inventory,
        Action<string>? log = null)
    {
        IReadOnlyList<CombatDebuffSource> sources = DebuffSources(
            identity,
            in target,
            inventory,
            log);
        foreach (CombatDebuffSource source in sources)
        {
            // With the fallback allowed, a source whose flight this pass has
            // ALREADY found blocked steps aside for the next-best one. With it
            // off there is no stepping aside: the winner stands and its column
            // is turned off when its flight turns out to be blocked.
            if (_settings.AllowDebuffFallback
                && !KnownClear(target.ObjectId, source.PathKind))
            {
                continue;
            }
            return source;
        }
        return null;
    }

    private bool IsDebuffStepDue(
        CombatDebuffStep step,
        in PluginCombatTarget target,
        IReadOnlyList<PluginInventoryItem> inventory)
    {
        IReadOnlyList<CombatDebuffSource> sources = DebuffSources(
            step.Identity,
            in target,
            inventory,
            log: null);
        if (sources.Count == 0)
            return false;
        return _debuffs.IsDue(
            target.ObjectId,
            step.Identity,
            sources[0].Spell,
            _now,
            step.ZeroTolerance ? 0d : _settings.DebuffPrecastSeconds);
    }

    private bool TryFind(uint objectId, out PluginCombatTarget found)
    {
        foreach (PluginCombatTarget target in _targets)
        {
            if (target.ObjectId == objectId)
            {
                found = target;
                return true;
            }
        }
        found = default;
        return false;
    }

    private void SetTarget(
        PluginCombatTarget target,
        ResolvedMonsterRule resolved)
    {
        _targetId = target.ObjectId;
        _targetRule = resolved;
        _targetName = string.IsNullOrWhiteSpace(target.Name)
            ? $"0x{target.ObjectId:X8}"
            : target.Name;
        _targetDistance = target.Distance;
        _targetText = string.Create(
            CultureInfo.InvariantCulture, $"Target  {_targetName}  {_targetDistance:0.0}m");
        _health.SetTarget(_targetId, target.Name, _now);
        // Whatever the host already knows about this monster's health counts
        // as the first report, so the fight does not start a scan behind.
        _health.Observe(target, _now);
    }

    private void ClearTarget()
    {
        _health.Clear(_now);
        DisarmPhysicalResultText();
        // A swing belongs to the monster it was armed at. Dropping the target
        // drops the swing with it — the cancel is what ends it on the server —
        // and the wait on an answer that can no longer mean anything goes with
        // it, so the next monster is not held behind a dead one's swing and
        // the dead one is not charged a miss for never answering.
        if (_pendingPhysicalTarget != 0u)
        {
            _pendingPhysicalTarget = 0u;
            _host.Automation.Combat.AbortPhysicalAttack();
        }
        ReleaseSwingExecutorForRetarget();
        _physicalSwingSentAt = double.NegativeInfinity;
        _physicalSwingArmedAt = double.NegativeInfinity;
        _physicalSwingClosestDistance = float.PositiveInfinity;
        StopApproachMovement();
        StopBreakableTurnMovement();
        StopSelectionJiggle();
        _targetId = 0u;
        _targetRule = default;
        _targetName = string.Empty;
        _targetDistance = 0f;
        _targetText = "Target  —";
    }

    private void Disable(string status)
    {
        _host.Automation.Combat.AbortPhysicalAttack();
        ResetSwingExecutor();
        StopApproachMovement();
        StopBreakableTurnMovement();
        Enabled = false;
        _paused = false;
        _combatPolicySuspended = false;
        _targets = Array.Empty<PluginCombatTarget>();
        _combatSpellSnapshot = null;
        _attackSpellSnapshot = null;
        _attackCatalog = AttackSpellCatalog.Build(Array.Empty<PluginSpellInfo>());
        _debuffs.Reset();
        _failures.Reset();
        _health.Reset();
        _observedPhysicalCompletion = 0;
        _observedAttackCastCompletion = 0;
        _pendingPhysicalTarget = 0u;
        _pendingAttackSpell = 0u;
        _pendingAttackTarget = 0u;
        ClearPendingItemDebuff();
        _observedChatSequence = 0u;
        _itemTransactionChatSequence = 0u;
        _observedItemCompletion = 0;
        // The tracker's own reset: a stopped macro must not leave the busy
        // latch up.
        _castTracker.Reset();
        _plannedWeapon = 0u;
        _plannedOffhand = null;
        Gate.Reset();
        _randomDamageIndex = 0;
        _observedJiggleCastCompletion = 0;
        ClearTarget();
        Status = status;
        _combatControl?.Dispose();
        _combatControl = null;
    }

    /// <summary>
    /// The reference's gate on the monster approach's idle-peace fallback:
    /// true while the monster worth walking to is further than the creep
    /// distance, or while there is none.
    /// </summary>
    internal bool IsApproachOutsideCreepDistance()
    {
        if (!Enabled || !_settings.Enabled || !_host.Automation.IsAvailable)
            return true;
        ClearPassMemos();
        return SelectApproachTarget() is not { } approach
            || approach.Distance >= NavigationMover.CreepDistanceMeters;
    }

    /// <summary>
    /// The walk's own turn on the rule pass: it answers the pass and arms
    /// the mover.
    /// </summary>
    internal bool ClaimMonsterApproachFromRulePass(bool canAct)
    {
        // The pass consumes whatever the mover was owed, so a rule turn and
        // a mover frame never both spend the same time.
        _ = _approachMover.TakePendingSeconds();
        bool claimed = TickMonsterApproach(canAct);
        _approachMover.Arm(claimed);
        return claimed;
    }

    /// <summary>
    /// One host frame of the armed mover. The monster moves and so does the
    /// character, so the choice of monster, the bearing and the range are all
    /// re-asked on the mover's own interval rather than once per rule pass —
    /// a walk steered once per pass overshoots every turn, because one pass
    /// of held turn is tens of degrees against a four-degree band.
    /// </summary>
    internal void StepArmedApproachMover(double elapsedSeconds)
    {
        if (_approachMover.TryTakeMoverFrame(elapsedSeconds, out _))
            _ = TickMonsterApproach(canAct: true);
    }

    /// <summary>
    /// Walking to a monster the character cannot yet hit. This is its OWN job,
    /// twenty positions below the attack, with its own candidate pick at the
    /// approach range: the attack must not claim the pass for a monster it
    /// would have to walk to, or nothing below the attack ever runs.
    /// </summary>
    /// <returns>True while there is a monster worth walking to.</returns>
    internal bool TickMonsterApproach(bool canAct)
    {
        if (!Enabled
            || !_settings.Enabled
            || !_host.Automation.IsAvailable
            || !canAct
            || _settings.ApproachDistance <= _settings.MaximumRange)
        {
            StopApproachMovement();
            return false;
        }

        // This rule is its own pass: the attack's may not have run at all (its
        // gate can refuse for seconds at a time), so everything the attack
        // pass learns and forgets per pass is taken fresh here rather than
        // inherited stale — a column another pass turned off must not silently
        // narrow the walk's choice of monster.
        ClearPassMemos();
        if (SelectApproachTarget() is not { } approach)
        {
            StopApproachMovement();
            return false;
        }
        // The walk ends where the attack begins.
        if (approach.Distance <= _settings.MaximumRange)
        {
            StopApproachMovement();
            return false;
        }
        return TickApproachTo(
            approach.ObjectId,
            approach.Target.Name,
            approach.Distance);
    }

    /// <summary>
    /// The same comparison chain the attack runs, over the monsters inside the
    /// approach range rather than the ones inside weapon range.
    /// </summary>
    private CombatTargetCandidate? SelectApproachTarget()
    {
        IReadOnlyList<PluginCombatTarget> reachable =
            _host.Automation.Combat.CaptureHostileTargets(
                (float)_settings.ApproachDistance);
        if (reachable.Count == 0)
            return null;

        PluginCombatSnapshot combat = _host.Automation.Combat.Snapshot;
        RefreshSpellCatalogs();
        IReadOnlyList<PluginInventoryItem> inventory =
            PassInventory();
        IReadOnlyList<PluginEquipmentItem> equipment = PassEquipment();
        (uint wieldedWeapon, uint wieldedOffhand) = WieldedPair(equipment);

        var candidates = new List<CombatTargetCandidate>();
        foreach (PluginCombatTarget target in reachable)
        {
            if (TryBuildCandidate(
                    target,
                    combat,
                    _lastTargetId,
                    inventory,
                    equipment,
                    _settings.ApproachDistance,
                    out CombatTargetCandidate candidate))
            {
                candidates.Add(candidate);
            }
        }
        if (candidates.Count == 0)
            return null;

        CombatTargetCandidate chosen = CombatTargetSelector.Select(
            candidates,
            _settings.DebuffEachFirst,
            _settings.SelectionMethod,
            _settings.TargetSelectAngleRange,
            wieldedWeapon,
            wieldedOffhand);
        return chosen.ObjectId == 0u ? null : chosen;
    }

    private bool TickApproachTo(uint objectId, string name, double distance)
    {
        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationSnapshot self = navigation.Snapshot;
        if (!self.IsAvailable || self.IsPortalSpace
            || !navigation.TryGetObject(
                objectId,
                out PluginNavigationObject target))
        {
            StopApproachMovement();
            return false;
        }

        string label = string.IsNullOrWhiteSpace(name)
            ? $"0x{objectId:X8}"
            : name;
        Status = $"Approaching {label} ({distance:0.0}m)";
        return _approachMover.Steer(
            navigation,
            self.Position,
            target.Position,
            distance);
    }

    private bool ReadyForBreakableTurn(
        in PluginSpellInfo spell,
        uint targetObjectId)
    {
        if (!_settings.UseBreakableTurnTo
            || !spell.RequiresTurnTo
            || targetObjectId == 0u
            || targetObjectId == _host.Automation.Character.ObjectId)
        {
            StopBreakableTurnMovement();
            return true;
        }

        if (!DriveBreakableTurn(targetObjectId))
        {
            StopBreakableTurnMovement();
            return true;
        }
        HoldPassForTurn(targetObjectId);
        return false;
    }

    /// <summary>
    /// The unconditional turn the fists arm makes. It is not the breakable
    /// turn and does not read that option: the character faces the monster,
    /// a shade to one side of dead-on, before the spell goes out.
    /// </summary>
    /// <returns>True once the character is facing where it needs to.</returns>
    private bool FaceForFists(uint targetObjectId)
    {
        const float LeadDegrees = 180f / 50f;
        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationSnapshot self = navigation.Snapshot;
        if (!self.IsAvailable
            || self.IsPortalSpace
            || !navigation.TryGetObject(
                targetObjectId,
                out PluginNavigationObject target))
        {
            return true;
        }

        float desired = NavigationController.DesiredHeading(
            self.Position,
            target.Position) - LeadDegrees;
        float delta = NavigationController.SignedHeadingDelta(
            self.Position.HeadingDegrees,
            desired);
        if (MathF.Abs(delta) <= BreakableTurnToleranceDegrees)
            return true;

        if (navigation.ClearMovementIntent()
            != PluginNavigationCommandStatus.Accepted)
        {
            return true;
        }
        if (_now - _breakableTurnFaceHeadingStamp
            >= NavigationController.FaceHeadingReissueSeconds)
        {
            _breakableTurnFaceHeadingStamp = _now;
            if (navigation.FaceHeading(desired)
                != PluginNavigationCommandStatus.Accepted)
            {
                return true;
            }
        }
        _breakableTurnOwned = true;
        Status = $"Turning to {_targetName} ({delta:+0.0;-0.0}°)";
        return false;
    }

    /// <summary>
    /// One step of a turn already in flight. Returns true while the character
    /// still has turning left to do.
    /// </summary>
    private bool DriveBreakableTurn(uint targetObjectId)
    {
        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationSnapshot self = navigation.Snapshot;
        if (!self.IsAvailable
            || self.IsPortalSpace
            || !navigation.TryGetObject(
                targetObjectId,
                out PluginNavigationObject target))
        {
            return false;
        }

        float desired = NavigationController.DesiredHeading(
            self.Position,
            target.Position);
        float delta = NavigationController.SignedHeadingDelta(
            self.Position.HeadingDegrees,
            desired);
        if (MathF.Abs(delta) <= BreakableTurnToleranceDegrees)
            return false;

        if (navigation.ClearMovementIntent()
            != PluginNavigationCommandStatus.Accepted)
        {
            return false;
        }
        if (_now - _breakableTurnFaceHeadingStamp
            >= NavigationController.FaceHeadingReissueSeconds)
        {
            _breakableTurnFaceHeadingStamp = _now;
            if (navigation.FaceHeading(desired)
                != PluginNavigationCommandStatus.Accepted)
            {
                return false;
            }
        }
        _breakableTurnOwned = true;
        Status = $"Turning to {_targetName} ({delta:+0.0;-0.0}°)";
        return true;
    }

    /// <summary>
    /// Turning owns the character, so it owns the rule pass too: nothing else
    /// may claim a turn while the character is still swinging round to face
    /// its target. Raised once for the life of one turn.
    /// </summary>
    private void HoldPassForTurn(uint targetObjectId)
    {
        _breakableTurnTargetId = targetObjectId;
        if (_turnHoldsPass)
            return;
        _turnHoldsPass = true;
        _suspendPass();
    }

    /// <summary>True while a turn to face the target holds the rule pass.</summary>
    internal bool TurnHoldsPass => _turnHoldsPass;

    /// <summary>
    /// Seconds a frame driver has added to the clock since the attack's turn
    /// last ran; the next turn subtracts them, so one wall clock is kept
    /// between the turn and the frame.
    /// </summary>
    private double _frameAdvancedSinceTick;

    private void AdvanceClockFromFrame(double elapsedSeconds)
    {
        double elapsed = Math.Max(0d, elapsedSeconds);
        _now += elapsed;
        _frameAdvancedSinceTick += elapsed;
    }

    /// <summary>The controller's clock, in seconds; a probe for tests.</summary>
    internal double ClockSeconds => _now;

    /// <summary>
    /// A HELD ITEM's cast is a transaction of its own: it began on a turn the
    /// attack owned, it holds the item slot, and it finishes on its own clock
    /// whoever owns the pass meanwhile. While the attack is not being asked
    /// (it lost the turn), the host frame watches that cast to its end here,
    /// including putting the item slot down early. Only the held item is such
    /// a transaction: a weapon proc or a thrown one rides a physical swing,
    /// and losing the turn has just aborted that swing.
    /// </summary>
    internal void ObserveHeldItemCast(double elapsedSeconds)
    {
        if (_heldItemDrivenByTurn)
        {
            _heldItemDrivenByTurn = false;
            return;
        }
        if (_pendingItemDebuff is not
                { Source.Kind: CombatDebuffSourceKind.CasterItem })
        {
            return;
        }
        AdvanceClockFromFrame(elapsedSeconds);
        ObserveItemTransaction();
        TickPendingItemDebuff(_host.Automation.Combat.Snapshot);
    }

    /// <summary>
    /// Reads the server's answer to a LEARNED debuff cast, on the host frame
    /// rather than on the attack's turn. Such a cast holds the whole pass, so
    /// the pass cannot be what ends the wait — it would be waiting on itself,
    /// and worse, the attack only gets a turn when it wins the pass, which it
    /// cannot do while its own cast is holding it. Nothing is issued here.
    /// </summary>
    internal void ObserveLearnedDebuffReceipt(double elapsedSeconds)
    {
        if (!_debuffs.HasPending || !_host.Automation.IsAvailable)
            return;
        AdvanceClockFromFrame(elapsedSeconds);
        OnLearnedDebuffCompleted(
            _debuffs.Observe(_host.Automation.Magic.LastCompletion, _now));
        _debuffs.ExpirePending(_now);
    }

    /// <summary>
    /// The server's answer to a learned debuff, from whichever reader saw it
    /// first: the pass or the host frame. The tracker counts receipts by
    /// revision, so each answer arrives here once. A failure is only a
    /// status line; a success is told to the other clients on this computer.
    /// </summary>
    private void OnLearnedDebuffCompleted(DebuffCompletion completion)
    {
        if (!completion.Completed)
            return;
        if (!completion.Succeeded)
        {
            Status = $"{completion.SpellName} failed (0x{completion.WeenieError:X})";
            return;
        }
        if (completion.Spell is { } spell)
            AnnounceCastSuccess(completion.TargetObjectId, spell);
    }

    /// <summary>
    /// Tells the other clients on this computer that a debuff is going out at
    /// a target, with the skill it is cast with, so a second character can
    /// decide not to start the same spell at the same target.
    /// </summary>
    private void AnnounceCastAttempt(uint targetObjectId, PluginSpellInfo spell)
    {
        if (!_castSharingEnabled())
            return;
        _host.Automation.Network.AnnounceCastAttempt(
            targetObjectId,
            spell.SpellId,
            EffectiveSkillFor(spell));
    }

    /// <summary>
    /// Tells the other clients on this computer that a debuff landed and how
    /// long its effect lasts, so a second character can stand down rather
    /// than land it again. A spell with no lasting effect has nothing to tell.
    /// </summary>
    private void AnnounceCastSuccess(uint targetObjectId, PluginSpellInfo spell)
    {
        if (!_castSharingEnabled() || !(spell.DurationSeconds > 0f))
            return;
        _host.Automation.Network.AnnounceCastSuccess(
            targetObjectId,
            spell.SpellId,
            EffectiveSkillFor(spell),
            spell.DurationSeconds);
    }

    /// <summary>
    /// The character's current skill in the spell's school, or zero when
    /// the school is unknown or the character has no such skill: the number
    /// another client would weigh this cast by.
    /// </summary>
    private int EffectiveSkillFor(PluginSpellInfo spell) =>
        spell.School != 0u
        && _host.Automation.Character.TryGetSkill(spell.School, out PluginSkillInfo skill)
            ? (int)Math.Min(skill.Current, int.MaxValue)
            : 0;

    /// <summary>
    /// Whether casts are shared with the other clients on this computer at
    /// all, and which of their tags a peer must carry for its casts to be
    /// taken in here: null or blank means every peer. Both are asked each
    /// time, so a change on the settings page applies on the next frame.
    /// </summary>
    internal void BindCastSharing(Func<bool> enabled, Func<string?> peerTag)
    {
        _castSharingEnabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _castSharingPeerTag = peerTag ?? throw new ArgumentNullException(nameof(peerTag));
    }

    /// <summary>
    /// Takes in what the other clients on this computer have said they cast
    /// since the last look. A landed cast is mirrored into the host's own
    /// ledger of effects, so the host and every other plugin agree on what
    /// is on the target; one that classifies as a debuff this macro tracks
    /// lands in the debuff table as well, where target selection, the debuff
    /// chain and the element choice all read it. Driven from the host frame
    /// beside the other frame drivers, so a peer's cast is known before the
    /// next pass rather than a pass later. Nothing is issued here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing about a cast is trusted from the wire: the spell is looked up
    /// in this client's own table and classified here, a cast naming this
    /// character as the caster is this character's own and is skipped, and
    /// an attempt that has not landed yet records nothing.
    /// </para>
    /// <para>
    /// What is left of the effect comes as seconds remaining, already aged by
    /// the host, and is stamped onto this controller's own clock as now plus
    /// those seconds. The clock only moves when the pass runs, so the stamp
    /// can be up to one pass behind, which errs on the early side: the
    /// effect is thought to lapse a little sooner than it does, never later.
    /// The table is only written while the macro runs, because it is cleared
    /// when the macro stops and the clock stands still meanwhile.
    /// </para>
    /// </remarks>
    internal void ObserveRemoteCasts()
    {
        if (!_host.Automation.IsAvailable || !_castSharingEnabled())
            return;
        INetworkAutomation network = _host.Automation.Network;
        IReadOnlyList<PluginPeerCast> casts = network.CaptureCasts(_remoteCastCursor);
        if (casts.Count == 0)
            return;
        uint self = _host.Automation.Character.ObjectId;
        HashSet<uint>? trusted = null;
        bool trustedCaptured = false;
        bool tableLive = Enabled && !_combatPolicySuspended;
        foreach (PluginPeerCast cast in casts)
        {
            if (cast.CasterObjectId == self
                || !cast.Landed
                || !(cast.SecondsRemaining > 0d))
            {
                _remoteCastCursor = Math.Max(_remoteCastCursor, cast.Sequence);
                continue;
            }
            // Attempts and expired/self casts need no second peer-directory
            // read. Take one current trust snapshot only when it can be used.
            if (!trustedCaptured)
            {
                trusted = TrustedPeerClients(network);
                trustedCaptured = true;
            }
            _remoteCastCursor = Math.Max(_remoteCastCursor, cast.Sequence);
            if ((trusted is not null && !trusted.Contains(cast.ClientId))
                || !_host.Automation.Spells.TryGet(cast.SpellId, out PluginSpellInfo spell))
            {
                continue;
            }
            _host.Automation.Enchantments.ReportCast(
                cast.TargetObjectId,
                cast.SpellId,
                cast.SecondsRemaining);
            if (!tableLive
                || !DebuffSpellCatalog.TryClassify(spell, out DebuffIdentity identity, out _)
                || !_debuffs.RecordRemote(
                    cast.TargetObjectId,
                    identity,
                    spell,
                    _now,
                    cast.SecondsRemaining))
            {
                continue;
            }
            Log?.Invoke(
                MacroLogChannel.SpellCast,
                $"Peer cast: {spell.Name} on {cast.TargetObjectId} by "
                + $"0x{cast.CasterObjectId:X8}, {cast.SecondsRemaining:0} s left");
        }
    }

    /// <summary>
    /// The peers whose casts are taken in, or null for every peer. The tag
    /// is one the peer client was started with; a peer carrying none of the
    /// wanted tag is ignored, and so is one whose published record has gone
    /// stale, since nothing then says what it was.
    /// </summary>
    private HashSet<uint>? TrustedPeerClients(INetworkAutomation network)
    {
        string? tag = _castSharingPeerTag()?.Trim();
        if (string.IsNullOrEmpty(tag))
            return null;
        var trusted = new HashSet<uint>();
        foreach (PluginNetworkClient client in network.CaptureClients())
        {
            foreach (string candidate in client.Tags)
            {
                if (!candidate.Equals(tag, StringComparison.OrdinalIgnoreCase))
                    continue;
                trusted.Add(client.ClientId);
                break;
            }
        }
        return trusted;
    }

    /// <summary>
    /// Steps a turn that is holding the pass. The pass itself is frozen while
    /// the hold is up, so the turn needs a driver outside it — the host frame.
    /// </summary>
    internal void AdvanceHeldTurn(double elapsedSeconds)
    {
        if (!_turnHoldsPass)
            return;
        AdvanceClockFromFrame(elapsedSeconds);
        if (_breakableTurnTargetId == 0u
            || !DriveBreakableTurn(_breakableTurnTargetId))
        {
            StopBreakableTurnMovement();
        }
    }

    private void StopBreakableTurnMovement()
    {
        _breakableTurnFaceHeadingStamp =
            NavigationController.NoFaceHeadingStamp;
        _breakableTurnTargetId = 0u;
        if (_turnHoldsPass)
        {
            _turnHoldsPass = false;
            _resumePass();
        }
        if (!_breakableTurnOwned)
            return;
        _host.Automation.Navigation.ClearMovementIntent();
        _breakableTurnOwned = false;
    }

    /// <summary>
    /// The monster-approach rule losing its turn. The reference writes
    /// Running=false to every loser on every pass, and a navigate rule
    /// told that releases the keys it was holding -- otherwise the walk it
    /// started carries on unsupervised under whichever rule won, and two
    /// movement owners steer at once.
    /// </summary>
    internal void StopMonsterApproachForLostTurn() =>
        _approachMover.StopForLostTurn();

    /// <summary>
    /// Drops whatever the walk is holding without taking the turn away: the
    /// pass that armed the mover is the only thing allowed to disarm it.
    /// </summary>
    private void StopApproachMovement() => _approachMover.StopMovement();

    private readonly record struct RuleCandidate(
        PluginCombatTarget Target,
        ResolvedMonsterRule Rule);

    private enum DebuffStartResult
    {
        Skipped,
        Handled,
    }

    private sealed class PendingItemDebuff(
        CombatDebuffSource source,
        uint targetObjectId,
        string targetName,
        string itemName,
        double dispatchedAt,
        long itemCompletionRevision,
        long physicalCompletionRevision,
        PluginCombatMode mode,
        float power)
    {
        public CombatDebuffSource Source { get; } = source;
        public uint TargetObjectId { get; } = targetObjectId;
        public string TargetName { get; } = targetName;
        public string ItemName { get; } = itemName;
        public double DispatchedAt { get; } = dispatchedAt;
        public long ItemCompletionRevision { get; } = itemCompletionRevision;
        public long PhysicalCompletionRevision { get; set; } =
            physicalCompletionRevision;
        public PluginCombatMode Mode { get; } = mode;
        public float Power { get; } = power;
        public double? AttackCompletedAt { get; set; }
        public bool RecoverySent { get; set; }
        public int RecoveryStage { get; set; }
    }

    private void ObserveAttackReceipts(
        PluginCombatSnapshot combat,
        PluginCastCompletion cast)
    {
        if (combat.CompletionRevision > _observedPhysicalCompletion)
        {
            _observedPhysicalCompletion = combat.CompletionRevision;
            // The server says the attack sequence finished. The macro keeps
            // reading result text for two seconds past this point, because
            // the last swing's outcome line can still be in flight.
            _physicalCompletedAt = _now;
            _pendingPhysicalTarget = 0u;
        }

        if (cast.Revision <= _observedAttackCastCompletion)
            return;
        _observedAttackCastCompletion = cast.Revision;

        if (_pendingAttackSpell == cast.SpellId)
        {
            _pendingAttackSpell = 0u;
            _pendingAttackTarget = 0u;
        }
    }

    /// <summary>
    /// The line the reference client prints when it gives a monster up as
    /// unhittable, so a watching player knows why the bot walked away.
    /// </summary>
    private void AnnounceBlacklist(bool tripped, uint objectId, string name)
    {
        if (!tripped)
            return;
        string shown = string.IsNullOrWhiteSpace(name)
            ? FindTarget(objectId).Name
            : name;
        if (string.IsNullOrWhiteSpace(shown))
            shown = "???";
        _host.Automation.Chat.PostSystemMessage(
            "Cannot hit "
            + shown
            + " ("
            + objectId.ToString(CultureInfo.InvariantCulture)
            + ") — blacklisted for "
            + ((int)_settings.BlacklistMonsterTimeoutSeconds)
                .ToString(CultureInfo.InvariantCulture)
            + " seconds.");
    }

    /// <summary>How often the stalled-health check runs.</summary>
    private const double GhostSweepIntervalSeconds = 6.271d;

    private double _untilGhostSweep = GhostSweepIntervalSeconds;

    /// <summary>
    /// A monster that has been engaged for a while and whose health has not
    /// moved once in all that time is very likely not there any more: the
    /// server has dropped it and the client is still drawing it. Only a
    /// monster the profile's database gives a health ceiling for can be judged
    /// this way — without a ceiling the client is never told the health in the
    /// first place, so "the health has not moved" says nothing.
    /// </summary>
    private void CheckStalledHealthGhost()
    {
        if (!Enabled
            || !_settings.DeleteGhostMonstersByHealthTracker
            || _health.TargetObjectId == 0u
            || !_settings.MonsterFacts.IsListed(_health.TargetName)
            || _settings.MonsterFacts.MaximumHealth(_health.TargetName) <= 0)
        {
            return;
        }
        double stale = Math.Max(0d, _settings.GhostDeleteHealthTrackerSeconds);
        // Health that has never moved counts as having last moved before the
        // fight started, so the acquisition age alone decides.
        double sinceChange = _health.LastHealthChangeAt is double changed
            ? _now - changed
            : double.PositiveInfinity;
        if (_now - _health.AcquiredAt < stale || sinceChange < stale)
            return;
        DeleteGhostMonster(
            _health.TargetObjectId,
            allowed: true,
            "due to HP tracker notification");
    }

    /// <summary>
    /// Asks the client to forget an object it is still drawing. Nothing else
    /// happens: there is no per-monster "give up on this one" flag, so a
    /// deletion the client refuses leaves the monster exactly as targetable as
    /// it was.
    /// </summary>
    private void DeleteGhostMonster(
        uint objectId,
        bool allowed,
        string reason = "")
    {
        if (objectId == 0u || !allowed)
            return;
        // A ghost is looked up in the whole world, not in the range-limited
        // scan: the monster that stopped answering is often the one that has
        // just dropped out of it, and the health tracker still has its name.
        PluginCombatTarget scanned = FindTarget(objectId);
        string name = scanned.ObjectId == objectId && scanned.Name.Length > 0
            ? scanned.Name
            : _health.TargetObjectId == objectId
                ? _health.TargetName
                : string.Empty;
        if (name.Length == 0)
            return;
        PluginCombatCommandResult result =
            _host.Automation.Combat.DismissGhostTarget(objectId);
        if (!result.Accepted)
            return;
        _host.Automation.Chat.PostSystemMessage(
            string.IsNullOrEmpty(reason)
                ? $"Deleting ghost monster {name} ({objectId})"
                : $"Deleting ghost monster {name} ({objectId}) {reason}.");
        // Losing the awaited target drops the tracker to idle; deleting a
        // ghost is our own version of that event.
        _castTracker.ResetForTarget(objectId);
        if (_targetId == objectId)
        {
            _host.Automation.Combat.AbortPhysicalAttack();
            ClearTarget();
        }
    }
}
