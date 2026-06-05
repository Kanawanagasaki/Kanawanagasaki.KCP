namespace Kanawanagasaki.KCP.Sample;

using System.Net;
using System.Net.Sockets;
using System.Text;
using Kanawanagasaki.KCP;

public class KcpSession
{
    private UdpKcpTransport? _transport;
    private readonly KcpConfig _config;
    private bool _isRunning;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    private long _transmittedBytes;
    private long _receivedBytes;
    private long _transmittedMessages;
    private long _receivedMessages;

    private long _lastSpeedCheckTicks;
    private long _lastTransmittedBytes;
    private long _lastReceivedBytes;
    private double _sendSpeedBytesPerSec;
    private double _receiveSpeedBytesPerSec;
    private readonly Lock _speedLock = new();

    public UdpKcpTransport? Transport => _transport;
    public bool IsRunning => _isRunning;
    public KcpConfig Config => _config;

    public long TransmittedBytes => Volatile.Read(ref _transmittedBytes);
    public long ReceivedBytes => Volatile.Read(ref _receivedBytes);
    public long TransmittedMessages => Volatile.Read(ref _transmittedMessages);
    public long ReceivedMessages => Volatile.Read(ref _receivedMessages);

    public double SendSpeedBytesPerSec
    {
        get { lock (_speedLock) return _sendSpeedBytesPerSec; }
    }
    public double ReceiveSpeedBytesPerSec
    {
        get { lock (_speedLock) return _receiveSpeedBytesPerSec; }
    }

    public event Action<string>? OnLog;

    public KcpSession(KcpConfig config)
    {
        _config = config;
    }

    public void UpdateSpeed()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        lock (_speedLock)
        {
            var elapsedTicks = nowTicks - _lastSpeedCheckTicks;
            if (elapsedTicks <= 0) return;

            var elapsedSec = (double)elapsedTicks / TimeSpan.TicksPerSecond;
            var currentTx = Volatile.Read(ref _transmittedBytes);
            var currentRx = Volatile.Read(ref _receivedBytes);

            _sendSpeedBytesPerSec = (currentTx - _lastTransmittedBytes) / elapsedSec;
            _receiveSpeedBytesPerSec = (currentRx - _lastReceivedBytes) / elapsedSec;

            _lastTransmittedBytes = currentTx;
            _lastReceivedBytes = currentRx;
            _lastSpeedCheckTicks = nowTicks;
        }
    }

    public async Task StartAsync()
    {
        if (_isRunning)
        {
            OnLog?.Invoke("Already running");
            return;
        }

        var localEndpoint = new IPEndPoint(IPAddress.Parse(_config.LocalIp), _config.LocalPort);
        var remoteEndpoint = new IPEndPoint(IPAddress.Parse(_config.RemoteIp), _config.RemotePort);
        var udp = new UdpClient(localEndpoint);

        _transport = new UdpKcpTransport(udp, localEndpoint, remoteEndpoint, _config.ConversationId);
        _transport.OnLogMessage += message => OnLog?.Invoke($"[KCP] {message}");

        ApplyConfig();

        _transport.Start();
        _isRunning = true;
        _transmittedBytes = _receivedBytes = _transmittedMessages = _receivedMessages = 0;

        lock (_speedLock)
        {
            _lastSpeedCheckTicks = DateTime.UtcNow.Ticks;
            _lastTransmittedBytes = 0;
            _lastReceivedBytes = 0;
            _sendSpeedBytesPerSec = 0;
            _receiveSpeedBytesPerSec = 0;
        }

        if (_config.Direction is not KcpConfig.ECommunicationDirection.SendOnly)
            await StartReceiveLoopAsync();

        OnLog?.Invoke($"STARTED — {localEndpoint} -> {remoteEndpoint} conv={_config.ConversationId}");
    }

    public void ApplyConfig()
    {
        _transport?.SetNoDelay(_config.NoDelay, _config.IntervalMs, _config.FastResend, _config.NoCongestionControl);
        _transport?.SetWindowSize(_config.SendWindow, _config.ReceiveWindow);
        _transport?.SetMtu(_config.Mtu);
        _transport?.SetStreamMode(_config.StreamMode);
    }

    public async Task StopAsync()
    {
        if (!_isRunning || _transport is null)
            return;

        await StopReceiveLoopAsync();

        try
        {
            await _transport.StopAsync();
        }
        catch { }
        try
        {
            await _transport.DisposeAsync();
        }
        catch { }

        _transport = null;
        _isRunning = false;
        OnLog?.Invoke("STOPPED");
    }

    private async Task StartReceiveLoopAsync()
    {
        await StopReceiveLoopAsync();
        _receiveCts = new CancellationTokenSource();
        var token = _receiveCts.Token;

        _receiveTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && _transport is not null && _isRunning)
                {
                    try
                    {
                        if (_config.StreamMode)
                        {
                            var stream = _transport.GetStream();
                            var buffer = new byte[_config.BufferSize];
                            var bytesRead = await stream.ReadAsync(buffer, token);
                            if (0 < bytesRead)
                            {
                                Interlocked.Add(ref _receivedBytes, bytesRead);
                                Interlocked.Increment(ref _receivedMessages);
                                OnDataReceived(buffer.AsMemory(0, bytesRead));
                            }
                        }
                        else
                        {
                            var data = await _transport.ReadAsync(token);
                            if (!data.IsEmpty)
                            {
                                Interlocked.Add(ref _receivedBytes, data.Length);
                                Interlocked.Increment(ref _receivedMessages);
                                OnDataReceived(data);
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (InvalidOperationException) { break; }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            OnLog?.Invoke($"[RECV ERR] {ex.Message}");
                            await Task.Delay(100, token);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private async Task StopReceiveLoopAsync()
    {
        _receiveCts?.Cancel();
        try
        {
            if (_receiveTask is not null)
                await _receiveTask;
        }
        catch { }
        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
    }

    public async Task RestartReceiveLoopAsync()
    {
        await StopReceiveLoopAsync();
        if (_isRunning && _config.Direction is not KcpConfig.ECommunicationDirection.SendOnly)
            await StartReceiveLoopAsync();
    }

    private void OnDataReceived(ReadOnlyMemory<byte> data)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data.Span);
            var isPrintable = text.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');
            if (isPrintable && 0 < text.Length)
            {
                OnLog?.Invoke($"[RECV] {data.Length}B: \"{text}\"");
            }
            else
            {
                LogHexData(data);
            }
        }
        catch
        {
            LogHexData(data);
        }
    }

    private void LogHexData(ReadOnlyMemory<byte> data)
    {
        var hex = Convert.ToHexString(data.Span);
        var display = hex.Length > 64 ? hex[..64] + "..." : hex;
        OnLog?.Invoke($"[RECV] {data.Length}B: {display}");
    }

    public async Task SendTextAsync(string text)
    {
        if (_transport is null || !_isRunning)
        {
            OnLog?.Invoke("Not running");
            return;
        }
        if (_config.Direction is KcpConfig.ECommunicationDirection.ReceiveOnly)
        {
            OnLog?.Invoke("ReceiveOnly");
            return;
        }

        try
        {
            var data = Encoding.UTF8.GetBytes(text);
            if (_config.StreamMode)
                await _transport.GetStream().WriteAsync(data);
            else
                _transport.Write(data);
            Interlocked.Add(ref _transmittedBytes, data.Length);
            Interlocked.Increment(ref _transmittedMessages);
            OnLog?.Invoke($"[SENT] {data.Length}B: \"{text}\"");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[SEND ERR] {ex.Message}");
        }
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        if (_transport is null || !_isRunning)
        {
            OnLog?.Invoke("Not running");
            return;
        }
        if (_config.Direction is KcpConfig.ECommunicationDirection.ReceiveOnly)
        {
            OnLog?.Invoke("ReceiveOnly");
            return;
        }

        try
        {
            if (_config.StreamMode)
                await _transport.GetStream().WriteAsync(data);
            else
                _transport.Write(data);
            Interlocked.Add(ref _transmittedBytes, data.Length);
            Interlocked.Increment(ref _transmittedMessages);
            OnLog?.Invoke($"[SENT] {data.Length}B");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[SEND ERR] {ex.Message}");
        }
    }

    public async Task FloodAsync(int count, int size)
    {
        if (_transport is null || !_isRunning)
        {
            OnLog?.Invoke("Not running");
            return;
        }
        if (_config.Direction is KcpConfig.ECommunicationDirection.ReceiveOnly)
        {
            OnLog?.Invoke("ReceiveOnly");
            return;
        }

        OnLog?.Invoke($"Flooding {count} x {size}B...");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < count; i++)
        {
            try
            {
                var data = new byte[size];
                Random.Shared.NextBytes(data);
                if (_config.StreamMode)
                    await _transport.GetStream().WriteAsync(data);
                else
                    _transport.Write(data);
                Interlocked.Add(ref _transmittedBytes, size);
                Interlocked.Increment(ref _transmittedMessages);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Flood {i} err: {ex.Message}");
                break;
            }
        }

        stopwatch.Stop();
        var megabytesPerSec = stopwatch.ElapsedMilliseconds > 0
            ? count * (long)size / 1024.0 / 1024.0 / (stopwatch.ElapsedMilliseconds / 1000.0)
            : 0;
        OnLog?.Invoke($"Flood: {count} msgs in {stopwatch.ElapsedMilliseconds}ms ({megabytesPerSec:F2} MB/s)");
    }

    public async Task StreamSendAsync(int size)
    {
        if (_transport is null || !_isRunning)
        {
            OnLog?.Invoke("Not running");
            return;
        }
        if (!_config.StreamMode)
        {
            OnLog?.Invoke("Enable Stream Mode first");
            return;
        }

        var data = new byte[size];
        Random.Shared.NextBytes(data);
        OnLog?.Invoke($"Streaming {size:N0}B...");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await _transport.GetStream().WriteAsync(data);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Stream err: {ex.Message}");
            return;
        }

        stopwatch.Stop();
        Interlocked.Add(ref _transmittedBytes, size);
        Interlocked.Increment(ref _transmittedMessages);

        var megabytesPerSec = stopwatch.ElapsedMilliseconds > 0
            ? size / 1024.0 / 1024.0 / (stopwatch.ElapsedMilliseconds / 1000.0)
            : 0;
        OnLog?.Invoke($"Stream: {size:N0}B in {stopwatch.ElapsedMilliseconds}ms ({megabytesPerSec:F2} MB/s)");
    }

    public string BuildStatisticsString()
    {
        if (_transport is null || !_isRunning)
            return "Transport not running";

        try
        {
            var transport = _transport;
            var sendSpeed = SendSpeedBytesPerSec;
            var recvSpeed = ReceiveSpeedBytesPerSec;

            return new StringBuilder()
                .AppendLine($"Conv: {transport.ConversationId}  State: {transport.State}  Dead: {transport.IsDead}")
                .AppendLine($"NoDelay: {transport.NoDelay}  Interval: {transport.Interval}ms  FastResend: {transport.FastResend}")
                .AppendLine($"SRTT: {transport.SmoothedRoundTripTime}ms  RTTVar: {transport.RoundTripTimeVariance}ms  RTO: {transport.RetransmissionTimeout}ms")
                .AppendLine($"SndWnd: {transport.SendWindow}  RcvWnd: {transport.ReceiveWindow}  RmtWnd: {transport.RemoteWindow}  CWnd: {transport.CongestionWindow}")
                .AppendLine($"SSThresh: {transport.SlowStartThreshold}  NoCWnd: {transport.NoCongestionWindow}  FastLimit: {transport.FastLimit}")
                .AppendLine($"MTU: {transport.Mtu}  MSS: {transport.MaximumSegmentSize}  Stream: {transport.IsStreamMode}")
                .AppendLine($"SndUna: {transport.SendUnacknowledged}  SndNxt: {transport.SendNext}  RcvNxt: {transport.ReceiveNext}")
                .AppendLine($"WaitSnd: {transport.GetWaitSnd()}  FreeSnd: {transport.GetFreeSendWindowBytes()}B  Xmit: {transport.RetransmissionCount}  DeadLink: {transport.DeadLink}")
                .AppendLine($"RcvBuf: {transport.ReceiveBufferCount}  SndBuf: {transport.SendBufferCount}  RcvQue: {transport.ReceiveQueueCount}  SndQue: {transport.SendQueueCount}")
                .AppendLine($"UDP Tx: {transport.UdpBytesSent:N0}B/{transport.UdpPacketsSent:N0}pkt  Rx: {transport.UdpBytesReceived:N0}B/{transport.UdpPacketsReceived:N0}pkt")
                .AppendLine($"App Tx: {TransmittedBytes:N0}B/{TransmittedMessages:N0}msg  Rx: {ReceivedBytes:N0}B/{ReceivedMessages:N0}msg")
                .AppendLine($"Speed Tx: {FormatSpeed(sendSpeed)}  Rx: {FormatSpeed(recvSpeed)}")
                .ToString();
        }
        catch (ObjectDisposedException)
        {
            return "Transport disposed";
        }
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024)
            return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024)
            return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec / 1024 / 1024:F2} MB/s";
    }
}
