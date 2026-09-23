using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace RIoT2.Net.Node.Tests;

internal sealed class SimulatedEasyPlc : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<PlcExchange> _requests = Channel.CreateUnbounded<PlcExchange>();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly List<Connection> _connections = [];
    private readonly object _disposeGate = new();
    private readonly Task _acceptTask;
    private Task _disposeTask;
    private int _connectionCount;
    private int _activeConnections;

    public SimulatedEasyPlc()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptAsync();
    }

    public int Port { get; }
    public int ConnectionCount => Volatile.Read(ref _connectionCount);
    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public ChannelReader<PlcExchange> Requests => _requests.Reader;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var connection = new Connection(this, client, Interlocked.Increment(ref _connectionCount));
                _connections.Add(connection);
                Interlocked.Increment(ref _activeConnections);
                connection.RunTask = connection.RunAsync();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        _errors.Enqueue(error);
        _requests.Writer.TryComplete(error);
        _shutdown.Cancel();
        _listener.Stop();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        await _acceptTask.ConfigureAwait(false);
        // The accept loop is the only writer, and has now finished adding connections.
        foreach (var connection in _connections)
            connection.Close();
        await Task.WhenAll(_connections.Select(connection => connection.RunTask)).ConfigureAwait(false);
        foreach (var connection in _connections)
            connection.DisposeCancellation();
        _shutdown.Dispose();
        _requests.Writer.TryComplete();
        if (!_errors.IsEmpty)
            throw new AggregateException("EasyPLC simulator failed.", _errors);
    }

    internal static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xa001);
        }
        return crc;
    }

    private sealed class Connection
    {
        private readonly SimulatedEasyPlc _owner;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly CancellationTokenSource _closed;
        private readonly int _id;
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isClosed;

        public Connection(SimulatedEasyPlc owner, TcpClient client, int id)
        {
            _owner = owner;
            _client = client;
            _stream = client.GetStream();
            _id = id;
            _closed = CancellationTokenSource.CreateLinkedTokenSource(owner._shutdown.Token);
        }

        public Task RunTask { get; set; } = Task.CompletedTask;

        public async Task RunAsync()
        {
            // Receiving never waits for a test's reply: EOF cancels an unanswered exchange.
            var receiving = ReceiveAsync();
            try
            {
                for (byte step = 0; step < 2; step++)
                {
                    var handshake = await _frames.Reader.ReadAsync(_closed.Token).ConfigureAwait(false);
                    byte[] expected = step == 0
                        ? [0x45, 0x07, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x5f]
                        : [0x45, 0x07, 0x01, 0x00, 0x80, 0x00, 0x00, 0x3d, 0x9f];
                    if (!handshake.AsSpan().SequenceEqual(expected))
                        throw new InvalidDataException($"Connection {_id}: invalid handshake {step + 1}.");
                    await SendAsync([0x65, 0x06, 0x00, 0x00, 0x00, 0x41, 0x48, 0x30]).ConfigureAwait(false);
                }

                while (!_closed.IsCancellationRequested)
                {
                    var frame = await _frames.Reader.ReadAsync(_closed.Token).ConfigureAwait(false);
                    ValidateTransaction(frame);
                    var exchange = new PlcExchange(frame, _id, _disconnected.Task, Close);
                    if (!_owner._requests.Writer.TryWrite(exchange))
                    {
                        _closed.Token.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("The simulator request channel is closed.");
                    }
                    var response = await exchange.Response.WaitAsync(_closed.Token).ConfigureAwait(false);
                    await SendAsync(response).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (IsExpectedDisconnect(error)) { }
            catch (Exception error) { _owner.Fail(error); }
            finally
            {
                Close();
                await receiving.ConfigureAwait(false);
            }
        }

        private async Task ReceiveAsync()
        {
            try
            {
                while (!_closed.IsCancellationRequested)
                {
                    var header = new byte[2];
                    await _stream.ReadExactlyAsync(header, _closed.Token).ConfigureAwait(false);
                    if (header[0] != 0x45 || header[1] < 5)
                        throw new InvalidDataException($"Connection {_id}: invalid request header.");
                    var frame = new byte[header[1] + 2];
                    header.CopyTo(frame, 0);
                    await _stream.ReadExactlyAsync(frame.AsMemory(2), _closed.Token).ConfigureAwait(false);
                    var crc = Crc(frame.AsSpan(1, frame.Length - 3));
                    if (frame[^2] != (byte)crc || frame[^1] != (byte)(crc >> 8))
                        throw new InvalidDataException($"Connection {_id}: invalid request CRC.");
                    if (!_frames.Writer.TryWrite(frame))
                        throw new InvalidOperationException("The connection frame channel is closed.");
                }
            }
            catch (Exception error) when (IsExpectedDisconnect(error)) { }
            catch (Exception error) { _owner.Fail(error); }
            finally
            {
                Close();
                _frames.Writer.TryComplete();
            }
        }

        private void ValidateTransaction(byte[] frame)
        {
            var valid = frame[4] switch
            {
                0x0a => frame.Length == 9 && frame[2] == 1 && frame[3] == 0 &&
                    frame[5] == 0 && frame[6] == 6,
                0x04 => frame.Length == 10 && frame[2] >= 32 && frame[3] == 0 &&
                    frame[6] == 0 && frame[7] <= 1,
                _ => false
            };
            if (!valid)
                throw new InvalidDataException($"Connection {_id}: invalid transaction request.");
        }

        private async Task SendAsync(byte[] frame)
        {
            for (var index = 0; index < frame.Length; index++)
                await _stream.WriteAsync(frame.AsMemory(index, 1), _closed.Token).ConfigureAwait(false);
        }

        private bool IsExpectedDisconnect(Exception error) =>
            error is EndOfStreamException ||
            (_closed.IsCancellationRequested &&
                error is OperationCanceledException or ObjectDisposedException or ChannelClosedException) ||
            error is SocketException socket && IsDisconnectSocketError(socket.SocketErrorCode) ||
            error is IOException { InnerException: SocketException inner } && IsDisconnectSocketError(inner.SocketErrorCode);

        private static bool IsDisconnectSocketError(SocketError error) =>
            error is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown
                or SocketError.OperationAborted or SocketError.NotConnected;

        public void Close()
        {
            if (Interlocked.Exchange(ref _isClosed, 1) != 0)
                return;
            _closed.Cancel();
            _client.Dispose();
            Interlocked.Decrement(ref _owner._activeConnections);
            _disconnected.TrySetResult();
        }

        public void DisposeCancellation() => _closed.Dispose();
    }
}

internal sealed class PlcExchange
{
    private readonly TaskCompletionSource<byte[]> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _disconnect;

    internal PlcExchange(byte[] frame, int connectionId, Task disconnected, Action disconnect)
    {
        Frame = frame;
        ConnectionId = connectionId;
        Disconnected = disconnected;
        _disconnect = disconnect;
    }

    public byte[] Frame { get; }
    public int ConnectionId { get; }
    public bool IsRead => Frame[4] == 0x0a;
    public Task Disconnected { get; }
    internal Task<byte[]> Response => _response.Task;

    public void Reply(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_response.TrySetResult((byte[])frame.Clone()))
            throw new InvalidOperationException("This exchange already has a reply.");
    }

    public void Disconnect() => _disconnect();

    public void ReplyWithMarkers(bool value)
    {
        if (!IsRead)
            throw new InvalidOperationException("Marker data requires a read request.");
        // Two status bytes precede the 256 marker bits (least significant bit first).
        var frame = new byte[38];
        frame[0] = 0x65;
        frame[1] = 36;
        frame.AsSpan(4, 32).Fill(value ? (byte)0xff : (byte)0x00);
        var crc = SimulatedEasyPlc.Crc(frame.AsSpan(1, frame.Length - 3));
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        Reply(frame);
    }

    public void ReplyWithWriteAck()
    {
        if (IsRead)
            throw new InvalidOperationException("A write acknowledgement requires a write request.");
        Reply([0x65, 0x05, 0x00, 0x00, 0x00, 0x00, 0xcc]);
    }
}
