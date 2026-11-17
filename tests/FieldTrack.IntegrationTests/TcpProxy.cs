using System.Net;
using System.Net.Sockets;

namespace FieldTrack.IntegrationTests;

/// <summary>
/// A TCP relay in front of the test broker that tests can cut: <see cref="SeverConnections"/> drops every client the way a
/// network fault would, and a proxy not yet <see cref="Start"/>ed is a broker that is down.
/// </summary>
internal sealed class TcpProxy : IDisposable
{
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly List<TcpClient> _connections = [];
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    /// <summary>A proxy to <paramref name="targetHost"/>:<paramref name="targetPort"/> on a free local port, which nothing listens on until <see cref="Start"/>.</summary>
    public TcpProxy(string targetHost, int targetPort)
    {
        (_targetHost, _targetPort) = (targetHost, targetPort);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
    }

    /// <summary>The port clients connect to.</summary>
    public int Port { get; }

    /// <summary>Starts accepting connections.</summary>
    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _ = AcceptAsync(_listener);
    }

    /// <summary>Closes every connection through the proxy; new connections are still accepted.</summary>
    public void SeverConnections()
    {
        lock (_connections)
        {
            foreach (var connection in _connections)
            {
                connection.Close();
            }

            _connections.Clear();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
        SeverConnections();
        _stop.Dispose();
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_stop.Token);
                var upstream = new TcpClient();
                await upstream.ConnectAsync(_targetHost, _targetPort, _stop.Token);
                lock (_connections)
                {
                    _connections.Add(client);
                    _connections.Add(upstream);
                }

                _ = RelayAsync(client, upstream);
                _ = RelayAsync(upstream, client);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Disposed, or the test broker went away: nothing left to relay.
        }
    }

    private static async Task RelayAsync(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream());
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The other side was severed.
        }
        finally
        {
            from.Close();
            to.Close();
        }
    }
}
