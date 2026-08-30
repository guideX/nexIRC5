using System.Net;
using System.Net.Sockets;
using System.Text;
using nexIRC.Core.Networking;
using nexIRC.Networking;

namespace nexIRC.Networking.Tests;

public sealed class TcpTlsIrcTransportTests
{
    [Fact]
    public async Task PlainTcpTransportConnectsReadsWritesAndClosesDeterministically()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync();
            await using var transport = new TcpTlsIrcTransport(new IrcEndpoint("127.0.0.1", port, false), new TcpTlsIrcTransportOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                MaximumReadBytes = 32
            });

            await transport.ConnectAsync(CancellationToken.None);
            using var server = await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));
            await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("PING :server\r\n"));
            var inbound = new byte[64];
            var count = await transport.ReadAsync(inbound, CancellationToken.None);
            Assert.Equal("PING :server\r\n", Encoding.UTF8.GetString(inbound, 0, count));

            await transport.WriteAsync(Encoding.UTF8.GetBytes("PONG :server\r\n"), CancellationToken.None);
            var response = new byte[64];
            var responseCount = await server.GetStream().ReadAsync(response);
            Assert.Equal("PONG :server\r\n", Encoding.UTF8.GetString(response, 0, responseCount));

            await transport.DisconnectAsync(new ConnectionFailure(ConnectionFailureKind.Intentional, "test", IsTransient: false), CancellationToken.None);
            Assert.False(transport.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }
}
