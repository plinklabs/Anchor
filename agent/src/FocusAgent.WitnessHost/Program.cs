using FocusAgent.Core.Tamper;
using FocusAgent.WitnessHost;

// Native-messaging host for the Anchor witness link (#146 part 1). Launched by
// Edge when the extension calls chrome.runtime.connectNative(net.anchor.witness);
// it bridges the browser's stdio to the FocusAgent's named pipe so the agent can
// witness the extension being disabled/removed (stdin EOF → this process exits →
// the pipe closes), and so the extension learns when the agent goes away.
//
// stdout is the native-messaging channel — nothing else may be written to it.

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();

// The pipe name is fixed in production (WitnessLink.PipeName); the env override
// lets the integration test run the real host against a hermetic, unique pipe.
var pipeName = Environment.GetEnvironmentVariable("ANCHOR_WITNESS_PIPE");
if (string.IsNullOrWhiteSpace(pipeName)) pipeName = WitnessLink.PipeName;

// Resolve the backend URL this deployment targets (#204) and hand it to the
// extension over the native-messaging link, so a single published extension
// learns its backend from the on-box agent at runtime instead of baking it in.
var backendUrl = BackendUrlConfig.ResolveFromEnvironment();
var backendUrlMessage = BackendUrlConfig.BuildMessage(backendUrl);

// Resolve this deployment's Entra tenant/client/scope (#289) the same way and
// hand it down too, so the extension can mint a real student token for the hub.
// Null in a dev build (no per-deployment auth configured) — the extension then
// falls back to the dev_impersonate_oid shortcut.
var authConfig = AuthConfig.ResolveFromEnvironment();
var authConfigMessage = authConfig is not null ? AuthConfig.BuildMessage(authConfig) : null;

await using var agent = new NamedPipeAgentLink(pipeName);
var bridge = new WitnessBridge(input, output, agent, backendUrlMessage, authConfigMessage);
await bridge.RunAsync(cts.Token);
return 0;
