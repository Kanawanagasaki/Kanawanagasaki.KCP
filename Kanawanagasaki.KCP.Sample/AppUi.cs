namespace Kanawanagasaki.KCP.Sample;

using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public static class AppUi
{
    private static IApplication? _application;
    private static KcpSession? _session;
    private static Window? _window;
    private static bool _isCompactLayout;

    private const int FullLayoutMinWidth = 90;
    private const int FullLayoutMinHeight = 26;
    private const int TopSectionHeight = 14;

    private static Editor? _logView;
    private static int _logLineCount;
    private static readonly Lock _logLock = new();

    private static readonly List<string> _pendingLogs = [];
    private static readonly Lock _pendingLogsLock = new();

    private static TextField? _commandInput;
    private static Label? _hintLabel;

    private static Label? _statsLabel;

    private static TextField? _fieldLocalIp;
    private static TextField? _fieldLocalPort;
    private static TextField? _fieldRemoteIp;
    private static TextField? _fieldRemotePort;
    private static TextField? _fieldConversationId;

    private static CheckBox? _checkNoDelay;
    private static CheckBox? _checkNoCongestion;
    private static CheckBox? _checkStreamMode;
    private static TextField? _fieldInterval;
    private static TextField? _fieldFastResend;
    private static TextField? _fieldSendWindow;
    private static TextField? _fieldReceiveWindow;
    private static TextField? _fieldMtu;

    private static TextField? _fieldBufferSize;
    private static OptionSelector<KcpConfig.ECommunicationDirection>? _directionSelector;

    private static readonly (string Command, string Hint, string Usage)[] CommandHints =
    [
        ("/send", "Send text message", "/send <text>"),
        ("/hex", "Send hex data", "/hex <hex>"),
        ("/random", "Send random bytes", "/random <size>"),
        ("/flood", "Flood messages", "/flood <count> <size>"),
        ("/stream", "Stream data", "/stream <size>"),
        ("/flush", "Flush KCP buffer", "/flush"),
        ("/nodelay", "Set nodelay params", "/nodelay <0|1> <interval> <fastResend> <noCwnd:0|1>"),
        ("/window", "Set window sizes", "/window <send> <receive>"),
        ("/mtu", "Set MTU", "/mtu <value>"),
        ("/interval", "Set interval", "/interval <ms>"),
        ("/direction", "Set direction", "/direction <both|send|recv>"),
        ("/help", "Show help", "/help"),
    ];

    public static async Task RunAsync(KcpSession session)
    {
        _session = session;
        _session.OnLog += Log;

        using (_application = Application.Create().Init())
        {
            _window = new Window();


            var screen = _application.Screen;
            _isCompactLayout = screen.Width < FullLayoutMinWidth || screen.Height < FullLayoutMinHeight;

            BuildLayout();


            _application.ScreenChanged += OnScreenChanged;

            _application.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
            {
                _session?.UpdateSpeed();
                RefreshStats();
                FlushPendingLogs();
                return true;
            });

            _application.Run(_window);
        }



        if (_session is not null && _session.IsRunning)
            await _session.StopAsync();
    }

    #region Layout

    private static void OnScreenChanged(object? sender, EventArgs<Rectangle> e)
    {
        var shouldBeCompact = e.Value.Width < FullLayoutMinWidth || e.Value.Height < FullLayoutMinHeight;

        if (shouldBeCompact != _isCompactLayout)
        {
            _isCompactLayout = shouldBeCompact;
            RebuildLayout();
        }
    }

    private static void RebuildLayout()
    {
        if (_window is null) return;


        var savedLogText = _logView?.Text.ToString() ?? "";
        var savedInputText = _commandInput?.Text.ToString() ?? "";
        var savedLogLineCount = _logLineCount;


        _window.RemoveAll();


        _logView = null;
        _statsLabel = null;
        _hintLabel = null;
        _commandInput = null;
        _fieldLocalIp = null;
        _fieldLocalPort = null;
        _fieldRemoteIp = null;
        _fieldRemotePort = null;
        _fieldConversationId = null;
        _checkNoDelay = null;
        _checkNoCongestion = null;
        _checkStreamMode = null;
        _fieldInterval = null;
        _fieldFastResend = null;
        _fieldSendWindow = null;
        _fieldReceiveWindow = null;
        _fieldMtu = null;
        _fieldBufferSize = null;
        _directionSelector = null;

        BuildLayout();


        if (_logView is not null && !string.IsNullOrEmpty(savedLogText))
            _logView.Text = savedLogText;
        if (_commandInput is not null && !string.IsNullOrEmpty(savedInputText))
            _commandInput.Text = savedInputText;
        _logLineCount = savedLogLineCount;
    }

    private static void BuildLayout()
    {
        if (_window is null) return;

        if (_isCompactLayout)
            BuildCompactLayout();
        else
            BuildFullLayout();

        BuildStatusBar();
    }

    private static void BuildFullLayout()
    {

        var configFrame = new FrameView
        {
            Title = "Config",
            X = 0,
            Y = 1,
            Width = 58,
            Height = TopSectionHeight
        };
        BuildConfigPanel(configFrame);
        _window!.Add(configFrame);


        var statsFrame = new FrameView
        {
            Title = "Statistics",
            X = Pos.Right(configFrame),
            Y = 1,
            Width = Dim.Fill(),
            Height = TopSectionHeight
        };
        _statsLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "Start the transport to see statistics."
        };
        statsFrame.Add(_statsLabel);
        _window.Add(statsFrame);


        var logFrame = new FrameView
        {
            Title = "Log",
            X = 0,
            Y = Pos.Bottom(configFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2
        };
        _logView = new Editor
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Multiline = true,
        };
        logFrame.Add(_logView);
        _window.Add(logFrame);


        BuildCommandInput(Pos.Bottom(logFrame));
    }

    private static void BuildCompactLayout()
    {
        var tabs = new Tabs
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = TopSectionHeight,
            TabSide = Side.Top
        };


        var configTab = new FrameView
        {
            Title = "Config",
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        BuildConfigPanel(configTab);
        tabs.InsertTab(0, configTab);


        var statsTab = new FrameView
        {
            Title = "Statistics",
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _statsLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "Start the transport to see statistics."
        };
        statsTab.Add(_statsLabel);
        tabs.InsertTab(1, statsTab);


        tabs.Value = configTab;

        _window!.Add(tabs);


        var logFrame = new FrameView
        {
            Title = "Log",
            X = 0,
            Y = Pos.Bottom(tabs),
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2
        };
        _logView = new Editor
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Multiline = true,
        };
        logFrame.Add(_logView);
        _window.Add(logFrame);


        BuildCommandInput(Pos.Bottom(logFrame));
    }

    #endregion

    #region Config Panel

    private static void BuildConfigPanel(View container)
    {
        int row = 0;


        container.Add(CreateLabel("Network", 1, row));
        row++;

        container.Add(CreateLabel("Local:", 1, row));
        _fieldLocalIp = CreateTextField(_session?.Config.LocalIp ?? string.Empty, 8, row, 15);
        container.Add(_fieldLocalIp);

        container.Add(CreateLabel("Port:", 25, row));
        _fieldLocalPort = CreateTextField(_session?.Config.LocalPort.ToString() ?? string.Empty, 30, row, 6);
        container.Add(_fieldLocalPort);

        container.Add(CreateLabel("Conv:", 38, row));
        _fieldConversationId = CreateTextField(_session?.Config.ConversationId.ToString() ?? string.Empty, 43, row, 6);
        container.Add(_fieldConversationId);
        row++;

        container.Add(CreateLabel("Remote:", 1, row));
        _fieldRemoteIp = CreateTextField(_session?.Config.RemoteIp ?? string.Empty, 8, row, 15);
        container.Add(_fieldRemoteIp);

        container.Add(CreateLabel("Port:", 25, row));
        _fieldRemotePort = CreateTextField(_session?.Config.RemotePort.ToString() ?? string.Empty, 30, row, 6);
        container.Add(_fieldRemotePort);

        var applyNetBtn = new Button { Text = "Net+", X = 38, Y = row };
        applyNetBtn.Accepting += (_, _) => ApplyNetworkSettings();
        container.Add(applyNetBtn);
        row++;


        container.Add(CreateLabel("KCP", 1, row));
        row++;

        _checkNoDelay = new CheckBox
        {
            Text = "ND",
            X = 1,
            Y = row,
            Value = _session?.Config.NoDelay == true ? CheckState.Checked : CheckState.UnChecked
        };
        container.Add(_checkNoDelay);

        _checkNoCongestion = new CheckBox
        {
            Text = "NoCwnd",
            X = 14,
            Y = row,
            Value = _session?.Config.NoCongestionControl == true ? CheckState.Checked : CheckState.UnChecked
        };
        container.Add(_checkNoCongestion);

        _checkStreamMode = new CheckBox
        {
            Text = "Stream",
            X = 33,
            Y = row,
            Value = _session?.Config.StreamMode == true ? CheckState.Checked : CheckState.UnChecked
        };
        container.Add(_checkStreamMode);
        row++;

        container.Add(CreateLabel("Iv:", 1, row));
        _fieldInterval = CreateTextField(_session?.Config.IntervalMs.ToString() ?? string.Empty, 4, row, 6);
        container.Add(_fieldInterval);

        container.Add(CreateLabel("FR:", 13, row));
        _fieldFastResend = CreateTextField(_session?.Config.FastResend.ToString() ?? string.Empty, 16, row, 4);
        container.Add(_fieldFastResend);

        container.Add(CreateLabel("MTU:", 23, row));
        _fieldMtu = CreateTextField(_session?.Config.Mtu.ToString() ?? string.Empty, 27, row, 6);
        container.Add(_fieldMtu);

        container.Add(CreateLabel("SndW:", 36, row));
        _fieldSendWindow = CreateTextField(_session?.Config.SendWindow.ToString() ?? string.Empty, 41, row, 5);
        container.Add(_fieldSendWindow);

        container.Add(CreateLabel("RcvW:", 48, row));
        _fieldReceiveWindow = CreateTextField(_session?.Config.ReceiveWindow.ToString() ?? string.Empty, 53, row, 5);
        container.Add(_fieldReceiveWindow);
        row++;

        var applyKcpBtn = new Button { Text = "KCP+", X = 1, Y = row };
        applyKcpBtn.Accepting += (_, _) => ApplyKcpSettings();
        container.Add(applyKcpBtn);
        row++;


        container.Add(CreateLabel("Comm", 1, row));
        row++;

        container.Add(CreateLabel("Buf:", 1, row));
        _fieldBufferSize = CreateTextField(_session?.Config.BufferSize.ToString() ?? string.Empty, 5, row, 6);
        container.Add(_fieldBufferSize);

        container.Add(CreateLabel("Dir:", 14, row));
        _directionSelector = new OptionSelector<KcpConfig.ECommunicationDirection>
        {
            X = 18,
            Y = row,
            Width = 15,
            Height = 1,
            Labels = ["Both", "Send", "Recv"],
            Value = _session?.Config.Direction
        };
        container.Add(_directionSelector);

        var applyCommBtn = new Button { Text = "Comm+", X = 35, Y = row };
        applyCommBtn.Accepting += (_, _) => _ = ApplyCommunicationSettingsAsync();
        container.Add(applyCommBtn);
    }

    #endregion

    #region Command Input

    private static void BuildCommandInput(Window window, Pos topPos)
    {
        var cmdLabel = CreateLabel("Cmd>", 0, topPos);
        window.Add(cmdLabel);

        _commandInput = new TextField
        {
            X = Pos.Right(cmdLabel) + 1,
            Y = topPos,
            Width = Dim.Fill() - 8,
            Height = 1
        };
        _commandInput.Accepting += (_, _) => _ = OnCommandInputAsync();
        _commandInput.TextChanging += (_, args) => UpdateCommandHint(args.Result ?? "");
        window.Add(_commandInput);

        var sendBtn = new Button { Text = "_Send", X = Pos.Right(_commandInput) + 1, Y = topPos };
        sendBtn.Accepting += (_, _) => _ = OnCommandInputAsync();
        window.Add(sendBtn);


        _hintLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(cmdLabel),
            Width = Dim.Fill(),
            Height = 1,
            Text = "Type / for commands, or just type text to send",
            SchemeName = "Accent"
        };
        window.Add(_hintLabel);
    }

    private static void BuildCommandInput(Pos topPos)
    {
        if (_window is null) return;
        BuildCommandInput(_window, topPos);
    }

    #endregion

    #region Command Hints

    private static void UpdateCommandHint(string currentText)
    {
        if (_hintLabel is null) return;

        if (string.IsNullOrEmpty(currentText) || !currentText.StartsWith('/'))
        {
            _hintLabel.Text = "Type / for commands, or just type text to send";
            return;
        }

        var input = currentText.TrimStart('/');
        if (string.IsNullOrEmpty(input))
        {
            _hintLabel.Text = "Commands: /send /hex /random /flood /stream /flush /nodelay /window /mtu /interval /direction /help";
            return;
        }


        var matches = CommandHints.Where(c => c.Command.StartsWith(currentText, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            _hintLabel.Text = "Unknown command. Try /help";
            return;
        }

        if (matches.Length == 1)
        {
            _hintLabel.Text = $"{matches[0].Command} — {matches[0].Hint}  Usage: {matches[0].Usage}";
            return;
        }


        _hintLabel.Text = string.Join("  ", matches.Select(m => m.Command));
    }

    #endregion

    private static void BuildStatusBar()
    {
        if (_window is null) return;

        var statusBar = new StatusBar(
        [
            new(Key.Q.WithCtrl, "~^Q~ Quit", () => _application?.RequestStop(), ""),
            new(Key.S.WithCtrl, "~^S~ Start", () => _ = OnStartTransportAsync(), ""),
            new(Key.T.WithCtrl, "~^T~ Stop", ()=> _ = OnStopTransportAsync(), ""),
            new(Key.L.WithCtrl, "~^L~ Clear Log", () =>
            {
                lock (_logLock)
                    _logLineCount = 0;
                if (_logView is not null)
                    _logView.Text = "";
            }, ""),
        ]);

        _window.Add(statusBar);
    }

    #region Settings Apply

    private static void ApplyNetworkSettings()
    {
        _session?.Config.LocalIp = GetTextFieldValue(_fieldLocalIp, _session?.Config.LocalIp ?? "0.0.0.0");
        _session?.Config.LocalPort = GetTextFieldInt(_fieldLocalPort, _session?.Config.LocalPort ?? 10001);
        _session?.Config.RemoteIp = GetTextFieldValue(_fieldRemoteIp, _session?.Config.RemoteIp ?? "127.0.0.1");
        _session?.Config.RemotePort = GetTextFieldInt(_fieldRemotePort, _session?.Config.RemotePort ?? 10002);
        _session?.Config.ConversationId = GetTextFieldUint(_fieldConversationId, _session?.Config.ConversationId ?? 1);

        Log($"Network: {_session?.Config.LocalIp}:{_session?.Config.LocalPort} -> {_session?.Config.RemoteIp}:{_session?.Config.RemotePort} conv={_session?.Config.ConversationId}");
    }

    private static void ApplyKcpSettings()
    {
        _session?.Config.NoDelay = IsCheckBoxChecked(_checkNoDelay);
        _session?.Config.IntervalMs = GetTextFieldInt(_fieldInterval, _session?.Config.IntervalMs ?? 100);
        _session?.Config.FastResend = GetTextFieldInt(_fieldFastResend, _session?.Config.FastResend ?? 0);
        _session?.Config.NoCongestionControl = IsCheckBoxChecked(_checkNoCongestion);
        _session?.Config.SendWindow = GetTextFieldInt(_fieldSendWindow, _session?.Config.SendWindow ?? 128);
        _session?.Config.ReceiveWindow = GetTextFieldInt(_fieldReceiveWindow, _session?.Config.ReceiveWindow ?? 256);
        _session?.Config.Mtu = GetTextFieldInt(_fieldMtu, _session?.Config.Mtu ?? 1400);
        _session?.Config.StreamMode = IsCheckBoxChecked(_checkStreamMode);

        if (_session?.Transport is not null && _session.IsRunning)
        {
            try
            {
                _session.ApplyConfig();
                Log("KCP settings applied LIVE");
            }
            catch (Exception ex)
            {
                Log($"Live apply error: {ex.Message}");
            }
        }
        else
        {
            Log("KCP settings saved");
        }
    }

    private static async Task ApplyCommunicationSettingsAsync()
    {
        try
        {
            _session?.Config.BufferSize = GetTextFieldInt(_fieldBufferSize, _session?.Config.BufferSize ?? 4096);
            _session?.Config.Direction = _directionSelector?.Value ?? _session?.Config.Direction ?? KcpConfig.ECommunicationDirection.Bidirectional;

            if (_session is not null && _session.IsRunning)
                await _session.RestartReceiveLoopAsync();

            Log($"Comm: Dir={_session?.Config.Direction}, Buf={_session?.Config.BufferSize}, Stream={_session?.Config.StreamMode}");
        }
        catch (Exception ex)
        {
            Log($"ApplyCommunicationSettingsAsync FAILED: {ex.Message}");
        }
    }

    private static void SyncUiFromConfig()
    {
        _fieldLocalIp?.Text = _session?.Config.LocalIp ?? string.Empty;
        _fieldLocalPort?.Text = _session?.Config.LocalPort.ToString() ?? string.Empty;
        _fieldRemoteIp?.Text = _session?.Config.RemoteIp ?? string.Empty;
        _fieldRemotePort?.Text = _session?.Config.RemotePort.ToString() ?? string.Empty;
        _fieldConversationId?.Text = _session?.Config.ConversationId.ToString() ?? string.Empty;

        _checkNoDelay?.Value = _session?.Config.NoDelay == true ? CheckState.Checked : CheckState.UnChecked;
        _fieldInterval?.Text = _session?.Config.IntervalMs.ToString() ?? string.Empty;
        _fieldFastResend?.Text = _session?.Config.FastResend.ToString() ?? string.Empty;
        _checkNoCongestion?.Value = _session?.Config.NoCongestionControl == true ? CheckState.Checked : CheckState.UnChecked;
        _fieldSendWindow?.Text = _session?.Config.SendWindow.ToString() ?? string.Empty;
        _fieldReceiveWindow?.Text = _session?.Config.ReceiveWindow.ToString() ?? string.Empty;
        _fieldMtu?.Text = _session?.Config.Mtu.ToString() ?? string.Empty;
        _checkStreamMode?.Value = _session?.Config.StreamMode == true ? CheckState.Checked : CheckState.UnChecked;

        _fieldBufferSize?.Text = _session?.Config.BufferSize.ToString() ?? string.Empty;
        _directionSelector?.Value = _session?.Config.Direction;
    }

    #endregion

    #region Transport Start/Stop

    private static async Task OnStartTransportAsync()
    {
        try
        {
            if (_session is null)
                return;

            ApplyNetworkSettings();
            ApplyKcpSettings();

            _session.ApplyConfig();

            await _session.StartAsync();
        }
        catch (Exception ex)
        {
            Log($"START FAILED: {ex.Message}");
        }
    }

    private static async Task OnStopTransportAsync()
    {
        try
        {
            if (_session is not null)
                await _session.StopAsync();
        }
        catch (Exception ex)
        {
            Log($"STOP FAILED: {ex.Message}");
        }
    }

    #endregion

    #region Command Processing

    private static async Task OnCommandInputAsync()
    {
        if (_commandInput is null)
            return;

        var input = _commandInput.Text.ToString()?.Trim() ?? "";
        if (string.IsNullOrEmpty(input))
            return;

        _commandInput.Text = "";

        if (!input.StartsWith('/'))
        {
            if (_session is not null)
                await _session.SendTextAsync(input);
            return;
        }

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        try
        {
            switch (command)
            {
                case "/send":
                    if (parts.Length < 2)
                    {
                        Log("Usage: /send <text>");
                        break;
                    }
                    if (_session is not null)
                        await _session.SendTextAsync(string.Join(' ', parts[1..]));
                    break;

                case "/hex":
                    if (parts.Length < 2)
                    {
                        Log("Usage: /hex <hex>");
                        break;
                    }
                    if (_session is not null)
                        await _session.SendBinaryAsync(Convert.FromHexString(string.Join("", parts[1..])));
                    break;

                case "/random":
                    if (parts.Length < 2 || !int.TryParse(parts[1], out var randomSize))
                    {
                        Log("Usage: /random <size>");
                        break;
                    }
                    var randomData = new byte[randomSize];
                    Random.Shared.NextBytes(randomData);
                    if (_session is not null)
                        await _session.SendBinaryAsync(randomData);
                    break;

                case "/flood":
                    if (parts.Length < 3 || !int.TryParse(parts[1], out var floodCount) || !int.TryParse(parts[2], out var floodSize))
                    {
                        Log("Usage: /flood <count> <size>");
                        break;
                    }
                    if (_session is not null)
                        await _session.FloodAsync(floodCount, floodSize);
                    break;

                case "/stream":
                    if (parts.Length < 2 || !int.TryParse(parts[1], out var streamSize))
                    {
                        Log("Usage: /stream <size>");
                        break;
                    }
                    if (_session is not null)
                        await _session.StreamSendAsync(streamSize);
                    break;

                case "/flush":
                    if (_session?.Transport is not null && _session.IsRunning)
                    {
                        _session.Transport.Flush();
                        Log("Flushed.");
                    }
                    else
                    {
                        Log("Not running.");
                    }
                    break;

                case "/nodelay":
                    if (parts.Length < 5 || _session?.Transport is null)
                    {
                        Log("Usage: /nodelay <0|1> <interval> <fastResend> <noCwnd:0|1>");
                        break;
                    }
                    var noDelay = parts[1] == "1";
                    var interval = int.Parse(parts[2]);
                    var fastResend = int.Parse(parts[3]);
                    var noCwnd = parts[4] == "1";
                    _session.Transport.SetNoDelay(noDelay, interval, fastResend, noCwnd);
                    _session?.Config.NoDelay = noDelay;
                    _session?.Config.IntervalMs = interval;
                    _session?.Config.FastResend = fastResend;
                    _session?.Config.NoCongestionControl = noCwnd;
                    SyncUiFromConfig();
                    Log($"NoDelay: nd={noDelay} iv={interval} fr={fastResend} nc={noCwnd}");
                    break;

                case "/window":
                    if (parts.Length < 3 || _session?.Transport is null)
                    {
                        Log("Usage: /window <send> <receive>");
                        break;
                    }
                    var sendWin = int.Parse(parts[1]);
                    var recvWin = int.Parse(parts[2]);
                    _session.Transport.SetWindowSize(sendWin, recvWin);
                    _session?.Config.SendWindow = sendWin;
                    _session?.Config.ReceiveWindow = recvWin;
                    SyncUiFromConfig();
                    Log($"Window: snd={sendWin} rcv={recvWin}");
                    break;

                case "/mtu":
                    if (parts.Length < 2 || _session?.Transport is null)
                    {
                        Log("Usage: /mtu <value>");
                        break;
                    }
                    var mtu = int.Parse(parts[1]);
                    _session.Transport.SetMtu(mtu);
                    _session?.Config.Mtu = mtu;
                    SyncUiFromConfig();
                    Log($"MTU: {mtu}");
                    break;

                case "/interval":
                    if (parts.Length < 2 || _session?.Transport is null)
                    {
                        Log("Usage: /interval <ms>");
                        break;
                    }
                    var intervalMs = int.Parse(parts[1]);
                    _session.Transport.SetInterval(intervalMs);
                    _session?.Config.IntervalMs = intervalMs;
                    SyncUiFromConfig();
                    Log($"Interval: {intervalMs}ms");
                    break;

                case "/direction":
                    if (parts.Length < 2)
                    {
                        Log("Usage: /direction <both|send|recv>");
                        break;
                    }
                    var direction = parts[1].ToLowerInvariant() switch
                    {
                        "send" => KcpConfig.ECommunicationDirection.SendOnly,
                        "recv" => KcpConfig.ECommunicationDirection.ReceiveOnly,
                        _ => KcpConfig.ECommunicationDirection.Bidirectional
                    };
                    _session?.Config.Direction = direction;

                    if (_directionSelector is not null)
                        _directionSelector.Value = direction;

                    if (_session is not null && _session.IsRunning)
                        await _session.RestartReceiveLoopAsync();

                    Log($"Direction: {direction}");
                    break;

                case "/help":
                    Log("/send <text>  /hex <hex>  /random <size>  /flood <count> <size>  /stream <size>");
                    Log("/flush  /nodelay <0|1> <iv> <fr> <nc>  /window <send> <recv>  /mtu <value>  /interval <ms>");
                    Log("/direction <both|send|recv>");
                    break;

                default:
                    Log($"Unknown: {command}. Try /help");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"Command error: {ex.Message}");
        }
    }

    #endregion

    #region Log & Stats

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_pendingLogsLock)
        {
            _pendingLogs.Add(line);
        }
    }

    private static void FlushPendingLogs()
    {
        List<string> toFlush;
        lock (_pendingLogsLock)
        {
            if (_pendingLogs.Count == 0)
                return;
            toFlush = new List<string>(_pendingLogs);
            _pendingLogs.Clear();
        }

        _application?.Invoke(() =>
        {
            if (_logView is null)
                return;

            lock (_logLock)
            {
                foreach (var line in toFlush)
                {
                    _logLineCount++;

                    if (5000 < _logLineCount)
                    {
                        var currentText = _logView.Text.ToString() ?? "";
                        var lines = currentText.Split('\n');
                        if (3000 < lines.Length)
                        {
                            _logView.Text = string.Join('\n', lines[^2000..]);
                            _logLineCount = 2000;
                        }
                    }

                    _logView.Text += "\n" + line;
                }
            }
        });
    }

    private static void RefreshStats()
    {
        if (_statsLabel is null)
            return;

        var statsText = _session?.BuildStatisticsString() ?? "No session";
        _application?.Invoke(() => _statsLabel.Text = statsText);
    }

    #endregion

    #region UI Helpers

    private static Label CreateLabel(string text, int x, object y)
        => new()
        {
            Text = text,
            X = x,
            Y = y is int intValue ? intValue : (Pos)y
        };

    private static TextField CreateTextField(string text, int x, int y, int width)
        => new()
        {
            Text = text,
            X = x,
            Y = y,
            Width = width,
            Height = 1
        };

    private static string GetTextFieldValue(TextField? field, string defaultValue)
        => field?.Text.ToString()?.Trim() ?? defaultValue;

    private static int GetTextFieldInt(TextField? field, int defaultValue)
        => int.TryParse(field?.Text.ToString()?.Trim(), out var value) ? value : defaultValue;

    private static uint GetTextFieldUint(TextField? field, uint defaultValue)
        => uint.TryParse(field?.Text.ToString()?.Trim(), out var value) ? value : defaultValue;

    private static bool IsCheckBoxChecked(CheckBox? checkBox)
        => checkBox?.Value == CheckState.Checked;

    #endregion
}
