using System.Linq;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.Afk;
using Content.Server.Afk.Events;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Server.Preferences.Managers;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Players;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Roles;
using Content.Shared.Preferences;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Players.PlayTimeTracking;

/// <summary>
/// Connects <see cref="PlayTimeTrackingManager"/> to the simulation state. Reports trackers and such.
/// </summary>
public sealed class PlayTimeTrackingSystem : EntitySystem
{
    [Dependency] private readonly IAfkManager _afk = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly MindSystem _minds = default!;
    [Dependency] private readonly PlayTimeTrackingManager _tracking = default!;
    [Dependency] private readonly IAdminManager _adminManager = default!;
    [Dependency] private readonly IServerPreferencesManager _preferencesManager = default!;

    public override void Initialize()
    {
        base.Initialize();

        _tracking.CalcTrackers += CalcTrackers;

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundEnd);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<RoleAddedEvent>(OnRoleAdd);
        SubscribeLocalEvent<RoleRemovedEvent>(OnRoleRemove);
        SubscribeLocalEvent<AFKEvent>(OnAFK);
        SubscribeLocalEvent<UnAFKEvent>(OnUnAFK);
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnPlayerJoinedLobby);
        _adminManager.OnPermsChanged += AdminPermsChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _tracking.CalcTrackers -= CalcTrackers;
        _adminManager.OnPermsChanged -= AdminPermsChanged;
    }

    private void CalcTrackers(ICommonSession player, HashSet<string> trackers)
    {
        if (_afk.IsAfk(player))
            return;

        if (_adminManager.IsAdmin(player))
        {
            trackers.Add(PlayTimeTrackingShared.TrackerAdmin);
            trackers.Add(PlayTimeTrackingShared.TrackerOverall);
            return;
        }

        if (!IsPlayerAlive(player))
            return;

        trackers.Add(PlayTimeTrackingShared.TrackerOverall);
        trackers.UnionWith(GetTimedRoles(player));
    }

    private bool IsPlayerAlive(ICommonSession session)
    {
        var attached = session.AttachedEntity;
        if (attached == null)
            return false;

        if (!TryComp<MobStateComponent>(attached, out var state))
            return false;

        return state.CurrentState is MobState.Alive or MobState.Critical;
    }

    public IEnumerable<string> GetTimedRoles(EntityUid mindId)
    {
        var ev = new MindGetAllRolesEvent(new List<RoleInfo>());
        RaiseLocalEvent(mindId, ref ev);

        foreach (var role in ev.Roles)
        {
            if (string.IsNullOrWhiteSpace(role.PlayTimeTrackerId))
                continue;

            yield return _prototypes.Index<PlayTimeTrackerPrototype>(role.PlayTimeTrackerId).ID;
        }
    }

    private IEnumerable<string> GetTimedRoles(ICommonSession session)
    {
        var contentData = _playerManager.GetPlayerData(session.UserId).ContentData();

        if (contentData?.Mind == null)
            return Enumerable.Empty<string>();

        return GetTimedRoles(contentData.Mind.Value);
    }

    private void OnRoleRemove(RoleRemovedEvent ev)
    {
        if (_minds.TryGetSession(ev.Mind, out var session))
            _tracking.QueueRefreshTrackers(session);
    }

    private void OnRoleAdd(RoleAddedEvent ev)
    {
        if (_minds.TryGetSession(ev.Mind, out var session))
            _tracking.QueueRefreshTrackers(session);
    }

    private void OnRoundEnd(RoundRestartCleanupEvent ev)
    {
        _tracking.Save();
    }

    private void OnUnAFK(ref UnAFKEvent ev)
    {
        _tracking.QueueRefreshTrackers(ev.Session);
    }

    private void OnAFK(ref AFKEvent ev)
    {
        _tracking.QueueRefreshTrackers(ev.Session);
    }

    private void AdminPermsChanged(AdminPermsChangedEventArgs admin)
    {
        _tracking.QueueRefreshTrackers(admin.Player);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        _tracking.QueueRefreshTrackers(ev.Player);
    }

    private void OnPlayerDetached(PlayerDetachedEvent ev)
    {
        // This doesn't fire if the player doesn't leave their body. I guess it's fine?
        _tracking.QueueRefreshTrackers(ev.Player);
    }

    private void OnMobStateChanged(MobStateChangedEvent ev)
    {
        if (!TryComp(ev.Target, out ActorComponent? actor))
            return;

        _tracking.QueueRefreshTrackers(actor.PlayerSession);
    }

    private void OnPlayerJoinedLobby(PlayerJoinedLobbyEvent ev)
    {
        _tracking.QueueRefreshTrackers(ev.PlayerSession);
        // Send timers to client when they join lobby, so the UIs are up-to-date.
        _tracking.QueueSendTimers(ev.PlayerSession);
        _tracking.QueueSendWhitelist(ev.PlayerSession); // Nyanotrasen - Send whitelist
    }

    public bool IsAllowed(ICommonSession player, string role)
    {
        if (!_prototypes.TryIndex<JobPrototype>(role, out var job) ||
            job.Requirements == null)
            return true;

        Dictionary<string, TimeSpan>? playTimes = null;
        if (_cfg.GetCVar(CCVars.GameRoleTimers))
        {
            if (!_tracking.TryGetTrackerTimes(player, out playTimes))
            {
                Log.Error($"Unable to check playtimes {Environment.StackTrace}");
                playTimes = new Dictionary<string, TimeSpan>();
            }
        }

        var isWhitelisted = player.ContentData()?.Whitelisted ?? false; // DeltaV - Whitelist requirement

        string species;
        if (_preferencesManager.GetPreferences(player.UserId).SelectedCharacter is HumanoidCharacterProfile selectedCharacter)
        {
            species = selectedCharacter.Species;
        }
        else
        {
            species = string.Empty;
        }

        return JobRequirements.TryRequirementsMet(job, playTimes, out _, EntityManager, _prototypes, isWhitelisted, species);
    }

    public HashSet<string> GetDisallowedJobs(ICommonSession player)
    {
        var roles = new HashSet<string>();

        Dictionary<string, TimeSpan>? playTimes = null;
        if (_cfg.GetCVar(CCVars.GameRoleTimers))
        {
            if (!_tracking.TryGetTrackerTimes(player, out playTimes))
            {
                Log.Error($"Unable to check playtimes {Environment.StackTrace}");
                playTimes = new Dictionary<string, TimeSpan>();
            }
        }

        var isWhitelisted = player.ContentData()?.Whitelisted ?? false; // DeltaV - Whitelist requirement

        string species;
        if (_preferencesManager.GetPreferences(player.UserId).SelectedCharacter is HumanoidCharacterProfile selectedCharacter)
        {
            species = selectedCharacter.Species;
        }
        else
        {
            species = string.Empty;
        }

        foreach (var job in _prototypes.EnumeratePrototypes<JobPrototype>())
        {
            if (job.Requirements != null)
            {
                foreach (var requirement in job.Requirements)
                {
                    if (JobRequirements.TryRequirementMet(requirement, playTimes, out _, EntityManager, _prototypes, isWhitelisted, species))
                        continue;

                    goto NoRole;
                }
            }

            roles.Add(job.ID);
            NoRole:;
        }

        return roles;
    }

    public void RemoveDisallowedJobs(NetUserId userId, ref List<string> jobs)
    {
        var player = _playerManager.GetSessionById(userId);

        Dictionary<string, TimeSpan>? playTimes = null;
        if (_cfg.GetCVar(CCVars.GameRoleTimers))
        {
            if (!_tracking.TryGetTrackerTimes(player, out playTimes))
            {
                Log.Error($"Unable to check playtimes {Environment.StackTrace}");
                playTimes = new Dictionary<string, TimeSpan>();
            }
        }

        var isWhitelisted = player.ContentData()?.Whitelisted ?? false; // DeltaV - Whitelist requirement

        string species;
        if (_preferencesManager.GetPreferences(player.UserId).SelectedCharacter is HumanoidCharacterProfile selectedCharacter)
        {
            species = selectedCharacter.Species;
        }
        else
        {
            species = string.Empty;
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];

            if (!_prototypes.TryIndex<JobPrototype>(job, out var jobber) ||
                jobber.Requirements == null ||
                jobber.Requirements.Count == 0)
                continue;

            foreach (var requirement in jobber.Requirements)
            {
                if (JobRequirements.TryRequirementMet(requirement, playTimes, out _, EntityManager, _prototypes, isWhitelisted, species))
                    continue;

                jobs.RemoveSwap(i);
                i--;
                break;
            }
        }
    }

    public void PlayerRolesChanged(ICommonSession player)
    {
        _tracking.QueueRefreshTrackers(player);
    }
}
