namespace Kanawanagasaki.KCP.Sample;

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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

    private const byte FrameTypeText = 0x01;
    private const byte FrameTypeBinary = 0x02;
    private const byte FrameTypeStream = 0x03;
    private const int FrameHeaderSize = 1 + 4;
    private const int FrameHashSize = 32;

    public UdpKcpTransport? Transport => _transport;
    public bool IsRunning => _isRunning;
    public KcpConfig Config => _config;

    public long TransmittedBytes => Volatile.Read(ref _transmittedBytes);
    public long ReceivedBytes => Volatile.Read(ref _receivedBytes);
    public long TransmittedMessages => Volatile.Read(ref _transmittedMessages);
    public long ReceivedMessages => Volatile.Read(ref _receivedMessages);

    public event Action<string>? OnLog;

    public KcpSession(KcpConfig config)
    {
        _config = config;
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
                if (_config.StreamMode)
                    await ReceiveStreamModeAsync(token);
                else
                    await ReceiveMessageModeAsync(token);
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

    private async Task ReceiveStreamModeAsync(CancellationToken token)
    {
        var stream = _transport!.GetStream();
        var headerBuf = new byte[FrameHeaderSize];
        var readBuf = ArrayPool<byte>.Shared.Rent(_config.BufferSize);

        try
        {
            while (!token.IsCancellationRequested && _transport is not null && _isRunning)
            {
                try
                {
                    await stream.ReadExactlyAsync(headerBuf, token);
                    var frameType = headerBuf[0];
                    var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(1));

                    switch (frameType)
                    {
                        case FrameTypeText:
                            {
                                var textBuf = ArrayPool<byte>.Shared.Rent((int)payloadSize);
                                try
                                {
                                    await stream.ReadExactlyAsync(textBuf.AsMemory(0, (int)payloadSize), token);
                                    var text = Encoding.UTF8.GetString(textBuf, 0, (int)payloadSize);
                                    OnLog?.Invoke($"[RECV] {payloadSize}B: \"{text}\"");
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(textBuf);
                                }
                                Interlocked.Add(ref _receivedBytes, FrameHeaderSize + payloadSize);
                                break;
                            }
                        case FrameTypeBinary:
                            {
                                long remaining = payloadSize;
                                while (0 < remaining)
                                {
                                    int toRead = (int)Math.Min(remaining, readBuf.Length);
                                    await stream.ReadExactlyAsync(readBuf.AsMemory(0, toRead), token);
                                    remaining -= toRead;
                                }
                                OnLog?.Invoke($"[RECV] {FormatSize(payloadSize)} binary");
                                Interlocked.Add(ref _receivedBytes, FrameHeaderSize + payloadSize);
                                break;
                            }
                        case FrameTypeStream:
                            {
                                OnLog?.Invoke($"Receiving stream of {FormatSize(payloadSize)}...");
                                var sw = Stopwatch.StartNew();
                                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                                {
                                    long remaining = payloadSize;
                                    while (0 < remaining)
                                    {
                                        int toRead = (int)Math.Min(remaining, readBuf.Length);
                                        await stream.ReadExactlyAsync(readBuf.AsMemory(0, toRead), token);
                                        hash.AppendData(readBuf.AsSpan(0, toRead));
                                        remaining -= toRead;
                                    }

                                    var hashBuf = new byte[FrameHashSize];
                                    await stream.ReadExactlyAsync(hashBuf, token);

                                    var computedHash = hash.GetHashAndReset();
                                    sw.Stop();
                                    var safeMs = Math.Max(1, sw.ElapsedMilliseconds);
                                    var hashStr = Convert.ToHexString(computedHash).ToLowerInvariant();
                                    var match = computedHash.SequenceEqual(hashBuf);
                                    OnLog?.Invoke($"[STREAM RX] {FormatSize(payloadSize)} in {sw.ElapsedMilliseconds}ms ({FormatSpeed(payloadSize / (safeMs / 1000.0))}) SHA256:{hashStr} {(match ? "OK" : "MISMATCH")}");
                                }
                                Interlocked.Add(ref _receivedBytes, FrameHeaderSize + payloadSize + FrameHashSize);
                                break;
                            }
                        default:
                            OnLog?.Invoke($"[FRAME ERR] Unknown type 0x{frameType:X2}");
                            break;
                    }
                    Interlocked.Increment(ref _receivedMessages);
                }
                catch (OperationCanceledException) { throw; }
                catch (ObjectDisposedException) { throw; }
                catch (InvalidOperationException) { throw; }
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
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuf);
        }
    }

    private async Task ReceiveMessageModeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _transport is not null && _isRunning)
        {
            try
            {
                var data = await _transport.ReadAsync(token);
                if (data.IsEmpty)
                    continue;

                Interlocked.Add(ref _receivedBytes, data.Length);
                Interlocked.Increment(ref _receivedMessages);

                if (FrameHeaderSize <= data.Length)
                {
                    var frameType = data.Span[0];
                    var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Span.Slice(1));

                    if (data.Length == FrameHeaderSize + payloadSize && IsValidFrameType(frameType))
                    {
                        switch (frameType)
                        {
                            case FrameTypeText:
                                {
                                    var payload = data.Span.Slice(FrameHeaderSize, (int)payloadSize);
                                    var frameText = Encoding.UTF8.GetString(payload);
                                    OnLog?.Invoke($"[RECV] {payloadSize}B: \"{frameText}\"");
                                    break;
                                }
                            case FrameTypeBinary:
                                OnLog?.Invoke($"[RECV] {FormatSize(payloadSize)} binary");
                                break;
                            case FrameTypeStream:
                                OnLog?.Invoke("[RECV ERR] Stream frame not supported in message mode");
                                break;
                        }
                        continue;
                    }
                }

                var text = Encoding.UTF8.GetString(data.Span);
                var isPrintable = text.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');
                if (isPrintable && 0 < text.Length)
                    OnLog?.Invoke($"[RECV] {data.Length}B: \"{text}\"");
                else
                {
                    var hex = Convert.ToHexString(data.Span);
                    var display = 64 < hex.Length ? hex[..64] + "..." : hex;
                    OnLog?.Invoke($"[RECV] {data.Length}B: {display}");
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

    private static bool IsValidFrameType(byte t)
        => t is FrameTypeText or FrameTypeBinary or FrameTypeStream;

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
            var textBytes = Encoding.UTF8.GetBytes(text);
            var header = new byte[FrameHeaderSize];
            header[0] = FrameTypeText;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), (uint)textBytes.Length);

            if (_config.StreamMode)
            {
                var stream = _transport.GetStream();
                await stream.WriteAsync(header);
                await stream.WriteAsync(textBytes);
            }
            else
            {
                var frame = new byte[FrameHeaderSize + textBytes.Length];
                header.CopyTo(frame, 0);
                textBytes.CopyTo(frame, FrameHeaderSize);
                _transport.Write(frame);
            }

            Interlocked.Add(ref _transmittedBytes, textBytes.Length);
            Interlocked.Increment(ref _transmittedMessages);
            OnLog?.Invoke($"[SENT] {textBytes.Length}B: \"{text}\"");
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
            var header = new byte[FrameHeaderSize];
            header[0] = FrameTypeBinary;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), (uint)data.Length);

            if (_config.StreamMode)
            {
                var stream = _transport.GetStream();
                await stream.WriteAsync(header);
                await stream.WriteAsync(data);
            }
            else
            {
                var frame = new byte[FrameHeaderSize + data.Length];
                header.CopyTo(frame, 0);
                data.CopyTo(frame, FrameHeaderSize);
                _transport.Write(frame);
            }

            Interlocked.Add(ref _transmittedBytes, data.Length);
            Interlocked.Increment(ref _transmittedMessages);
            OnLog?.Invoke($"[SENT] {data.Length}B");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[SEND ERR] {ex.Message}");
        }
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

        var data = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            Random.Shared.NextBytes(data.AsSpan(0, size));

            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(data, 0, size);

            OnLog?.Invoke($"Streaming {FormatSize(size)}...");
            var sw = Stopwatch.StartNew();

            try
            {
                var stream = _transport.GetStream();

                var header = new byte[FrameHeaderSize];
                header[0] = FrameTypeStream;
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), (uint)size);
                await stream.WriteAsync(header);
                await stream.WriteAsync(data.AsMemory(0, size));
                await stream.WriteAsync(hash);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Stream err: {ex.Message}");
                return;
            }

            sw.Stop();
            Interlocked.Add(ref _transmittedBytes, size);
            Interlocked.Increment(ref _transmittedMessages);

            var safeMs = Math.Max(1, sw.ElapsedMilliseconds);
            var hashStr = Convert.ToHexString(hash).ToLowerInvariant();
            OnLog?.Invoke($"[STREAM TX] {FormatSize(size)} in {sw.ElapsedMilliseconds}ms ({FormatSpeed((double)size / (safeMs / 1000.0))}) SHA256:{hashStr}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    public string BuildStatisticsString()
    {
        if (_transport is null || !_isRunning)
            return "Transport not running";

        try
        {
            var t = _transport;
            return new StringBuilder()
                .AppendLine($"Conv: {t.ConversationId}  State: {t.State}  Dead: {t.IsDead}")
                .AppendLine($"NoDelay: {t.NoDelay}  Interval: {t.Interval}ms  FastResend: {t.FastResend}")
                .AppendLine($"SRTT: {t.SmoothedRoundTripTime}ms  RTTVar: {t.RoundTripTimeVariance}ms  RTO: {t.RetransmissionTimeout}ms")
                .AppendLine($"SndWnd: {t.SendWindow}  RcvWnd: {t.ReceiveWindow}  RmtWnd: {t.RemoteWindow}  CWnd: {t.CongestionWindow}")
                .AppendLine($"SSThresh: {t.SlowStartThreshold}  NoCWnd: {t.NoCongestionWindow}  FastLimit: {t.FastLimit}")
                .AppendLine($"MTU: {t.Mtu}  MSS: {t.MaximumSegmentSize}  Stream: {t.IsStreamMode}")
                .AppendLine($"SndUna: {t.SendUnacknowledged}  SndNxt: {t.SendNext}  RcvNxt: {t.ReceiveNext}")
                .AppendLine($"WaitSnd: {t.GetWaitSnd()}  FreeSnd: {t.GetFreeSendWindowBytes()}B  Xmit: {t.RetransmissionCount}  DeadLink: {t.DeadLink}")
                .AppendLine($"RcvBuf: {t.ReceiveBufferCount}  SndBuf: {t.SendBufferCount}  RcvQue: {t.ReceiveQueueCount}  SndQue: {t.SendQueueCount}")
                .AppendLine($"UDP Tx: {FormatSize(t.UdpBytesSent)}/{t.UdpPacketsSent:N0}pkt  Rx: {FormatSize(t.UdpBytesReceived)}/{t.UdpPacketsReceived:N0}pkt")
                .AppendLine($"App Tx: {FormatSize(TransmittedBytes)}/{TransmittedMessages:N0}msg  Rx: {FormatSize(ReceivedBytes)}/{ReceivedMessages:N0}msg")
                .Append($"Speed Tx: {FormatSpeed(t.GetLastSecondSendBytes())}  Rx: {FormatSpeed(t.GetLastSecondReceiveBytes())}")
                .ToString();
        }
        catch (ObjectDisposedException)
        {
            return "Transport disposed";
        }
    }

    public static string FormatSpeed(long bytesPerSec)
        => FormatSpeed((double)bytesPerSec);

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024)
            return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024)
            return $"{bytesPerSec / 1024:0.0} KB/s";

        return $"{bytesPerSec / 1024 / 1024:0.0} MB/s";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes}B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.0}KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / 1024.0 / 1024.0:0.0}MB";

        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0}GB";
    }
}
