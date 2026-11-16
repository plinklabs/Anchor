namespace FocusAgent.Core.Tamper;

/// <summary>
/// One extension's link to the agent (#517). Every Edge profile runs its own
/// copy of the extension, and each copy launches its own native host, so the
/// agent holds one of these per profile that has Edge open with the extension
/// on. The same instance is passed to
/// <see cref="IExtensionWitnessTransport.WitnessConnected"/>, to every
/// <see cref="WitnessMessage"/> from that extension and to
/// <see cref="IExtensionWitnessTransport.WitnessDisconnected"/>, and it is what
/// <see cref="IExtensionWitnessChannel.TrySend"/> addresses.
///
/// A class rather than a bare id so that what the agent learns about a
/// connection later has a place to live. It does not know which profile a
/// connection belongs to, and doesn't need to: whether a profile is open with
/// the extension off is read from Edge itself (#518,
/// <see cref="EdgeOpenProfileExtensionProbe"/>).
/// </summary>
public sealed class WitnessConnection
{
    public WitnessConnection(long id) => Id = id;

    /// <summary>
    /// Unique among the connections this transport has accepted since the agent
    /// started; never reused. A profile that reconnects gets a new one.
    /// </summary>
    public long Id { get; }

    public override string ToString() => $"#{Id}";
}

/// <summary>One line an extension sent, and the connection it came in on.</summary>
public readonly record struct WitnessMessage(WitnessConnection Connection, string Line);
