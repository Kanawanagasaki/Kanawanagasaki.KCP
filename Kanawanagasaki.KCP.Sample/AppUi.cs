namespace Kanawanagasaki.KCP.Sample;

public static class AppUi
{
    private static KcpSession? _session;
    private static bool _running;
    private static bool _isCompactLayout;

    private const int FullLayoutMinWidth = 96;
    private const int FullLayoutMinHeight = 26;
    private const int TopSectionHeight = 14;
    private const int ConfigFrameWidth = 64;

    private static readonly List<Focusable> _focusables = [];
    private static int _focusIndex;

    private static readonly List<string> _logLines = [];
    private static readonly Lock _logLock = new();
    private static int _logLineCount;
    private static readonly List<string> _pendingLogs = [];
    private static readonly Lock _pendingLogsLock = new();

    private static int _compactTabIndex;

    private static string _hintText = "Type / for commands, or just type text to send";

    private static bool _cursorVisible = true;
    private static long _lastCursorToggle;

    private static int _lastConsoleWidth;
    private static int _lastConsoleHeight;

    private static InputField? _fieldLocalIp, _fieldLocalPort, _fieldConversationId;
    private static InputField? _fieldRemoteIp, _fieldRemotePort;
    private static Button? _btnNetApply;
    private static CheckBox? _checkNoDelay, _checkNoCongestion, _checkStreamMode;
    private static InputField? _fieldInterval, _fieldFastResend, _fieldMtu;
    private static InputField? _fieldSendWindow, _fieldReceiveWindow;
    private static Button? _btnKcpApply;
    private static InputField? _fieldBufferSize;
    private static OptionSelector? _directionSelector;
    private static Button? _btnCommApply;

    private static TabSelectorElement? _tabSelector;
    private static LogViewElement? _logView;
    private static CommandInputElement? _commandInput;

    private static Button? _btnQuit, _btnStart, _btnStop, _btnClearLog;

    private static string _statsText = "Start the transport to see statistics.";

    private static readonly List<string> _commandHistory = [];
    private static int _commandHistoryIndex = -1;
    private static string _currentSavedInput = "";

    private static int _unreadLogCount;

    private static readonly (string Command, string Hint, string Usage)[] CommandHints =
    [
        ("/send", "Send text message", "/send <text>"),
        ("/hex", "Send hex data", "/hex <hex>"),
        ("/random", "Send random bytes", "/random <size>"),
        ("/stream", "Stream data", "/stream <size>"),
        ("/flush", "Flush KCP buffer", "/flush"),
        ("/nodelay", "Set nodelay params", "/nodelay <0|1> <interval> <fastResend> <noCwnd:0|1>"),
        ("/window", "Set window sizes", "/window <send> <receive>"),
        ("/mtu", "Set MTU", "/mtu <value>"),
        ("/interval", "Set interval", "/interval <ms>"),
        ("/direction", "Set direction", "/direction <both|send|recv>"),
        ("/help", "Show help", "/help"),
    ];



    private abstract class Focusable
    {
        public Func<bool>? VisibilityCheck;
        public bool IsVisible => VisibilityCheck?.Invoke() ?? true;
        public abstract void Draw(bool focused);
        public abstract bool HandleKey(ConsoleKeyInfo key);
    }

    private class InputField : Focusable
    {
        public int X, Y, Width;
        public string Text = "";
        public int CursorPos;
        private int _viewStart;

        private void EnsureCursorVisible()
        {
            if (Text.Length < Width)
            {
                _viewStart = 0;
                return;
            }

            if (CursorPos < _viewStart)
                _viewStart = CursorPos;
            else if (_viewStart + Width <= CursorPos)
                _viewStart = CursorPos - Width + 1;

            _viewStart = Math.Max(0, Math.Min(_viewStart, Text.Length - Width + 1));
        }

        public override void Draw(bool focused)
        {
            EnsureCursorVisible();
            var visibleText = _viewStart < Text.Length ? Text[_viewStart..] : "";
            var display = PadField(visibleText, Width);
            if (focused)
            {
                var fg = ConsoleColor.White;
                var bg = ConsoleColor.DarkCyan;
                ConsoleBuffer.Write(X, Y, display, fg, bg);

                var screenCursor = CursorPos - _viewStart;
                if (_cursorVisible && 0 <= screenCursor && screenCursor < Width)
                    ConsoleBuffer.Write(X + screenCursor, Y, "|", ConsoleColor.White, bg);
            }
            else
            {
                ConsoleBuffer.Write(X, Y, display, ConsoleColor.Gray, ConsoleColor.Black);
            }
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.LeftArrow:
                    if (0 < CursorPos)
                        CursorPos--;
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.RightArrow:
                    if (CursorPos < Text.Length)
                        CursorPos++;
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.Home:
                    CursorPos = 0;
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.End:
                    CursorPos = Text.Length;
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.Backspace:
                    if (0 < CursorPos)
                    {
                        Text = Text[..(CursorPos - 1)] + Text[CursorPos..];
                        CursorPos--;
                    }
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.Delete:
                    if (CursorPos < Text.Length)
                        Text = Text[..CursorPos] + Text[(CursorPos + 1)..];
                    ResetCursorBlink();
                    return true;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        if (CursorPos < Text.Length)
                        {
                            Text = Text[..CursorPos] + key.KeyChar + Text[(CursorPos + 1)..];
                            CursorPos++;
                        }
                        else
                        {
                            Text += key.KeyChar;
                            CursorPos++;
                        }
                    }
                    ResetCursorBlink();
                    return key.KeyChar != '\0';
            }
        }

        private static string PadField(string text, int width)
        {
            if (width < text.Length)
                return text[..width];
            return text.PadRight(width);
        }
    }

    private class CheckBox : Focusable
    {
        public int X, Y;
        public string Label = "";
        public bool IsChecked;

        public override void Draw(bool focused)
        {
            var mark = IsChecked ? "V" : " ";
            var text = $"[{mark}]{Label}";
            if (focused)
                ConsoleBuffer.Write(X, Y, text, ConsoleColor.Yellow, ConsoleColor.DarkCyan);
            else
                ConsoleBuffer.Write(X, Y, text, ConsoleColor.Gray, ConsoleColor.Black);
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.Spacebar || key.Key == ConsoleKey.Enter)
            {
                IsChecked = !IsChecked;
                return true;
            }
            return false;
        }
    }

    private class Button : Focusable
    {
        public int X, Y;
        public string Label = "";
        public Action? OnActivate;

        public override void Draw(bool focused)
        {
            var text = $"[{Label}]";
            var fg = focused ? ConsoleColor.Black : ConsoleColor.White;
            var bg = focused ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
            ConsoleBuffer.Write(X, Y, text, fg, bg);
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar)
            {
                OnActivate?.Invoke();
                return true;
            }
            return false;
        }
    }

    private class OptionSelector : Focusable
    {
        public int X, Y, Width;
        public string[] Labels = [];
        public int SelectedIndex;

        public KcpConfig.ECommunicationDirection Value
        {
            get => (KcpConfig.ECommunicationDirection)SelectedIndex;
            set => SelectedIndex = (int)value;
        }

        public override void Draw(bool focused)
        {
            var option = Labels[SelectedIndex];
            var text = $"< {option} >";
            var padded = Width <= text.Length ? text[..Width] : text.PadRight(Width);
            if (focused)
                ConsoleBuffer.Write(X, Y, padded, ConsoleColor.White, ConsoleColor.DarkCyan);
            else
                ConsoleBuffer.Write(X, Y, padded, ConsoleColor.Gray, ConsoleColor.Black);
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (0 < SelectedIndex)
                    SelectedIndex--;
                return true;
            }
            if (key.Key == ConsoleKey.RightArrow)
            {
                if (SelectedIndex < Labels.Length - 1)
                    SelectedIndex++;
                return true;
            }
            return false;
        }
    }

    private class LogViewElement : Focusable
    {
        public int X, Y, Width, Height;
        public int ScrollOffset;

        public override void Draw(bool focused)
        {
            ClampScrollOffset();

            var fg = focused ? ConsoleColor.Cyan : ConsoleColor.Gray;
            ConsoleBuffer.DrawBox(X, Y, Width, Height, fg);
            ConsoleBuffer.Write(X + 2, Y, " Log ", fg);

            var contentX = X + 1;
            var contentY = Y + 1;
            var contentWidth = Width - 2;
            var contentHeight = Height - 2;

            List<string> lines;
            lock (_logLock)
                lines = [.. _logLines];

            var totalLines = lines.Count;
            var visibleLines = contentHeight;
            var startLine = totalLines <= visibleLines ? 0 : Math.Max(0, totalLines - visibleLines - ScrollOffset);
            var endLine = Math.Min(totalLines, startLine + visibleLines);

            for (int i = 0; i < contentHeight; i++)
            {
                var lineIndex = startLine + i;
                string line;
                if (lineIndex < endLine && lineIndex < totalLines)
                {
                    var rawLine = lines[lineIndex];
                    line = contentWidth < rawLine.Length ? rawLine[..contentWidth] : rawLine.PadRight(contentWidth);
                }
                else
                {
                    line = new string(' ', contentWidth);
                }

                var lineFg = ConsoleColor.Gray;
                if (line.Contains("[SENT]") || line.Contains("[STREAM TX]"))
                    lineFg = ConsoleColor.Green;
                else if (line.Contains("[RECV]") || line.Contains("[STREAM RX]"))
                    lineFg = ConsoleColor.Cyan;
                else if (line.Contains("ERR") || line.Contains("FAIL"))
                    lineFg = ConsoleColor.Red;
                else if (line.Contains("STARTED"))
                    lineFg = ConsoleColor.Green;
                else if (line.Contains("STOPPED"))
                    lineFg = ConsoleColor.Yellow;

                ConsoleBuffer.Write(contentX, contentY + i, line, lineFg, ConsoleColor.Black);
            }

            if (0 < _unreadLogCount && 0 < ScrollOffset)
            {
                var counterText = $" {_unreadLogCount} new ";
                var counterX = X + Width - counterText.Length - 2;
                var counterY = Y + Height - 1;
                if (X + 6 < counterX)
                    ConsoleBuffer.Write(counterX, counterY, counterText, ConsoleColor.Black, ConsoleColor.Yellow);
            }
        }

        public void ClampScrollOffset()
        {
            int visibleLines = Height - 2;
            int maxScroll;
            lock (_logLock)
                maxScroll = Math.Max(0, _logLines.Count - visibleLines);
            if (maxScroll < ScrollOffset)
                ScrollOffset = maxScroll;
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            int visibleLines = Height - 2;
            int maxScroll;
            lock (_logLock)
                maxScroll = Math.Max(0, _logLines.Count - visibleLines);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (ScrollOffset < maxScroll)
                        ScrollOffset++;
                    return true;
                case ConsoleKey.DownArrow:
                    if (0 < ScrollOffset)
                        ScrollOffset--;
                    if (ScrollOffset == 0)
                        _unreadLogCount = 0;
                    return true;
                case ConsoleKey.PageUp:
                    ScrollOffset = Math.Min(ScrollOffset + visibleLines, maxScroll);
                    return true;
                case ConsoleKey.PageDown:
                    ScrollOffset = Math.Max(ScrollOffset - visibleLines, 0);
                    if (ScrollOffset == 0)
                        _unreadLogCount = 0;
                    return true;
                case ConsoleKey.Home:
                    ScrollOffset = maxScroll;
                    return true;
                case ConsoleKey.End:
                    ScrollOffset = 0;
                    _unreadLogCount = 0;
                    return true;
            }
            return false;
        }
    }

    private class CommandInputElement : Focusable
    {
        public int X, Y, Width;
        public string Text = "";
        public int CursorPos;
        public Action? OnSubmit;
        private int _viewStart;

        public int FieldWidth => Width - 12;

        private void EnsureCursorVisible()
        {
            var fw = FieldWidth;
            if (Text.Length < fw)
            {
                _viewStart = 0;
                return;
            }

            if (CursorPos < _viewStart)
                _viewStart = CursorPos;
            else if (_viewStart + fw <= CursorPos)
                _viewStart = CursorPos - fw + 1;

            _viewStart = Math.Max(0, Math.Min(_viewStart, Text.Length - fw + 1));
        }

        public override void Draw(bool focused)
        {
            EnsureCursorVisible();

            ConsoleBuffer.Write(X, Y, "Cmd> ", ConsoleColor.Yellow, ConsoleColor.Black);

            var fieldX = X + 5;
            var fw = FieldWidth;
            var visibleText = _viewStart < Text.Length ? Text[_viewStart..] : "";
            var display = fw <= visibleText.Length ? visibleText[..fw] : visibleText.PadRight(fw);

            if (focused)
            {
                var fg = ConsoleColor.White;
                var bg = ConsoleColor.DarkCyan;
                ConsoleBuffer.Write(fieldX, Y, display, fg, bg);

                var screenCursor = CursorPos - _viewStart;
                if (_cursorVisible && 0 <= screenCursor && screenCursor < fw)
                    ConsoleBuffer.Write(fieldX + screenCursor, Y, "|", fg, bg);
            }
            else
            {
                ConsoleBuffer.Write(fieldX, Y, display, ConsoleColor.Gray, ConsoleColor.Black);
            }

            var sendX = X + Width - 7;
            var sendFg = focused ? ConsoleColor.Black : ConsoleColor.White;
            var sendBg = focused ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
            ConsoleBuffer.Write(sendX, Y, "[Send]", sendFg, sendBg);
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.Enter)
            {
                OnSubmit?.Invoke();
                return true;
            }

            var fw = FieldWidth;

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (0 < _commandHistory.Count)
                    {
                        if (_commandHistoryIndex < 0)
                        {
                            _currentSavedInput = Text;
                            _commandHistoryIndex = _commandHistory.Count - 1;
                        }
                        else if (0 < _commandHistoryIndex)
                        {
                            _commandHistoryIndex--;
                        }
                        Text = _commandHistory[_commandHistoryIndex];
                        CursorPos = Text.Length;
                    }
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.DownArrow:
                    if (0 <= _commandHistoryIndex)
                    {
                        _commandHistoryIndex++;
                        if (_commandHistory.Count <= _commandHistoryIndex)
                        {
                            _commandHistoryIndex = -1;
                            Text = _currentSavedInput;
                        }
                        else
                        {
                            Text = _commandHistory[_commandHistoryIndex];
                        }
                        CursorPos = Text.Length;
                    }
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.LeftArrow:
                    _commandHistoryIndex = -1;
                    if (0 < CursorPos) CursorPos--;
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.RightArrow:
                    _commandHistoryIndex = -1;
                    if (CursorPos < Text.Length) CursorPos++;
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.Home:
                    _commandHistoryIndex = -1;
                    CursorPos = 0;
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.End:
                    _commandHistoryIndex = -1;
                    CursorPos = Text.Length;
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.Backspace:
                    _commandHistoryIndex = -1;
                    if (0 < CursorPos)
                    {
                        Text = Text[..(CursorPos - 1)] + Text[CursorPos..];
                        CursorPos--;
                    }
                    ResetCursorBlink();
                    return true;
                case ConsoleKey.Delete:
                    _commandHistoryIndex = -1;
                    if (CursorPos < Text.Length)
                        Text = Text[..CursorPos] + Text[(CursorPos + 1)..];
                    ResetCursorBlink();
                    return true;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        _commandHistoryIndex = -1;
                        if (CursorPos < Text.Length)
                        {
                            Text = Text[..CursorPos] + key.KeyChar + Text[(CursorPos + 1)..];
                            CursorPos++;
                        }
                        else
                        {
                            Text += key.KeyChar;
                            CursorPos++;
                        }
                    }
                    ResetCursorBlink();
                    return key.KeyChar != '\0';
            }
        }
    }

    private class TabSelectorElement : Focusable
    {
        public int X, Y;
        public string[] Labels = [];
        public int SelectedIndex;

        public override void Draw(bool focused)
        {
            int x = X;
            for (int i = 0; i < Labels.Length; i++)
            {
                var label = $" {Labels[i]} ";
                if (i == SelectedIndex)
                {
                    var fg = focused ? ConsoleColor.Black : ConsoleColor.White;
                    var bg = focused ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
                    ConsoleBuffer.Write(x, Y, label, fg, bg);
                }
                else
                {
                    ConsoleBuffer.Write(x, Y, label, ConsoleColor.Gray, ConsoleColor.Black);
                }
                x += label.Length + 1;
            }
        }

        public override bool HandleKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (0 < SelectedIndex)
                {
                    SelectedIndex--;
                    _compactTabIndex = SelectedIndex;
                }
                return true;
            }
            if (key.Key == ConsoleKey.RightArrow)
            {
                if (SelectedIndex < Labels.Length - 1)
                {
                    SelectedIndex++;
                    _compactTabIndex = SelectedIndex;
                }
                return true;
            }
            return false;
        }
    }



    public static async Task RunAsync(KcpSession session)
    {
        _session = session;
        _session.OnLog += Log;

        ConsoleBuffer.Initialize();
        ConsoleBuffer.Clear();

        _isCompactLayout = Console.WindowWidth < FullLayoutMinWidth || Console.WindowHeight < FullLayoutMinHeight;
        _lastConsoleWidth = Console.WindowWidth;
        _lastConsoleHeight = Console.WindowHeight;
        _lastCursorToggle = Environment.TickCount64;
        BuildLayout();

        _running = true;
        try
        {
            while (_running)
            {
                ConsoleBuffer.CheckResize();

                var currentWidth = Console.WindowWidth;
                var currentHeight = Console.WindowHeight;
                if (currentWidth != _lastConsoleWidth || currentHeight != _lastConsoleHeight)
                {
                    _lastConsoleWidth = currentWidth;
                    _lastConsoleHeight = currentHeight;

                    var newCompact = currentWidth < FullLayoutMinWidth || currentHeight < FullLayoutMinHeight;
                    if (newCompact != _isCompactLayout)
                        _isCompactLayout = newCompact;

                    RebuildLayout();
                }

                var now = Environment.TickCount64;
                if (530 < now - _lastCursorToggle)
                {
                    _cursorVisible = !_cursorVisible;
                    _lastCursorToggle = now;
                }

                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    HandleKey(key);
                }

                FlushPendingLogs();
                RefreshStats();
                try
                {
                    RenderAll();
                    ConsoleBuffer.Render();
                }
                catch (Exception)
                {
                    ConsoleBuffer.CheckResize();
                }

                await Task.Delay(50);
            }
        }
        finally
        {
            ConsoleBuffer.ResetColors();
            Console.CursorVisible = true;
        }

        if (_session is not null && _session.IsRunning)
            await _session.StopAsync();
    }

    private static void ResetCursorBlink()
    {
        _cursorVisible = true;
        _lastCursorToggle = Environment.TickCount64;
    }



    private static void HandleKey(ConsoleKeyInfo key)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            switch (key.Key)
            {
                case ConsoleKey.Q:
                    _running = false;
                    return;
                case ConsoleKey.R:
                    _ = OnStartTransportAsync();
                    return;
                case ConsoleKey.T:
                    _ = OnStopTransportAsync();
                    return;
                case ConsoleKey.L:
                    ClearLog();
                    return;
            }
        }

        if (key.Key == ConsoleKey.Tab)
        {
            var direction = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1;
            NavigateFocus(direction);
            return;
        }

        if (0 <= _focusIndex && _focusIndex < _focusables.Count)
        {
            var focused = _focusables[_focusIndex];
            if (focused.IsVisible)
                focused.HandleKey(key);
        }

        if (_commandInput is not null)
            UpdateCommandHint(_commandInput.Text);
    }

    private static void NavigateFocus(int direction)
    {
        if (_focusables.Count == 0)
            return;

        var startIndex = _focusIndex;
        var tries = _focusables.Count;

        do
        {
            _focusIndex = (_focusIndex + direction + _focusables.Count) % _focusables.Count;
            if (_focusables[_focusIndex].IsVisible)
                return;
            tries--;
        }
        while (0 < tries && _focusIndex != startIndex);
    }



    private static void BuildLayout()
    {
        _focusables.Clear();

        if (_isCompactLayout)
        {
            _tabSelector = new TabSelectorElement
            {
                X = 1,
                Y = 1,
                Labels = ["Config", "Statistics"],
                SelectedIndex = _compactTabIndex,
                VisibilityCheck = () => _isCompactLayout
            };
            _focusables.Add(_tabSelector);
        }
        else
        {
            _tabSelector = null;
        }

        var configVisible = new Func<bool>(() => !_isCompactLayout || _compactTabIndex == 0);

        int baseX, baseY;
        if (_isCompactLayout)
        {
            baseX = 1;
            baseY = 3;
        }
        else
        {
            baseX = 1;
            baseY = 2;
        }

        int row = 0;

        row++;

        _fieldLocalIp = new InputField
        {
            X = baseX + 8,
            Y = baseY + row,
            Width = 15,
            Text = _session?.Config.LocalIp ?? "0.0.0.0",
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldLocalIp);

        _fieldLocalPort = new InputField
        {
            X = baseX + 30,
            Y = baseY + row,
            Width = 6,
            Text = (_session?.Config.LocalPort ?? 10001).ToString(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldLocalPort);

        _fieldConversationId = new InputField
        {
            X = baseX + 43,
            Y = baseY + row,
            Width = 6,
            Text = (_session?.Config.ConversationId ?? 1).ToString(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldConversationId);

        row++;

        _fieldRemoteIp = new InputField
        {
            X = baseX + 8,
            Y = baseY + row,
            Width = 15,
            Text = _session?.Config.RemoteIp ?? "127.0.0.1",
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldRemoteIp);

        _fieldRemotePort = new InputField
        {
            X = baseX + 30,
            Y = baseY + row,
            Width = 6,
            Text = (_session?.Config.RemotePort ?? 10002).ToString(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldRemotePort);

        _btnNetApply = new Button
        {
            X = baseX + 38,
            Y = baseY + row,
            Label = "Net+",
            OnActivate = ApplyNetworkSettings,
            VisibilityCheck = configVisible
        };
        _focusables.Add(_btnNetApply);

        row++;
        row++;

        _checkNoDelay = new CheckBox
        {
            X = baseX + 1,
            Y = baseY + row,
            Label = "ND",
            IsChecked = _session?.Config.NoDelay == true,
            VisibilityCheck = configVisible
        };
        _focusables.Add(_checkNoDelay);

        _checkNoCongestion = new CheckBox
        {
            X = baseX + 14,
            Y = baseY + row,
            Label = "NoCwnd",
            IsChecked = _session?.Config.NoCongestionControl == true,
            VisibilityCheck = configVisible
        };
        _focusables.Add(_checkNoCongestion);

        _checkStreamMode = new CheckBox
        {
            X = baseX + 33,
            Y = baseY + row,
            Label = "Stream",
            IsChecked = _session?.Config.StreamMode == true,
            VisibilityCheck = configVisible
        };
        _focusables.Add(_checkStreamMode);

        row++;

        _fieldInterval = new InputField
        {
            X = baseX + 4,
            Y = baseY + row,
            Width = 6,
            Text = (_session?.Config.IntervalMs ?? 100).ToString(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldInterval);

        _fieldFastResend = new InputField
        {
            X = baseX + 16,
            Y = baseY + row,
            Width = 4,
            Text = (_session?.Config.FastResend ?? 0).ToString(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldFastResend);

        _fieldMtu = new InputField
        {
            X = baseX + 27,
            Y = baseY + row,
            Width = 6,
            Text = (_session?.Config.Mtu ?? 1400).ToString(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldMtu);

        _fieldSendWindow = new InputField
        {
            X = baseX + 41,
            Y = baseY + row,
            Width = 5,
            Text = (_session?.Config.SendWindow ?? 32).ToString(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldSendWindow);

        _fieldReceiveWindow = new InputField
        {
            X = baseX + 53,
            Y = baseY + row,
            Width = 5,
            Text = (_session?.Config.ReceiveWindow ?? 128).ToString(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldReceiveWindow);

        row++;

        _btnKcpApply = new Button
        {
            X = baseX + 1,
            Y = baseY + row,
            Label = "KCP+",
            OnActivate = ApplyKcpSettings,
            VisibilityCheck = configVisible
        };
        _focusables.Add(_btnKcpApply);

        row++;
        row++;

        _fieldBufferSize = new InputField
        {
            X = baseX + 5,
            Y = baseY + row,
            Width = 6,
            Text = (_session?.Config.BufferSize ?? 4096).ToString(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_fieldBufferSize);

        _directionSelector = new OptionSelector
        {
            X = baseX + 18,
            Y = baseY + row,
            Width = 15,
            Labels = ["Both", "Send", "Recv"],
            SelectedIndex = (int)(_session?.Config.Direction ?? KcpConfig.ECommunicationDirection.Bidirectional),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_directionSelector);

        _btnCommApply = new Button
        {
            X = baseX + 35,
            Y = baseY + row,
            Label = "Comm+",
            OnActivate = () => _ = ApplyCommunicationSettingsAsync(),
            VisibilityCheck = configVisible
        };
        _focusables.Add(_btnCommApply);

        var logY = TopSectionHeight + 1;
        var logHeight = Console.WindowHeight - logY - 3;
        if (logHeight < 3) logHeight = 3;

        _logView = new LogViewElement
        {
            X = 0,
            Y = logY,
            Width = Console.WindowWidth,
            Height = logHeight
        };
        _focusables.Add(_logView);

        _commandInput = new CommandInputElement
        {
            X = 0,
            Y = Console.WindowHeight - 3,
            Width = Console.WindowWidth,
            OnSubmit = OnCommandSubmit
        };
        _focusables.Add(_commandInput);

        var statusY = Console.WindowHeight - 1;
        var statusX = 1;

        _btnQuit = new Button
        {
            X = statusX,
            Y = statusY,
            Label = "Quit ^Q",
            OnActivate = () => _running = false
        };
        _focusables.Add(_btnQuit);
        statusX += 11;

        _btnStart = new Button
        {
            X = statusX,
            Y = statusY,
            Label = "Start ^R",
            OnActivate = () => _ = OnStartTransportAsync()
        };
        _focusables.Add(_btnStart);
        statusX += 12;

        _btnStop = new Button
        {
            X = statusX,
            Y = statusY,
            Label = "Stop ^T",
            OnActivate = () => _ = OnStopTransportAsync()
        };
        _focusables.Add(_btnStop);
        statusX += 11;

        _btnClearLog = new Button
        {
            X = statusX,
            Y = statusY,
            Label = "Clear Log ^L",
            OnActivate = ClearLog
        };
        _focusables.Add(_btnClearLog);

        _focusIndex = 0;
        for (int i = 0; i < _focusables.Count; i++)
        {
            if (_focusables[i].IsVisible)
            {
                _focusIndex = i;
                break;
            }
        }
    }

    private static void RebuildLayout()
    {
        var savedLocalIp = _fieldLocalIp?.Text ?? _session?.Config.LocalIp ?? "";
        var savedLocalPort = _fieldLocalPort?.Text ?? _session?.Config.LocalPort.ToString() ?? "";
        var savedConvId = _fieldConversationId?.Text ?? _session?.Config.ConversationId.ToString() ?? "";
        var savedRemoteIp = _fieldRemoteIp?.Text ?? _session?.Config.RemoteIp ?? "";
        var savedRemotePort = _fieldRemotePort?.Text ?? _session?.Config.RemotePort.ToString() ?? "";
        var savedInterval = _fieldInterval?.Text ?? _session?.Config.IntervalMs.ToString() ?? "";
        var savedFastResend = _fieldFastResend?.Text ?? _session?.Config.FastResend.ToString() ?? "";
        var savedMtu = _fieldMtu?.Text ?? _session?.Config.Mtu.ToString() ?? "";
        var savedSendWindow = _fieldSendWindow?.Text ?? _session?.Config.SendWindow.ToString() ?? "";
        var savedRecvWindow = _fieldReceiveWindow?.Text ?? _session?.Config.ReceiveWindow.ToString() ?? "";
        var savedBufferSize = _fieldBufferSize?.Text ?? _session?.Config.BufferSize.ToString() ?? "";
        var savedNoDelay = _checkNoDelay?.IsChecked ?? _session?.Config.NoDelay == true;
        var savedNoCwnd = _checkNoCongestion?.IsChecked ?? _session?.Config.NoCongestionControl == true;
        var savedStream = _checkStreamMode?.IsChecked ?? _session?.Config.StreamMode == true;
        var savedDirection = _directionSelector?.SelectedIndex ?? (int)(_session?.Config.Direction ?? KcpConfig.ECommunicationDirection.Bidirectional);
        var savedCommand = _commandInput?.Text ?? "";
        var savedCommandCursor = _commandInput?.CursorPos ?? 0;
        var savedLogScroll = _logView?.ScrollOffset ?? 0;
        var savedFocusIndex = _focusIndex;

        BuildLayout();

        _fieldLocalIp?.Text = savedLocalIp;
        _fieldLocalPort?.Text = savedLocalPort;
        _fieldConversationId?.Text = savedConvId;
        _fieldRemoteIp?.Text = savedRemoteIp;
        _fieldRemotePort?.Text = savedRemotePort;
        _fieldInterval?.Text = savedInterval;
        _fieldFastResend?.Text = savedFastResend;
        _fieldMtu?.Text = savedMtu;
        _fieldSendWindow?.Text = savedSendWindow;
        _fieldReceiveWindow?.Text = savedRecvWindow;
        _fieldBufferSize?.Text = savedBufferSize;
        _checkNoDelay?.IsChecked = savedNoDelay;
        _checkNoCongestion?.IsChecked = savedNoCwnd;
        _checkStreamMode?.IsChecked = savedStream;
        _directionSelector?.SelectedIndex = savedDirection;
        _commandInput?.Text = savedCommand;
        _commandInput?.CursorPos = Math.Min(savedCommandCursor, savedCommand.Length);
        _logView?.ScrollOffset = savedLogScroll;
        if (savedFocusIndex < _focusables.Count)
            _focusIndex = savedFocusIndex;
    }



    private static void RenderAll()
    {
        var w = ConsoleBuffer.Width;
        var h = ConsoleBuffer.Height;

        var title = " KCP Sample";
        ConsoleBuffer.Write(0, 0, title.PadRight(w), ConsoleColor.White, ConsoleColor.DarkBlue);

        if (_isCompactLayout)
            RenderCompactLayout(w, h);
        else
            RenderFullLayout(w, h);

        var hintText = w < _hintText.Length ? _hintText[..w] : _hintText.PadRight(w);
        ConsoleBuffer.Write(0, h - 2, hintText, ConsoleColor.DarkCyan, ConsoleColor.Black);

        ConsoleBuffer.FillRect(0, h - 1, w, 1, ' ', ConsoleColor.White, ConsoleColor.DarkGray);

        _btnQuit?.Draw(_focusables.IndexOf(_btnQuit) == _focusIndex);
        _btnStart?.Draw(_focusables.IndexOf(_btnStart) == _focusIndex);
        _btnStop?.Draw(_focusables.IndexOf(_btnStop) == _focusIndex);
        _btnClearLog?.Draw(_focusables.IndexOf(_btnClearLog) == _focusIndex);
    }

    private static void RenderFullLayout(int w, int h)
    {
        ConsoleBuffer.DrawBox(0, 1, ConfigFrameWidth, TopSectionHeight, ConsoleColor.Gray);
        ConsoleBuffer.Write(2, 1, " Config ", ConsoleColor.Gray);

        var baseX = 1;
        var baseY = 2;

        ConsoleBuffer.Write(baseX + 1, baseY, "Network", ConsoleColor.Cyan);
        ConsoleBuffer.Write(baseX + 1, baseY + 1, "Local:", ConsoleColor.Gray);
        ConsoleBuffer.Write(baseX + 25, baseY + 1, "Port:", ConsoleColor.Gray);
        ConsoleBuffer.Write(baseX + 38, baseY + 1, "Conv:", ConsoleColor.Gray);
        ConsoleBuffer.Write(baseX + 1, baseY + 2, "Remote:", ConsoleColor.Gray);
        ConsoleBuffer.Write(baseX + 25, baseY + 2, "Port:", ConsoleColor.Gray);

        ConsoleBuffer.Write(baseX + 1, baseY + 3, "KCP", ConsoleColor.Cyan);

        ConsoleBuffer.Write(baseX + 1, baseY + 5, "Iv:", ConsoleColor.Gray);
        ConsoleBuffer.Write(baseX + 13, baseY + 5, "FR:", ConsoleColor.Gray);
        ConsoleBuffer.Write(baseX + 23, baseY + 5, "MTU:", ConsoleColor.Gray);
        ConsoleBuffer.Write(baseX + 36, baseY + 5, "SndW:", ConsoleColor.Gray);
        ConsoleBuffer.Write(baseX + 48, baseY + 5, "RcvW:", ConsoleColor.Gray);

        ConsoleBuffer.Write(baseX + 1, baseY + 7, "Comm", ConsoleColor.Cyan);
        ConsoleBuffer.Write(baseX + 1, baseY + 8, "Buf:", ConsoleColor.Gray);
        ConsoleBuffer.Write(baseX + 14, baseY + 8, "Dir:", ConsoleColor.Gray);

        foreach (var f in _focusables)
        {
            if (f.IsVisible && f is not LogViewElement and not CommandInputElement and not TabSelectorElement
                && f != _btnQuit && f != _btnStart && f != _btnStop && f != _btnClearLog)
                f.Draw(_focusables[_focusIndex] == f);
        }

        var statsW = w - ConfigFrameWidth;
        if (2 < statsW)
        {
            ConsoleBuffer.DrawBox(ConfigFrameWidth, 1, statsW, TopSectionHeight, ConsoleColor.Gray);
            ConsoleBuffer.Write(ConfigFrameWidth + 2, 1, " Statistics ", ConsoleColor.Gray);

            var statsLines = _statsText.Split('\n');
            var contentX = ConfigFrameWidth + 1;
            var contentY = 2;
            var contentW = statsW - 2;
            var contentH = TopSectionHeight - 2;

            for (int i = 0; i < contentH; i++)
            {
                string line;
                if (i < statsLines.Length)
                {
                    var raw = statsLines[i].TrimEnd();
                    line = contentW < raw.Length ? raw[..contentW] : raw.PadRight(contentW);
                }
                else
                {
                    line = new string(' ', contentW);
                }
                ConsoleBuffer.Write(contentX, contentY + i, line, ConsoleColor.Gray);
            }
        }

        if (_logView is not null)
            _logView.Draw(_focusables[_focusIndex] == _logView);

        if (_commandInput is not null)
            _commandInput.Draw(_focusables[_focusIndex] == _commandInput);
    }

    private static void RenderCompactLayout(int w, int h)
    {
        if (_tabSelector is not null)
            _tabSelector.Draw(_focusables[_focusIndex] == _tabSelector);

        var tabFrameHeight = TopSectionHeight - 1;
        ConsoleBuffer.DrawBox(0, 2, w, tabFrameHeight, ConsoleColor.Gray);

        if (_compactTabIndex == 0)
        {
            var baseX = 1;
            var baseY = 3;

            ConsoleBuffer.Write(baseX + 1, baseY, "Network", ConsoleColor.Cyan);
            ConsoleBuffer.Write(baseX + 1, baseY + 1, "Local:", ConsoleColor.Gray);
            ConsoleBuffer.Write(baseX + 25, baseY + 1, "Port:", ConsoleColor.Gray);
            ConsoleBuffer.Write(baseX + 38, baseY + 1, "Conv:", ConsoleColor.Gray);
            ConsoleBuffer.Write(baseX + 1, baseY + 2, "Remote:", ConsoleColor.Gray);
            ConsoleBuffer.Write(baseX + 25, baseY + 2, "Port:", ConsoleColor.Gray);

            ConsoleBuffer.Write(baseX + 1, baseY + 3, "KCP", ConsoleColor.Cyan);

            ConsoleBuffer.Write(baseX + 1, baseY + 5, "Iv:", ConsoleColor.Gray);
            ConsoleBuffer.Write(baseX + 13, baseY + 5, "FR:", ConsoleColor.Gray);
            ConsoleBuffer.Write(baseX + 23, baseY + 5, "MTU:", ConsoleColor.Gray);
            ConsoleBuffer.Write(baseX + 36, baseY + 5, "SndW:", ConsoleColor.Gray);
            ConsoleBuffer.Write(baseX + 48, baseY + 5, "RcvW:", ConsoleColor.Gray);

            ConsoleBuffer.Write(baseX + 1, baseY + 7, "Comm", ConsoleColor.Cyan);
            ConsoleBuffer.Write(baseX + 1, baseY + 8, "Buf:", ConsoleColor.Gray);
            ConsoleBuffer.Write(baseX + 14, baseY + 8, "Dir:", ConsoleColor.Gray);

            foreach (var f in _focusables)
            {
                if (f.IsVisible && f is not LogViewElement and not CommandInputElement and not TabSelectorElement
                    && f != _btnQuit && f != _btnStart && f != _btnStop && f != _btnClearLog)
                    f.Draw(_focusables[_focusIndex] == f);
            }
        }
        else
        {
            var contentX = 1;
            var contentY = 3;
            var contentW = w - 2;
            var contentH = tabFrameHeight - 2;

            var statsLines = _statsText.Split('\n');
            for (int i = 0; i < contentH; i++)
            {
                string line;
                if (i < statsLines.Length)
                {
                    var raw = statsLines[i].TrimEnd();
                    line = contentW < raw.Length ? raw[..contentW] : raw.PadRight(contentW);
                }
                else
                {
                    line = new string(' ', contentW);
                }
                ConsoleBuffer.Write(contentX, contentY + i, line, ConsoleColor.Gray);
            }
        }

        if (_logView is not null)
            _logView.Draw(_focusables[_focusIndex] == _logView);

        if (_commandInput is not null)
            _commandInput.Draw(_focusables[_focusIndex] == _commandInput);
    }



    private static void OnCommandSubmit()
    {
        if (_commandInput is null)
            return;

        var input = _commandInput.Text.Trim();
        _commandInput.Text = "";
        _commandInput.CursorPos = 0;

        if (string.IsNullOrEmpty(input))
            return;

        _commandHistory.Add(input);
        if (100 < _commandHistory.Count)
            _commandHistory.RemoveAt(0);
        _commandHistoryIndex = -1;
        _currentSavedInput = "";

        if (!input.StartsWith('/'))
        {
            if (_session is not null)
                _ = _session.SendTextAsync(input);
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
                        _ = _session.SendTextAsync(string.Join(' ', parts[1..]));
                    break;

                case "/hex":
                    if (parts.Length < 2)
                    {
                        Log("Usage: /hex <hex>");
                        break;
                    }
                    if (_session is not null)
                        _ = _session.SendBinaryAsync(Convert.FromHexString(string.Join("", parts[1..])));
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
                        _ = _session.SendBinaryAsync(randomData);
                    break;

                case "/stream":
                    if (parts.Length < 2 || !int.TryParse(parts[1], out var streamSize))
                    {
                        Log("Usage: /stream <size>");
                        break;
                    }
                    if (_session is not null)
                        _ = _session.StreamSendAsync(streamSize);
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
                    _session.Config.NoDelay = noDelay;
                    _session.Config.IntervalMs = interval;
                    _session.Config.FastResend = fastResend;
                    _session.Config.NoCongestionControl = noCwnd;
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
                    _session.Config.SendWindow = sendWin;
                    _session.Config.ReceiveWindow = recvWin;
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
                    _session.Config.Mtu = mtu;
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
                    _session.Config.IntervalMs = intervalMs;
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
                    if (_session is not null)
                        _session.Config.Direction = direction;

                    if (_directionSelector is not null)
                        _directionSelector.Value = direction;

                    if (_session is not null && _session.IsRunning)
                        _ = _session.RestartReceiveLoopAsync();

                    Log($"Direction: {direction}");
                    break;

                case "/help":
                    Log("/send <text>  /hex <hex>  /random <size>  /stream <size>");
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

    private static void UpdateCommandHint(string currentText)
    {
        if (string.IsNullOrEmpty(currentText) || !currentText.StartsWith('/'))
        {
            _hintText = "Type / for commands, or just type text to send";
            return;
        }

        var commandPart = currentText.Split(' ')[0];

        if (string.IsNullOrEmpty(commandPart) || commandPart == "/")
        {
            _hintText = "Commands: /send /hex /random /stream /flush /nodelay /window /mtu /interval /direction /help";
            return;
        }

        var exactMatch = CommandHints.FirstOrDefault(c => c.Command.Equals(commandPart, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(exactMatch.Command))
        {
            _hintText = $"{exactMatch.Command} - {exactMatch.Hint}  Usage: {exactMatch.Usage}";
            return;
        }

        var matches = CommandHints.Where(c => c.Command.StartsWith(commandPart, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            _hintText = "Unknown command. Try /help";
            return;
        }

        if (matches.Length == 1)
        {
            _hintText = $"{matches[0].Command} - {matches[0].Hint}  Usage: {matches[0].Usage}";
            return;
        }

        _hintText = string.Join("  ", matches.Select(m => m.Command));
    }



    private static void ApplyNetworkSettings()
    {
        _session?.Config.LocalIp = GetFieldValue(_fieldLocalIp, _session?.Config.LocalIp ?? "0.0.0.0");
        _session?.Config.LocalPort = GetFieldInt(_fieldLocalPort, _session?.Config.LocalPort ?? 10001);
        _session?.Config.RemoteIp = GetFieldValue(_fieldRemoteIp, _session?.Config.RemoteIp ?? "127.0.0.1");
        _session?.Config.RemotePort = GetFieldInt(_fieldRemotePort, _session?.Config.RemotePort ?? 10002);
        _session?.Config.ConversationId = GetFieldUint(_fieldConversationId, _session?.Config.ConversationId ?? 1);

        Log($"Network: {_session?.Config.LocalIp}:{_session?.Config.LocalPort} -> {_session?.Config.RemoteIp}:{_session?.Config.RemotePort} conv={_session?.Config.ConversationId}");
    }

    private static void ApplyKcpSettings()
    {
        _session?.Config.NoDelay = _checkNoDelay?.IsChecked ?? false;
        _session?.Config.IntervalMs = GetFieldInt(_fieldInterval, _session?.Config.IntervalMs ?? 100);
        _session?.Config.FastResend = GetFieldInt(_fieldFastResend, _session?.Config.FastResend ?? 0);
        _session?.Config.NoCongestionControl = _checkNoCongestion?.IsChecked ?? false;
        _session?.Config.SendWindow = GetFieldInt(_fieldSendWindow, _session?.Config.SendWindow ?? 128);
        _session?.Config.ReceiveWindow = GetFieldInt(_fieldReceiveWindow, _session?.Config.ReceiveWindow ?? 256);
        _session?.Config.Mtu = GetFieldInt(_fieldMtu, _session?.Config.Mtu ?? 1400);
        _session?.Config.StreamMode = _checkStreamMode?.IsChecked ?? false;

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
            _session?.Config.BufferSize = GetFieldInt(_fieldBufferSize, _session?.Config.BufferSize ?? 4096);
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

        _checkNoDelay?.IsChecked = _session?.Config.NoDelay == true;
        _fieldInterval?.Text = _session?.Config.IntervalMs.ToString() ?? string.Empty;
        _fieldFastResend?.Text = _session?.Config.FastResend.ToString() ?? string.Empty;
        _checkNoCongestion?.IsChecked = _session?.Config.NoCongestionControl == true;
        _fieldSendWindow?.Text = _session?.Config.SendWindow.ToString() ?? string.Empty;
        _fieldReceiveWindow?.Text = _session?.Config.ReceiveWindow.ToString() ?? string.Empty;
        _fieldMtu?.Text = _session?.Config.Mtu.ToString() ?? string.Empty;
        _checkStreamMode?.IsChecked = _session?.Config.StreamMode == true;

        _fieldBufferSize?.Text = _session?.Config.BufferSize.ToString() ?? string.Empty;
        _directionSelector?.Value = _session?.Config.Direction ?? KcpConfig.ECommunicationDirection.Bidirectional;
    }

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



    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_pendingLogsLock)
            _pendingLogs.Add(line);
    }

    private static void FlushPendingLogs()
    {
        List<string> toFlush;
        lock (_pendingLogsLock)
        {
            if (_pendingLogs.Count == 0)
                return;
            toFlush = [.. _pendingLogs];
            _pendingLogs.Clear();
        }

        var newCount = toFlush.Count;

        lock (_logLock)
        {
            foreach (var line in toFlush)
            {
                _logLineCount++;
                _logLines.Add(line);

                if (5000 < _logLineCount && 3000 < _logLines.Count)
                {
                    _logLines.RemoveRange(0, _logLines.Count - 2000);
                    _logLineCount = 2000;
                }
            }
        }

        if (_logView is not null && 0 < _logView.ScrollOffset)
        {
            _logView.ScrollOffset += newCount;
            _unreadLogCount += newCount;
        }
    }

    private static void ClearLog()
    {
        lock (_logLock)
        {
            _logLines.Clear();
            _logLineCount = 0;
        }
        _unreadLogCount = 0;
    }

    private static void RefreshStats()
    {
        _statsText = _session?.BuildStatisticsString() ?? "No session";
    }



    private static string GetFieldValue(InputField? field, string defaultValue)
        => field?.Text.Trim() ?? defaultValue;

    private static int GetFieldInt(InputField? field, int defaultValue)
        => int.TryParse(field?.Text.Trim(), out var value) ? value : defaultValue;

    private static uint GetFieldUint(InputField? field, uint defaultValue)
        => uint.TryParse(field?.Text.Trim(), out var value) ? value : defaultValue;
}
