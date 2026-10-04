using Anchor.Api.Realtime;
using Microsoft.AspNetCore.SignalR.Client;

namespace Anchor.Api.Tests;

internal static class HubConnectionTestExtensions
{
    /// <summary>
    /// Starts the connection and returns once the server has finished
    /// <see cref="SessionHub.OnConnectedAsync"/>, which is where the connection
    /// joins its <c>user:{id}</c> group. Use it whenever a test broadcasts to
    /// user groups next. <see cref="HubConnection.StartAsync"/> alone isn't
    /// enough (#351, #355): it completes when the handshake response arrives,
    /// and the server sends that response before it runs
    /// <c>OnConnectedAsync</c>, so a broadcast sent straight after it can find
    /// the group still empty. The server dispatches a connection's invocations
    /// only after <c>OnConnectedAsync</c> has completed, so the reply to any
    /// invocation proves the join happened. <c>LeaveSession</c> for a session
    /// that doesn't exist changes nothing on the server (no participant row, no
    /// broadcast, no group to leave), and it throws if the connection didn't
    /// resolve to a provisioned user.
    /// </summary>
    public static async Task StartAndAwaitOnConnectedAsync(this HubConnection connection)
    {
        await connection.StartAsync();
        await connection.InvokeAsync(nameof(SessionHub.LeaveSession), Guid.NewGuid());
    }
}
