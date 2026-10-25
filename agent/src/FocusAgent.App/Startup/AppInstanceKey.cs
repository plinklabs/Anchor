using FocusAgent.Core.Startup;
using Microsoft.Windows.AppLifecycle;

namespace FocusAgent.App.Startup;

/// <summary>
/// The agent's single-instance key on WinAppSDK's <see cref="AppInstance"/> (#461),
/// claimed through <see cref="SingleInstanceClaim"/> in <c>Program.Main</c>.
/// </summary>
internal sealed class AppInstanceKey : ISingleInstanceKey
{
    private readonly string _key;

    public AppInstanceKey(string key) => _key = key;

    public uint? RegisterOrFindHolder()
    {
        var instance = AppInstance.FindOrRegisterForKey(_key);
        // Null when the key is held but no instance in WinAppSDK's list has it, as when
        // the holder finished exiting in between.
        if (instance is null) return 0;
        return instance.IsCurrent ? null : instance.ProcessId;
    }
}
