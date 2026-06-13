namespace FocusAgent.Core.Tamper;

/// <summary>
/// Names shared across the witness link (#146 part 1) so the agent, the native
/// host, the extension, and the registration script can't drift apart:
///
///   extension --connectNative(<see cref="NativeHostName"/>)--> browser
///        --launches--> anchor-witness-host.exe
///        --NamedPipeClient(<see cref="PipeName"/>)--> FocusAgent (pipe server)
///
/// The extension repeats <see cref="NativeHostName"/> in witness.ts (a separate
/// language); the host manifest's <c>name</c> and the HKCU key must match it too.
/// </summary>
public static class WitnessLink
{
    /// <summary>
    /// Reverse-DNS native-messaging host name. Must equal the host manifest's
    /// <c>name</c>, the HKCU <c>…\NativeMessagingHosts\&lt;name&gt;</c> key, and
    /// <c>WITNESS_HOST_NAME</c> in the extension's witness.ts.
    /// </summary>
    public const string NativeHostName = "net.anchor.witness";

    /// <summary>
    /// Local named pipe the host connects to and the agent serves. Not a security
    /// boundary — it's per-machine and the witness only reports, never enforces.
    /// </summary>
    public const string PipeName = "anchor-witness";
}
