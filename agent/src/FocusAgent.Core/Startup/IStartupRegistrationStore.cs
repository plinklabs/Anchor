namespace FocusAgent.Core.Startup;

/// <summary>
/// The agent's read/write access to its per-user "start at login" entry under
/// <c>HKCU\...\Run</c> (#225). Abstracted off the concrete
/// <c>Microsoft.Win32.Registry</c> write so the orchestration in
/// <see cref="StartupRegistrar"/> — its present/update/remove decisions — is
/// unit-testable with an in-memory fake, while the real registry path is driven
/// end-to-end by the integration test against a throwaway HKCU subtree.
/// </summary>
public interface IStartupRegistrationStore
{
    /// <summary>
    /// The command currently registered under Anchor's Run value, or null when no
    /// such value exists yet.
    /// </summary>
    string? GetRegisteredCommand();

    /// <summary>
    /// Write (or overwrite) Anchor's Run value with <paramref name="command"/>,
    /// creating the Run key if needed.
    /// </summary>
    void SetRegisteredCommand(string command);

    /// <summary>
    /// Remove Anchor's Run value. No-op when the key or the value is absent — never
    /// touches any other product's Run entries.
    /// </summary>
    void RemoveRegistration();
}
