using System.Text.Json;

namespace FocusAgent.Core.Extension;

/// <summary>
/// What the agent remembers between runs about the Edge policy setup (#405).
/// </summary>
public sealed record ExtensionPolicyState
{
    /// <summary>
    /// The student said "not now", declined the UAC prompt, or the elevated write
    /// failed. Startup then stops asking on every launch; the tray menu still offers
    /// the setup, and taking it clears this.
    /// </summary>
    public bool PromptDeclined { get; init; }

    /// <summary>When <see cref="PromptDeclined"/> was last set, for the log.</summary>
    public DateTimeOffset? PromptDeclinedAt { get; init; }

    /// <summary>
    /// The HKLM forcelist value name this agent wrote, so uninstall removes only
    /// its own entry and never one that Intune or Group Policy put there.
    /// </summary>
    public string? MachineEntryValueName { get; init; }
}

/// <summary>Loads and saves <see cref="ExtensionPolicyState"/>.</summary>
public interface IExtensionPolicyStateStore
{
    /// <summary>The saved state, or a fresh one when nothing (readable) was saved.</summary>
    ExtensionPolicyState Load();

    /// <summary>Persist <paramref name="state"/>, replacing what was there.</summary>
    void Save(ExtensionPolicyState state);
}

/// <summary>
/// The real <see cref="IExtensionPolicyStateStore"/>: a small JSON file in the
/// student's local app data, next to the agent's logs. It sits outside the
/// Velopack install folder on purpose: the uninstall hook reads it after the app
/// files are queued for removal, and a reinstall keeps the "not now" answer.
/// </summary>
public sealed class FileExtensionPolicyStateStore : IExtensionPolicyStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;

    public FileExtensionPolicyStateStore(string? path = null) => _path = path ?? DefaultPath();

    /// <summary><c>%LOCALAPPDATA%\Anchor\FocusAgent\extension-policy.json</c>.</summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Anchor", "FocusAgent", "extension-policy.json");

    public ExtensionPolicyState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new ExtensionPolicyState();
            return JsonSerializer.Deserialize<ExtensionPolicyState>(File.ReadAllText(_path), Json)
                   ?? new ExtensionPolicyState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged or unreadable file is treated as "nothing remembered": the
            // worst case is that the student is asked once more.
            return new ExtensionPolicyState();
        }
    }

    public void Save(ExtensionPolicyState state)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write-then-move so a crash mid-write never leaves half a file behind.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>
/// An <see cref="IExtensionPolicyStateStore"/> that forgets everything when the
/// process ends. Dev runs use it so a "not now" clicked (or a window closed by a
/// test) never lands in the real state file an installed agent on the same box
/// reads.
/// </summary>
public sealed class InMemoryExtensionPolicyStateStore : IExtensionPolicyStateStore
{
    private ExtensionPolicyState _state = new();

    public ExtensionPolicyState Load() => _state;

    public void Save(ExtensionPolicyState state) => _state = state;
}
