using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// A loopback TCP relay between the agent and the e2e backend that a spec can
/// cut and restore: the agent's network dropping out, or its laptop going to
/// sleep, while the backend runs on (#354). Launch the agent against
/// <see cref="Url"/> and keep driving the backend directly from the spec.
///
/// Restarting the backend (<see cref="BackendFixture.StopAsync"/>) also drops
/// the agent's connection, but nothing can happen on the backend while it is
/// down. A cut leaves the backend up, so it can end a session while the agent
/// can't hear about it.
/// </summary>
internal sealed class NetworkRelay : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _backendPort;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, Link> _links = new();
    private readonly Task _acceptLoop;
    private int _nextLinkId;
    private volatile bool _cut;

    public NetworkRelay(string backendUrl)
    {
        _backendPort = new Uri(backendUrl).Port;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>The backend URL to hand the agent.</summary>
    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>
    /// Resets every open connection and refuses new ones until
    /// <see cref="Restore"/>. The agent's hub connection fails and it keeps
    /// retrying, as on a dead network.
    /// </summary>
    public void Cut()
    {
        _cut = true;
        foreach (var id in _links.Keys)
        {
            if (_links.TryRemove(id, out var link))
                link.Reset();
        }
    }

    /// <summary>Lets connections through again.</summary>
    public void Restore() => _cut = false;

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch
            {
                return; // disposed
            }
            _ = RelayAsync(client);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        if (_cut)
        {
            Link.Reset(client);
            return;
        }

        var backend = new TcpClient();
        try
        {
            await backend.ConnectAsync(IPAddress.Loopback, _backendPort, _shutdown.Token);
        }
        catch
        {
            Link.Reset(client);
            backend.Dispose();
            return;
        }

        var link = new Link(client, backend);
        var id = Interlocked.Increment(ref _nextLinkId);
        _links[id] = link;
        // A Cut that ran between the check above and the registration missed
        // this link, so drop it here.
        if (_cut && _links.TryRemove(id, out _))
        {
            link.Reset();
            return;
        }

        try
        {
            var agentSide = client.GetStream();
            var backendSide = backend.GetStream();
            // Either side closing ends the link: forward both ways until then.
            await Task.WhenAny(
                agentSide.CopyToAsync(backendSide, _shutdown.Token),
                backendSide.CopyToAsync(agentSide, _shutdown.Token));
        }
        catch
        {
            // reset by Cut, or by either end
        }
        finally
        {
            _links.TryRemove(id, out _);
            link.Reset();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        Cut();
        try { await _acceptLoop; } catch { }
        _shutdown.Dispose();
    }

    private sealed class Link
    {
        private readonly TcpClient _agent;
        private readonly TcpClient _backend;

        public Link(TcpClient agent, TcpClient backend)
        {
            _agent = agent;
            _backend = backend;
        }

        public void Reset()
        {
            Reset(_agent);
            Reset(_backend);
        }

        /// <summary>Close with a TCP reset rather than a graceful FIN, as a lost network would.</summary>
        public static void Reset(TcpClient client)
        {
            try
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Client.Close();
            }
            catch
            {
                // already closed
            }
            client.Dispose();
        }
    }
}
