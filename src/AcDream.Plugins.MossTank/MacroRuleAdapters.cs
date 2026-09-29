using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

/// <summary>
/// A rule whose body is a controller's tick. The gate is the rule's first
/// refusal, the way the reference's wrapper gates and each rule's opening
/// lock checks are: while it is closed the controller is not consulted at
/// all. When the gate is open the controller's tick answers the pass and, on
/// the pass it wins, does the work; losing the turn is one call on the edge.
/// </summary>
internal sealed class ControllerMacroRule : IMacroRule
{
    private readonly Func<MacroPassContext, bool> _tick;
    private readonly Func<bool>? _gate;
    private readonly Action? _onLostTurn;
    private readonly Func<string?>? _runningDetail;
    private readonly Func<string?>? _declineReason;
    private bool _running;
    private bool _claimed;
    private bool _gateClosed;

    public ControllerMacroRule(
        string name,
        Func<MacroPassContext, bool> tick,
        Func<bool>? gate = null,
        Action? onLostTurn = null,
        Func<string?>? runningDetail = null,
        Func<string?>? declineReason = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _gate = gate;
        _onLostTurn = onLostTurn;
        _runningDetail = runningDetail;
        _declineReason = declineReason;
    }

    public string Name { get; }

    public string? RunningDetail => _runningDetail?.Invoke();

    /// <summary>
    /// The controller's own reason, unless the rule's gate is what closed —
    /// the controller never ran in that case and its last reason would be
    /// stale.
    /// </summary>
    public string? DeclineReason => _gateClosed
        ? "the rule's own gate is closed"
        : _declineReason?.Invoke();

    public bool ValidNow(in MacroPassContext context)
    {
        bool gateOpen = _gate is null || _gate();
        _gateClosed = !gateOpen;
        if (!gateOpen || !context.CanAct)
            return false;
        // Sticky until the rule loses the turn: a claim that a later pass
        // declines may still have left the mover armed, and only a
        // Running=false switch-off stops it.
        bool claims = _tick(new MacroPassContext(context.ElapsedSeconds, CanAct: true));
        if (claims)
            _claimed = true;
        return claims;
    }

    public bool Running
    {
        get => _running;
        set
        {
            // A pre-chain can run a fallback in place of a rule that claimed
            // the turn, so the rule was asked (and may have armed something)
            // without ever being run; losing the turn still has to stop it.
            bool lostTurn = !value && (_running || _claimed);
            if (!value)
                _claimed = false;
            if (_running == value && !lostTurn)
                return;
            _running = value;
            if (lostTurn)
                _onLostTurn?.Invoke();
        }
    }
}

internal sealed class AbsentMacroRule : IMacroRule
{
    public AbsentMacroRule(string name, string reason)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    public string Reason { get; }

    public string Name { get; }

    public bool ValidNow(in MacroPassContext context) => false;

    public bool Running
    {
        get => false;
        set { }
    }
}

internal sealed class IdlePeaceRule : IMacroRule
{
    private readonly IPluginHost _host;
    private readonly CombatSettings _settings;
    private bool _running;

    public IdlePeaceRule(IPluginHost host, CombatSettings settings)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string Name => "IdlePeace";

    /// <summary>The last request's outcome, surfaced on the panel status line.</summary>
    public string? Status { get; private set; }

    public bool ValidNow(in MacroPassContext context)
    {
        if (!context.CanAct)
            return false;
        if (!_settings.IdlePeaceMode)
            return false;
        PluginCombatMode mode = _host.Automation.Combat.Snapshot.Mode;

        return mode is not (PluginCombatMode.Peace or PluginCombatMode.Unknown);
    }

    public bool Running
    {
        get => _running;
        set
        {
            _running = value;
            if (!value)
            {
                Status = null;
                return;
            }

            PluginCombatCommandResult result =
                _host.Automation.Combat.EnterMode(PluginCombatMode.Peace);
            Status = result.Status == PluginCombatCommandStatus.Refused
                ? result.Notice ?? "Cannot enter peace mode"
                : "Entering peace mode";
        }
    }
}
