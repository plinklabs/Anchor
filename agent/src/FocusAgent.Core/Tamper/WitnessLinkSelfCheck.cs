using System.Text.Json;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Logging;
using FocusAgent.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>What <see cref="WitnessLinkSelfCheck"/> found wrong with the link's set-up.</summary>
[Flags]
public enum WitnessLinkProblems
{
    None = 0,

    /// <summary>No HKCU <c>NativeMessagingHosts\net.anchor.witness</c> key, or no <c>(default)</c> value in it.</summary>
    NoKey = 1 << 0,

    /// <summary>The key points at a manifest that isn't there.</summary>
    ManifestMissing = 1 << 1,

    /// <summary>The manifest can't be read, or isn't the JSON object Edge reads.</summary>
    ManifestUnreadable = 1 << 2,

    /// <summary>The manifest names another host than <see cref="WitnessLink.NativeHostName"/>.</summary>
    ManifestWrongName = 1 << 3,

    /// <summary>The exe the manifest's <c>path</c> names isn't there (or it names none).</summary>
    HostExeMissing = 1 << 4,

    /// <summary>The manifest's exe is there, but it isn't the one next to the running agent.</summary>
    HostExeNotThisAgents = 1 << 5,

    /// <summary>No Edge profile has one of Anchor's extensions installed.</summary>
    NoExtensionInstalled = 1 << 6,

    /// <summary>An installed Anchor extension isn't in the manifest's <c>allowed_origins</c>.</summary>
    ExtensionNotAllowed = 1 << 7,

    /// <summary>Edge policy lets only hosts registered for the whole machine start (<c>NativeMessagingUserLevelHosts</c>).</summary>
    UserLevelHostsBlocked = 1 << 8,

    /// <summary>Edge policy blocks the witness host by name or with <c>*</c> (<c>NativeMessagingBlocklist</c>).</summary>
    HostBlocklisted = 1 << 9,
}

/// <summary>
/// Writes to the agent's log why the extension's link to the agent may be down
/// (#561), one line per check, so the next log upload (#446) carries the cause:
/// nobody can look at a student's laptop. Run when a session is joined and no
/// extension is connected (<see cref="WitnessLinkSelfCheckOnJoin"/>):
/// <list type="bullet">
///   <item>the HKCU key Edge finds the witness host by, and its <c>(default)</c> value;</item>
///   <item>the manifest that value points at: whether it is there, parses, and its
///   <c>name</c>, <c>path</c> and <c>allowed_origins</c>;</item>
///   <item>whether the exe at that <c>path</c> is there, and whether it is the one
///   next to the running agent;</item>
///   <item>which of Anchor's extensions is installed in which Edge profile, and
///   whether each is in <c>allowed_origins</c>;</item>
///   <item>Edge's native-messaging policies under HKLM and HKCU.</item>
/// </list>
/// Then it registers the host again (<see cref="WitnessHostRegistrar.TryEnsureRegistered"/>),
/// whose lines say what it had to rewrite: a missing or stale key or manifest is
/// repaired on the spot.
///
/// <para>
/// Only paths (with the student's own folder written as its variable,
/// <see cref="LogPath"/>), extension IDs, profile folder names, yes/no results
/// and error messages: nothing of what the student browses. Never throws.
/// </para>
/// </summary>
public sealed class WitnessLinkSelfCheck
{
    private readonly IWitnessHostStore _store;
    private readonly IEdgeNativeMessagingPolicySource _policies;
    private readonly Func<IReadOnlyList<InstalledExtension>> _findInstalls;
    private readonly string _agentHostExePath;
    private readonly WitnessHostRegistrar _registrar;
    private readonly ILogger<WitnessLinkSelfCheck> _log;

    /// <param name="store">The registry key and the files, as the registrar reads them.</param>
    /// <param name="policies">Edge's native-messaging policies.</param>
    /// <param name="findInstalls">Anchor's extensions installed in Edge's profiles (<see cref="EdgeProfileExtensionProbe.FindInstalls"/>).</param>
    /// <param name="agentHostExePath">The host exe next to the running agent: what the registration points at.</param>
    /// <param name="registrar">Registers the host again once the checks are logged, over the same store.</param>
    public WitnessLinkSelfCheck(
        IWitnessHostStore store,
        IEdgeNativeMessagingPolicySource policies,
        Func<IReadOnlyList<InstalledExtension>> findInstalls,
        string agentHostExePath,
        WitnessHostRegistrar registrar,
        ILogger<WitnessLinkSelfCheck>? log = null)
    {
        _store = store;
        _policies = policies;
        _findInstalls = findInstalls;
        _agentHostExePath = agentHostExePath;
        _registrar = registrar;
        _log = log ?? NullLogger<WitnessLinkSelfCheck>.Instance;
    }

    /// <summary>
    /// Logs every check, then registers the host again. Returns what the checks
    /// found, before the re-registration repaired any of it.
    /// </summary>
    /// <param name="sessionId">The session joined without a link, for the log.</param>
    public WitnessLinkProblems Run(Guid sessionId)
    {
        _log.LogWarning(
            "Witness link self-check: session {SessionId} joined with no extension connected; checking the link's set-up.",
            sessionId);

        var problems = WitnessLinkProblems.None;
        IReadOnlyList<string>? allowedOrigins = null;
        problems |= Step("the host registration", () => CheckRegistration(out allowedOrigins));
        problems |= Step("the installed extensions", () => CheckInstalls(allowedOrigins));
        problems |= Step("Edge's policies", CheckPolicies);

        if (problems == WitnessLinkProblems.None)
            _log.LogInformation("Witness link self-check: the set-up looks right.");
        else
            _log.LogWarning("Witness link self-check found: {Problems}.", problems);

        // Logs what it had to rewrite, or why it couldn't.
        _registrar.TryEnsureRegistered(_agentHostExePath, "self-check");
        return problems;
    }

    private WitnessLinkProblems Step(string what, Func<WitnessLinkProblems> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Witness link self-check: checking {What} failed: {Error}", what, ex.Message);
            return WitnessLinkProblems.None;
        }
    }

    private WitnessLinkProblems CheckRegistration(out IReadOnlyList<string>? allowedOrigins)
    {
        allowedOrigins = null;
        var key = $@"HKCU\{WitnessHostManifest.RegistryKeyPath}";
        var manifestPath = _store.GetRegisteredManifestPath()?.Trim();
        if (string.IsNullOrEmpty(manifestPath))
        {
            _log.LogWarning("Witness link self-check: key {Key}: missing, or without a (default) value.", key);
            return WitnessLinkProblems.NoKey;
        }
        _log.LogInformation("Witness link self-check: key {Key}: (default) = {ManifestPath}.", key, LogPath.Redact(manifestPath));

        if (!_store.FileExists(manifestPath))
        {
            _log.LogWarning("Witness link self-check: manifest {ManifestPath} doesn't exist.", LogPath.Redact(manifestPath));
            return WitnessLinkProblems.ManifestMissing;
        }

        var text = _store.ReadFile(manifestPath);
        if (text is null)
        {
            _log.LogWarning("Witness link self-check: manifest {ManifestPath} exists but can't be read.", LogPath.Redact(manifestPath));
            return WitnessLinkProblems.ManifestUnreadable;
        }

        string? name, exePath;
        List<string> origins;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException($"the manifest is a JSON {root.ValueKind}, not an object");
            name = StringOf(root, "name");
            exePath = StringOf(root, "path");
            origins = root.TryGetProperty("allowed_origins", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString()!).ToList()
                : [];
        }
        catch (JsonException ex)
        {
            _log.LogWarning("Witness link self-check: manifest {ManifestPath} doesn't parse: {Error}", LogPath.Redact(manifestPath), ex.Message);
            return WitnessLinkProblems.ManifestUnreadable;
        }
        allowedOrigins = origins;

        _log.LogInformation(
            "Witness link self-check: manifest {ManifestPath} parses: name {Name}, path {HostExe}, allowed_origins [{AllowedOrigins}].",
            LogPath.Redact(manifestPath), name ?? "(none)", LogPath.Redact(exePath) is { Length: > 0 } p ? p : "(none)",
            string.Join(", ", origins));

        var problems = WitnessLinkProblems.None;
        if (!string.Equals(name, WitnessLink.NativeHostName, StringComparison.Ordinal))
        {
            _log.LogWarning("Witness link self-check: the manifest names host {Name}, not {HostName}.", name ?? "(none)", WitnessLink.NativeHostName);
            problems |= WitnessLinkProblems.ManifestWrongName;
        }

        var exists = !string.IsNullOrWhiteSpace(exePath) && _store.FileExists(exePath);
        var thisAgents = !string.IsNullOrWhiteSpace(exePath) && SamePath(exePath, _agentHostExePath);
        if (exists)
        {
            _log.LogInformation(
                "Witness link self-check: host exe {HostExe} exists; the one next to this agent ({AgentHostExe}): {ThisAgents}.",
                LogPath.Redact(exePath), LogPath.Redact(_agentHostExePath), thisAgents ? "yes" : "no");
        }
        else
        {
            _log.LogWarning(
                "Witness link self-check: host exe {HostExe} doesn't exist; the one next to this agent is {AgentHostExe}.",
                LogPath.Redact(exePath) is { Length: > 0 } missing ? missing : "(none)", LogPath.Redact(_agentHostExePath));
            problems |= WitnessLinkProblems.HostExeMissing;
        }
        if (exists && !thisAgents)
            problems |= WitnessLinkProblems.HostExeNotThisAgents;
        return problems;
    }

    private WitnessLinkProblems CheckInstalls(IReadOnlyList<string>? allowedOrigins)
    {
        var installs = _findInstalls();
        if (installs.Count == 0)
        {
            _log.LogWarning("Witness link self-check: no Edge profile has one of Anchor's extensions installed.");
            return WitnessLinkProblems.NoExtensionInstalled;
        }

        var problems = WitnessLinkProblems.None;
        foreach (var install in installs)
        {
            var origin = $"chrome-extension://{install.ExtensionId}/";
            // Without a manifest that parses there is nothing to hold the ID against.
            if (allowedOrigins is null)
            {
                _log.LogInformation(
                    "Witness link self-check: extension {ExtensionId} is installed in Edge profile {Profile}.",
                    install.ExtensionId, install.Profile);
                continue;
            }
            var allowed = allowedOrigins.Any(o => string.Equals(o.Trim(), origin, StringComparison.OrdinalIgnoreCase));
            if (allowed)
            {
                _log.LogInformation(
                    "Witness link self-check: extension {ExtensionId} is installed in Edge profile {Profile}, and in allowed_origins.",
                    install.ExtensionId, install.Profile);
            }
            else
            {
                _log.LogWarning(
                    "Witness link self-check: extension {ExtensionId} is installed in Edge profile {Profile}, but not in allowed_origins.",
                    install.ExtensionId, install.Profile);
                problems |= WitnessLinkProblems.ExtensionNotAllowed;
            }
        }
        return problems;
    }

    private WitnessLinkProblems CheckPolicies()
    {
        var machine = _policies.Read(machine: true);
        var user = _policies.Read(machine: false);
        LogPolicy("HKLM", machine);
        LogPolicy("HKCU", user);

        var effective = EdgeNativeMessagingPolicy.Effective(machine, user);
        var problems = WitnessLinkProblems.None;
        if (effective.BlocksUserLevelHosts)
        {
            _log.LogWarning(
                "Witness link self-check: Edge policy NativeMessagingUserLevelHosts lets only machine-wide hosts start; the witness host is registered per user (HKCU).");
            problems |= WitnessLinkProblems.UserLevelHostsBlocked;
        }
        if (effective.Blocks(WitnessLink.NativeHostName))
        {
            _log.LogWarning(
                "Witness link self-check: Edge policy NativeMessagingBlocklist blocks {HostName}, and NativeMessagingAllowlist doesn't exempt it.",
                WitnessLink.NativeHostName);
            problems |= WitnessLinkProblems.HostBlocklisted;
        }
        return problems;
    }

    private void LogPolicy(string hive, EdgeNativeMessagingPolicy policy) =>
        _log.LogInformation(
            "Witness link self-check: Edge policy ({Hive}): NativeMessagingUserLevelHosts {UserLevelHosts}, NativeMessagingBlocklist {Blocklist}, NativeMessagingAllowlist {Allowlist}.",
            hive,
            policy.UserLevelHosts switch { null => "not set", true => "1", false => "0" },
            List(policy.Blocklist),
            List(policy.Allowlist));

    private static string List(IReadOnlyList<string>? values) =>
        values is null ? "not set" : $"[{string.Join(", ", values)}]";

    private static string? StringOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim()), Path.GetFullPath(b.Trim()), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// Runs the <see cref="WitnessLinkSelfCheck"/> when a session is joined with no
/// extension connected (#561), once per session, off the thread that raised the
/// join. When the link is up it logs that instead, so the log says either way.
/// The log upload's window opens 30 minutes before a session, which usually
/// misses the agent's start, so this doesn't rely on the startup registration's
/// lines.
/// </summary>
public sealed class WitnessLinkSelfCheckOnJoin : IDisposable
{
    private readonly SessionCoordinator _sessions;
    private readonly ExtensionWitnessMonitor _witness;
    private readonly WitnessLinkSelfCheck _check;
    private readonly Func<Action, Task> _run;
    private readonly ILogger<WitnessLinkSelfCheckOnJoin> _log;
    private readonly object _gate = new();
    private Guid? _checked;

    /// <param name="run">Runs the check; the thread pool by default.</param>
    public WitnessLinkSelfCheckOnJoin(
        SessionCoordinator sessions,
        ExtensionWitnessMonitor witness,
        WitnessLinkSelfCheck check,
        ILogger<WitnessLinkSelfCheckOnJoin>? log = null,
        Func<Action, Task>? run = null)
    {
        _sessions = sessions;
        _witness = witness;
        _check = check;
        _run = run ?? (action => Task.Run(action));
        _log = log ?? NullLogger<WitnessLinkSelfCheckOnJoin>.Instance;
        _sessions.SessionJoined += OnSessionJoined;
    }

    /// <summary>The last check started, for tests; completed when none has.</summary>
    public Task LastCheck { get; private set; } = Task.CompletedTask;

    private void OnSessionJoined(object? sender, SessionStartedPayload payload)
    {
        if (_witness.IsConnected)
        {
            _log.LogInformation(
                "Session {SessionId} joined with the extension connected ({Connections} link(s)).",
                payload.SessionId, _witness.Connections.Count);
            return;
        }
        lock (_gate)
        {
            if (_checked == payload.SessionId) return;
            _checked = payload.SessionId;
        }
        LastCheck = _run(() => _check.Run(payload.SessionId));
    }

    public void Dispose() => _sessions.SessionJoined -= OnSessionJoined;
}
