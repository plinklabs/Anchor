using FocusAgent.Core.Diagnostics;
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

// #561: the host's own log, next to the agent's, which the agent's log upload
// carries: whether Edge started it, for which extension, whether it reached the
// agent's pipe, and why it exited. Edge passes the calling extension's origin
// (chrome-extension://<id>/) as the first argument.
var log = new WitnessHostLog(WitnessHostLog.DefaultDirectory());
log.DeleteOldFiles();
var caller = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
log.Info($"Witness host {AgentVersion.Of(typeof(WitnessBridge).Assembly)} started by {caller ?? "(no extension origin)"}; agent pipe {pipeName}.");

try
{
    await using var agent = new NamedPipeAgentLink(pipeName, log);
    var bridge = new WitnessBridge(input, output, agent, log);
    await bridge.RunAsync(cts.Token);
    return 0;
}
catch (Exception ex)
{
    log.Warn($"Witness host failed: {WitnessHostLog.Describe(ex)}");
    throw;
}
