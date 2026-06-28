namespace FocusAgent.Core.Tamper;

/// <summary>
/// The agent's read/write access to the witness host registration (#288): the HKCU
/// <c>NativeMessagingHosts</c> key Edge reads, plus the manifest / backend-url files
/// next to the host exe. Abstracted off the concrete <c>Microsoft.Win32.Registry</c>
/// and filesystem writes so the orchestration in <see cref="WitnessHostRegistrar"/>
/// — its idempotency decisions — is unit-testable with an in-memory fake, while the
/// real registry + file paths are driven end-to-end by the integration test against
/// a throwaway HKCU subtree and a throwaway directory.
/// </summary>
public interface IWitnessHostStore
{
    /// <summary>
    /// The manifest path currently recorded under the host's HKCU key
    /// (<c>(default)</c> value), or null when the key doesn't exist yet.
    /// </summary>
    string? GetRegisteredManifestPath();

    /// <summary>
    /// Write (or overwrite) the host's HKCU <c>(default)</c> value with
    /// <paramref name="manifestPath"/>, creating the key if needed.
    /// </summary>
    void SetRegisteredManifestPath(string manifestPath);

    /// <summary>
    /// Remove the host's HKCU key. No-op when absent. The key is exclusively Anchor's
    /// (named after the reverse-DNS host), so removing it touches nothing else.
    /// </summary>
    void RemoveRegistration();

    /// <summary>Read a file's full text, or null when it's absent or unreadable.</summary>
    string? ReadFile(string path);

    /// <summary>Write <paramref name="content"/> to <paramref name="path"/>, creating the directory if needed.</summary>
    void WriteFile(string path, string content);
}
