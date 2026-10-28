using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MasLauncher;

/// <summary>
/// Front-end for the activation engine. The engine is compiled into this EXE and written to a
/// private per-user data folder at startup, then launched through cmd.exe with its own command-line
/// switches. Nothing is downloaded separately and no activation logic is reimplemented here.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(23, 23, 26);
    private static readonly Color PanelBg = Color.FromArgb(30, 30, 34);
    private static readonly Color CardBg = Color.FromArgb(36, 36, 42);
    private static readonly Color BtnBg = Color.FromArgb(47, 47, 56);
    private static readonly Color Fg = Color.FromArgb(228, 228, 231);
    private static readonly Color Muted = Color.FromArgb(148, 148, 158);
    private static readonly Color Accent = Color.FromArgb(76, 160, 240);
    private static readonly Color Green = Color.FromArgb(86, 200, 124);
    private static readonly Color Amber = Color.FromArgb(232, 176, 74);
    private static readonly Color Red = Color.FromArgb(236, 104, 104);
    private static readonly Color OsBlue = Color.FromArgb(0, 140, 255); // #008CFF

    private static readonly string CmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly Encoding Oem = CreateOemEncoding();

    private readonly Label _osLabel = new();
    private readonly Label _winLabel = new();
    private readonly Label _officeLabel = new();
    private readonly Label _stateLabel = new();
    private readonly RichTextBox _log = new();
    private readonly HintTextBox _input = new();
    private readonly RoundedButton _sendButton = new();
    private readonly RoundedButton _stopButton = new();
    private readonly RoundedButton _refreshButton = new();
    private readonly RoundedButton _updatesButton = new();
    private readonly List<Button> _actions = new();
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _clock = new();
    private readonly System.Windows.Forms.Timer _pump = new();
    private TerminalView _terminal = null!;
    private ConsoleSession? _session;

    private string? _enginePath;
    private string? _engineError;
    private SplitContainer _split = null!;
    private DateTime _startedAt;
    private string _runLabel = "";
    private bool _statusRunning;
    private bool _updateRunning;

    /// <summary>False while a diagnostic mode owns the process, so no update prompt can block it.</summary>
    internal static bool AutoUpdateOnStart = true;

    /// <summary>False during headless runs: update checks report instead of opening dialogs.</summary>
    internal static bool ShowPrompts = true;

    internal string? EnginePathForTest => _enginePath;

    public MainForm()
    {
        Text = $"WinAct Ver.{AppInfo.Version} - Windows and Office Activator";
        Icon = LoadAppIcon();
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(940, 660);
        Size = new Size(1040, 780);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        _clock.Interval = 500;
        _clock.Tick += (_, _) =>
        {
            if (_session is null) return;
            var el = (int)(DateTime.Now - _startedAt).TotalSeconds;
            _stateLabel.Text = $"{_runLabel}  ·  {el / 60:00}:{el % 60:00}";
        };

        BuildUi();
        _terminal = new TerminalView(_log);

        _pump.Interval = 45;
        _pump.Tick += (_, _) => _terminal.Pump();
        _pump.Start();

        _enginePath = Engine.Prepare(out _engineError);
        ApplyEngineInfo();
        AppUpdater.CleanupStale();

        Load += async (_, _) => await InitialiseAsync();
        FormClosing += OnFormClosing;
        Disposed += (_, _) => Engine.Cleanup();
    }

    /// <summary>
    /// While a script is running, a keystroke anywhere in the window is treated as
    /// terminal input, so the user can just start typing instead of hunting for the box.
    /// </summary>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (e.Handled || _session is null || char.IsControl(e.KeyChar)) return;
        if (ActiveControl == _input) return;

        _input.Focus();
        _input.Text += e.KeyChar;
        _input.SelectionStart = _input.Text.Length;
        e.Handled = true;
    }

    // ---------------------------------------------------------------- layout

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Bg,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildStatusBar(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);

        Controls.Add(root);
    }

    /// <summary>The window icon is the same .ico the EXE is stamped with.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("WinAct.ico");
            if (stream is not null) return new Icon(stream);
        }
        catch
        {
            // fall back to the icon on the executable
        }

        try
        {
            if (Environment.ProcessPath is { } path && Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch
        {
            // fall through to the stock icon
        }

        return SystemIcons.Application;
    }

    private Control BuildStatusBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = CardBg, Padding = new Padding(18, 0, 14, 0), Margin = new Padding(0) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, BackColor = Color.Transparent };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _winLabel.AutoSize = true;
        _winLabel.Anchor = AnchorStyles.Left;
        _winLabel.Text = "Windows: …";
        _winLabel.Margin = new Padding(0, 0, 24, 0);

        _officeLabel.AutoSize = true;
        _officeLabel.Anchor = AnchorStyles.Left;
        _officeLabel.Text = "Office: …";
        _officeLabel.Margin = new Padding(0, 0, 24, 0);

        _osLabel.AutoSize = false;
        _osLabel.Dock = DockStyle.Fill;
        _osLabel.TextAlign = ContentAlignment.MiddleLeft;
        _osLabel.ForeColor = OsBlue;
        _osLabel.Text = "detecting Windows build…";
        _osLabel.AutoEllipsis = true;
        _osLabel.Margin = new Padding(0);

        _refreshButton.Text = "Refresh";
        _refreshButton.AutoSize = false;
        _refreshButton.Size = new Size(84, 26);
        _refreshButton.Anchor = AnchorStyles.Right;
        _refreshButton.FlatStyle = FlatStyle.Flat;
        _refreshButton.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 82);
        _refreshButton.BackColor = BtnBg;
        _refreshButton.ForeColor = Fg;
        _refreshButton.UseVisualStyleBackColor = false;
        _refreshButton.Margin = new Padding(8, 0, 0, 0);
        _refreshButton.Click += async (_, _) => await RefreshStatusAsync(verbose: true);

        _updatesButton.Text = "Check updates";
        _updatesButton.AutoSize = false;
        _updatesButton.Size = new Size(104, 26);
        _updatesButton.Anchor = AnchorStyles.Right;
        _updatesButton.FlatStyle = FlatStyle.Flat;
        _updatesButton.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 82);
        _updatesButton.BackColor = BtnBg;
        _updatesButton.ForeColor = Fg;
        _updatesButton.UseVisualStyleBackColor = false;
        _updatesButton.Margin = new Padding(8, 0, 0, 0);
        _updatesButton.Click += async (_, _) => await CheckForUpdatesAsync();

        _tips.SetToolTip(_updatesButton, "Checks the release for a newer WinAct build and offers to install it. Nothing is downloaded without asking.");

        grid.Controls.Add(_winLabel, 0, 0);
        grid.Controls.Add(_officeLabel, 1, 0);
        grid.Controls.Add(_osLabel, 2, 0);
        grid.Controls.Add(_updatesButton, 3, 0);
        grid.Controls.Add(_refreshButton, 4, 0);
        bar.Controls.Add(grid);
        return bar;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Bg,
            SplitterWidth = 8,
            Margin = new Padding(0),
        };
        _split = split;
        split.Panel1.Padding = new Padding(14, 14, 14, 0);
        split.Panel2.Padding = new Padding(14, 0, 14, 14);
        split.Panel1.Controls.Add(BuildCards());
        split.Panel2.Controls.Add(BuildLog());
        return split;
    }

    /// <summary>Splitter geometry is only valid once the control has its real size.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _split.Panel1MinSize = 240;
            _split.Panel2MinSize = 160;
            _split.SplitterDistance = Math.Max(_split.Panel1MinSize, (int)(_split.Height * 0.56));
        }
        catch
        {
            // keep the default layout
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnableDarkTitleBar();
    }

    /// <summary>Matches the window chrome to the dark UI. Silently ignored on old builds.</summary>
    private void EnableDarkTitleBar()
    {
        try
        {
            var enabled = 1;
            const int DwmwaUseImmersiveDarkMode = 20;
            const int DwmwaUseImmersiveDarkModeLegacy = 19;
            if (DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
        catch
        {
            // cosmetic only
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private Control BuildCards()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0) };
        for (var i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));

        grid.Controls.Add(Card("Windows", new[]
        {
            new Item
            {
                Text = "Activate Windows", Sub = "HWID  ·  permanent digital licence",
                Tip = "Binds a digital licence to this PC's hardware. Windows 10/11 only.",
                Args = "/HWID",
            },
            new Item
            {
                Text = "TSforge  ·  Windows", Sub = "offline phone-style activation + ESU",
                Tip = "Works offline and also covers Server and ESU builds.",
                Args = "/Z-Windows",
            },
            new Item
            {
                Text = "Online KMS  ·  Windows", Sub = "180-day licence, auto-renewed",
                Tip = "Needs internet access; renews itself through a scheduled task.",
                Args = "/K-Windows",
            },
            new Item
            {
                Text = "HWID  ·  keep edition", Sub = "no automatic Home → Pro change",
                Tip = "Same as HWID but leaves the installed edition alone.",
                Args = "/HWID-NoEditionChange",
            },
        }), 0, 0);

        grid.Controls.Add(Card("Office", new[]
        {
            new Item
            {
                Text = "Activate Office", Sub = "Ohook  ·  permanent, Office 2013+",
                Tip = "Hooks the Office licensing check locally.",
                Args = "/Ohook",
            },
            new Item
            {
                Text = "Online KMS  ·  Office", Sub = "180-day licence, auto-renewed",
                Tip = "Needs internet access.",
                Args = "/K-Office",
            },
            new Item
            {
                Text = "Remove Ohook", Sub = "uninstall the Office hook",
                Tip = "Uninstalls the Office licensing hook.",
                Args = "/Ohook-Uninstall",
            },
            new Item
            {
                Text = "TSforge  ·  Office", Sub = "offline activation, all Office apps",
                Tip = "Covers Project and Visio too.",
                Args = "/Z-Office",
            },
        }), 1, 0);

        grid.Controls.Add(Card("Tools", new[]
        {
            new Item
            {
                Text = "Everything at once", Sub = "TSforge for Windows, Office and ESU",
                Tip = "Activates Windows, Office and ESU in one pass.",
                Args = "/Z-WindowsESUOffice",
            },
            new Item
            {
                Text = "Check activation status", Sub = "licence details in the log",
                Tip = "Queries the Software Protection Platform and prints every licensed product.",
                Run = () => RefreshStatusAsync(verbose: true),
            },
            new Item
            {
                Text = "Classic menu", Sub = "the full menu, answer below",
                Tip = "Opens the full menu. Selections are typed into the box under the log.",
                Args = "",
            },
            new Item
            {
                Text = "Open app folder", Sub = "show the app files in Explorer",
                Tip = "Opens the folder that holds the app.",
                Run = OpenAppFolder,
            },
        }), 2, 0);

        return grid;
    }

    private Panel Card(string heading, Item[] items)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBg,
            Radius = 6,
            CornerColor = Bg,
            Margin = new Padding(6, 0, 6, 6),
            Padding = new Padding(14, 12, 14, 12),
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Color.Transparent, Margin = new Padding(0), AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowCount = items.Length + 2;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        grid.Controls.Add(new Label
        {
            Text = heading.ToUpperInvariant(),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 8.5F),
            ForeColor = Muted,
            Margin = new Padding(2, 0, 0, 0),
        }, 0, 0);

        var row = 1;
        foreach (var item in items)
        {
            var button = new RoundedButton
            {
                Radius = 4,
                Text = item.Sub.Length == 0 ? item.Text : item.Text + "\n" + item.Sub,
                Dock = DockStyle.Fill,
                Height = item.Sub.Length == 0 ? 38 : 56,
                Margin = new Padding(0, 0, 0, 8),
                FlatStyle = FlatStyle.Flat,
                BackColor = BtnBg,
                ForeColor = Fg,
                UseVisualStyleBackColor = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 6, 0),
                Font = new Font("Segoe UI", 9F),
                Cursor = Cursors.Hand,
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(64, 64, 76);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 70);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(42, 42, 50);
            if (item.Tip.Length > 0) _tips.SetToolTip(button, item.Tip);

            var local = item;
            button.Click += async (_, _) =>
            {
                if (local.Run is not null) await local.Run();
                else StartSession(local.Args, local.Text);
            };

            _actions.Add(button);
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, button.Height + 8));
            grid.Controls.Add(button, 0, row++);
        }

        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) }, 0, row);

        card.Controls.Add(grid);
        return card;
    }

    private Control BuildLog()
    {
        var wrap = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.FromArgb(16, 16, 18), Margin = new Padding(0) };
        wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var bar = new Panel { Dock = DockStyle.Fill, BackColor = PanelBg, Padding = new Padding(12, 0, 8, 0), Margin = new Padding(0) };

        _stateLabel.Text = "Idle";
        _stateLabel.AutoSize = true;
        _stateLabel.ForeColor = Muted;
        _stateLabel.Location = new Point(12, 9);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(0, 5, 0, 0),
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };

        _stopButton.Text = "Stop";
        _stopButton.Enabled = false;
        _stopButton.ForeColor = Red;
        _stopButton.Click += (_, _) => StopRun();

        var save = SmallButton("Save log");
        save.Click += (_, _) => SaveLog();

        var copy = SmallButton("Copy");
        copy.Click += (_, _) => CopyLog();

        var clear = SmallButton("Clear");
        clear.Click += (_, _) => _terminal.Clear();

        buttons.Controls.Add(_stopButton);
        buttons.Controls.Add(save);
        buttons.Controls.Add(copy);
        buttons.Controls.Add(clear);
        _stopButton.Size = new Size(64, 24);
        _stopButton.FlatStyle = FlatStyle.Flat;
        _stopButton.FlatAppearance.BorderColor = Color.FromArgb(80, 60, 60);
        _stopButton.BackColor = BtnBg;
        _stopButton.UseVisualStyleBackColor = false;
        _stopButton.Margin = new Padding(6, 0, 0, 0);

        bar.Controls.Add(buttons);
        bar.Controls.Add(_stateLabel);

        _log.Dock = DockStyle.Fill;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.FromArgb(16, 16, 18);
        _log.ForeColor = Color.FromArgb(214, 214, 220);
        _log.Font = new Font("Consolas", 9F);
        _log.ReadOnly = true;
        _log.WordWrap = true;
        _log.ScrollBars = RichTextBoxScrollBars.Vertical;
        _log.HideSelection = false;
        _log.DetectUrls = true;
        _log.Margin = new Padding(0);
        _log.LinkClicked += (_, e) => OpenUrl(e.LinkText);

        // Indent the text away from the pane edge; the grid is a terminal, not a text dump.
        var logHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(16, 16, 18),
            Padding = new Padding(14, 10, 10, 6),
            Margin = new Padding(0),
        };
        logHost.Controls.Add(_log);

        wrap.Controls.Add(bar, 0, 0);
        wrap.Controls.Add(logHost, 0, 1);
        wrap.Controls.Add(BuildInputRow(), 0, 2);
        return wrap;
    }

    /// <summary>The answer box: whatever is typed here is sent to the running action's console.</summary>
    private Control BuildInputRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            BackColor = PanelBg,
            Padding = new Padding(12, 6, 10, 6),
            Margin = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));

        var prompt = new Label
        {
            Text = "›",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Accent,
            Font = new Font("Consolas", 11F),
            Margin = new Padding(0),
        };

        _input.Dock = DockStyle.Fill;
        _input.BorderStyle = BorderStyle.FixedSingle;
        _input.BackColor = Color.FromArgb(18, 18, 22);
        _input.ForeColor = Fg;
        _input.Font = new Font("Consolas", 9.5F);
        _input.Hint = "Run an action first, then type here";
        _input.Margin = new Padding(0, 2, 6, 2);
        _input.HandleCreated += (_, _) => SetInputMargins();
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SendInput();
        };

        _sendButton.Text = "Send";
        _sendButton.Dock = DockStyle.Fill;
        _sendButton.Enabled = false;
        _sendButton.FlatStyle = FlatStyle.Flat;
        _sendButton.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 82);
        _sendButton.BackColor = BtnBg;
        _sendButton.ForeColor = Fg;
        _sendButton.UseVisualStyleBackColor = false;
        _sendButton.Margin = new Padding(0, 2, 0, 2);
        _sendButton.Click += (_, _) => SendInput();
        _tips.SetToolTip(_sendButton, "Send your answer, followed by Enter.");

        row.Controls.Add(prompt, 0, 0);
        row.Controls.Add(_input, 1, 0);
        row.Controls.Add(_sendButton, 2, 0);
        return row;
    }

    private const int EmSetMargins = 0xD3;
    private const int EcLeftMargin = 0x0001;
    private const int EcRightMargin = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Indents the typed text and the placeholder inside the input box.</summary>
    private void SetInputMargins()
    {
        const int left = 10;
        const int right = 10;
        SendMessage(
            _input.Handle,
            EmSetMargins,
            (IntPtr)(EcLeftMargin | EcRightMargin),
            (IntPtr)((right << 16) | left));
    }

    private void SendInput()
    {
        var session = _session;
        if (session is null || !session.IsRunning)
        {
            AppendLog("Nothing is running — start an action first.", Muted);
            return;
        }

        var text = _input.Text;
        _input.Clear();
        session.Write(text + "\r");
        _input.Focus();
    }

    private static RoundedButton SmallButton(string text) => new()
    {
        Radius = 4,
        Text = text,
        AutoSize = false,
        Size = new Size(text.Length > 7 ? 78 : 60, 24),
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderColor = Color.FromArgb(70, 70, 82) },
        BackColor = BtnBg,
        ForeColor = Fg,
        UseVisualStyleBackColor = false,
        Margin = new Padding(6, 0, 0, 0),
    };

    // ---------------------------------------------------------------- startup

    private async Task InitialiseAsync()
    {
        AppendLog($"WinAct {AppInfo.Version}", Accent);
        AppendLog(IsElevated() ? "Elevated: yes" : "Elevated: NO — activation will fail. Restart the app as administrator.", IsElevated() ? Muted : Red);

        if (_enginePath is null)
        {
            AppendLog("WinAct could not be prepared: " + (_engineError ?? "unknown error") + ".", Red);
            AppendLog("Starting without WinAct — activation and Office tools are unavailable.", Amber);
        }
        else
        {
            var info = new FileInfo(_enginePath);
            AppendLog($"WinAct ready  ·  {info.Length / 1024:N0} KB", Muted);
            AppendLog("Tip: type your selection in the box under the log and press Enter when prompted.", Muted);
        }

        await RefreshStatusAsync(verbose: false);

        // Automatic check on launch: quiet unless a newer build can be offered.
        if (AutoUpdateOnStart && await CheckAppUpdateAsync(prompt: true, quiet: true).ConfigureAwait(true))
            return;
    }

    // ---------------------------------------------------------------- version / updates

    private void ApplyEngineInfo()
    {
        _updatesButton.Enabled = _session is null && !_updateRunning;
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateRunning) return;

        _updateRunning = true;
        _updatesButton.Enabled = false;
        _updatesButton.Text = "Checking…";

        try
        {
            await CheckAppUpdateAsync(prompt: true).ConfigureAwait(true);
        }
        finally
        {
            _updateRunning = false;
            _updatesButton.Text = "Check updates";
            _updatesButton.Enabled = _session is null;
        }
    }

    /// <summary>
    /// Compares the launcher with its own published release, then downloads, verifies and
    /// swaps it in when the user agrees. Returns true when the app is about to restart.
    /// </summary>
    private async Task<bool> CheckAppUpdateAsync(bool prompt, bool quiet = false)
    {
        try
        {
            var release = await AppUpdater.GetLatestAsync().ConfigureAwait(true);
            if (release is null)
            {
                if (!quiet) AppendLog("No launcher release has been published yet.", Muted);
                return false;
            }

            var current = AppInfo.Version;
            if (AppInfo.Compare(release.Version, current) <= 0)
            {
                if (!quiet) AppendLog($"WinAct {current} is up to date.", Green);
                return false;
            }

            AppendLog($"WinAct update available: {current} → {release.Version}.", Amber);
            if (release.PageUrl is not null) AppendLog("  " + release.PageUrl, Muted);

            if (!prompt) return false;

            if (!ShowPrompts)
            {
                AppendLog("  Headless check: download skipped.", Muted);
                return false;
            }

            if (_session is not null)
            {
                AppendLog("  An action is running — update deferred.", Muted);
                return false;
            }

            if (!AppUpdater.CanSelfInstall(out var reason))
            {
                AppendLog("  This copy cannot update itself — " + reason + ".", Muted);
                return false;
            }

            var answer = MessageBox.Show(this,
                $"WinAct {release.Version} is available. You have {current}.\n\n" +
                "Download and install it now? The app will close and restart.",
                "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (answer != DialogResult.Yes)
            {
                AppendLog("  Update skipped — press “Check updates” when you are ready.", Muted);
                return false;
            }

            AppendLog("  Downloading the new build…", Muted);
            var staged = await AppUpdater.DownloadAsync(release).ConfigureAwait(true);

            var problem = AppUpdater.Validate(staged, release.Version);
            if (problem is not null)
            {
                AppendLog("  The download was refused: " + problem + ".", Red);
                return false;
            }

            var exe = Environment.ProcessPath!;
            AppendLog("  Download verified — restarting…", Muted);
            AppUpdater.Install(staged, exe, out _);
            try { File.Delete(staged); } catch { }
            AppUpdater.Restart(exe);
            Application.Exit();
            return true;
        }
        catch (Exception ex)
        {
            if (!quiet) AppendLog("Launcher update failed: " + ex.Message, Amber);
            return false;
        }
    }

    internal Task UpdateCheckForTest() => CheckForUpdatesAsync();

    internal string LogTextForTest => _log.Text;

    // --- hooks used by the headless --inputtest run ---

    internal bool RunningForTest => _session is not null;

    internal StringBuilder? RawOutputForTest;

    internal void UseEngineForTest(string path)
    {
        _enginePath = path;
        ApplyEngineInfo();
    }

    internal void StartForTest(string args, string label) => StartSession(args, label);

    internal void SendForTest(string text)
    {
        _input.Text = text;
        SendInput();
    }

    // ---------------------------------------------------------------- running

    /// <summary>
    /// Starts the batch script inside a pseudoconsole. `-qedit` is passed so MAS does not
    /// relaunch itself into a separate conhost window, which would detach it from our pty.
    /// </summary>
    private void StartSession(string args, string label)
    {
        if (_session is not null)
        {
            AppendLog("Another operation is still running — stop it first.", Amber);
            return;
        }

        if (_enginePath is null)
        {
            _enginePath = Engine.Prepare(out _engineError);
            if (_enginePath is null)
            {
                AppendLog("WinAct is not available: " + (_engineError ?? "unknown error") + ".", Red);
                return;
            }
            ApplyEngineInfo();
        }

        var extra = string.IsNullOrWhiteSpace(args) ? string.Empty : " " + args;
        var commandLine = $"{CmdPath} /c \"\"{_enginePath}\"{extra} -qedit\"";

        AppendLog("", null);
        AppendLog($"▶ {label}", Accent);
        AppendLog("  type your selection below and press Enter when prompted", Muted);

        var session = new ConsoleSession();
        session.Output += chunk =>
        {
            RawOutputForTest?.Append(chunk);
            _terminal.Enqueue(chunk);
        };
        session.Exited += code => Post(() => OnSessionExited(session, code));

        if (!session.Start(commandLine, Path.GetDirectoryName(_enginePath)!))
        {
            AppendLog("Could not create the pseudoconsole — Windows 10 1809 or newer is required.", Red);
            session.Dispose();
            return;
        }

        _session = session;
        _startedAt = DateTime.Now;
        _runLabel = label;
        SetRunning(true, label);
        _input.Focus();
    }

    private void OnSessionExited(ConsoleSession session, int code)
    {
        var elapsed = DateTime.Now - _startedAt;
        try { session.Dispose(); } catch { }
        _terminal.Pump();

        AppendLog($"■ {_runLabel} finished in {elapsed.TotalSeconds:0}s  (exit code {code})", Muted);
        AppendLog("", null);

        if (ReferenceEquals(_session, session)) _session = null;
        SetRunning(false);

        _ = RefreshStatusAsync(verbose: false);
    }

    private void StopRun()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            session.Kill();
            AppendLog("Stop requested — the running action was terminated.", Amber);
        }
        catch (Exception ex)
        {
            AppendLog("Stop failed: " + ex.Message, Red);
        }
    }

    private void SetRunning(bool running, string label = "")
    {
        foreach (var button in _actions) button.Enabled = !running;
        _refreshButton.Enabled = !running && !_statusRunning;
        _updatesButton.Enabled = !running && !_updateRunning;
        _stopButton.Enabled = running;
        _sendButton.Enabled = running;
        _input.Hint = running ? "Type your answer, then press Enter" : "Run an action first, then type here";

        if (running)
        {
            _runLabel = label;
            _stateLabel.Text = label;
            _stateLabel.ForeColor = Accent;
            _clock.Start();
        }
        else
        {
            _clock.Stop();
            _stateLabel.Text = "Idle";
            _stateLabel.ForeColor = Muted;
        }
    }

    // ---------------------------------------------------------------- status

    private const string StatusScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $os = Get-CimInstance -ClassName Win32_OperatingSystem
        $prods = @(Get-CimInstance -ClassName SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL" |
            ForEach-Object { [pscustomobject]@{ name = $_.Name; status = [int]$_.LicenseStatus; desc = $_.Description } })
        [pscustomobject]@{
            os    = ("{0} - build {1}" -f $os.Caption, $os.BuildNumber)
            prods = $prods
        } | ConvertTo-Json -Depth 4 -Compress
        """;

    private async Task RefreshStatusAsync(bool verbose)
    {
        if (_statusRunning) return;
        _statusRunning = true;
        _refreshButton.Enabled = false;
        _winLabel.Text = "Windows: …";
        _officeLabel.Text = "Office: …";

        try
        {
            var json = await RunStatusQueryAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                _winLabel.Text = "Windows: unknown";
                _winLabel.ForeColor = Amber;
                _officeLabel.Text = "Office: unknown";
                _officeLabel.ForeColor = Amber;
                if (verbose) AppendLog("Status query returned nothing (Software Protection Platform unreachable).", Amber);
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("os", out var os) && os.GetString() is { Length: > 0 } osText)
                _osLabel.Text = osText;

            string? winName = null, officeName = null;
            var winStatus = -1;
            var officeStatus = -1;
            var all = new List<(string Name, int Status)>();

            if (root.TryGetProperty("prods", out var prods))
            {
                var items = prods.ValueKind == JsonValueKind.Array
                    ? prods.EnumerateArray().ToList()
                    : new List<JsonElement> { prods };

                foreach (var item in items)
                {
                    if (!item.TryGetProperty("name", out var nameProp)) continue;
                    var name = nameProp.GetString() ?? "";
                    item.TryGetProperty("status", out var statusProp);
                    var status = statusProp.ValueKind == JsonValueKind.Number ? statusProp.GetInt32() : -1;
                    all.Add((name, status));
                }
            }

            var windows = all.Where(p => p.Name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)).ToList();
            var office = all.Where(p => p.Name.StartsWith("Office", StringComparison.OrdinalIgnoreCase)).ToList();

            // Prefer a licensed entry, then a "main" product over auxiliary ones.
            var winPick = windows.OrderByDescending(p => p.Status == 1)
                                 .ThenByDescending(p => p.Name.Contains("Operating System", StringComparison.OrdinalIgnoreCase))
                                 .FirstOrDefault();
            var officePick = office.OrderByDescending(p => p.Status == 1)
                                   .ThenByDescending(p => p.Name.Contains("ProPlus", StringComparison.OrdinalIgnoreCase))
                                   .ThenByDescending(p => p.Name.Contains("Standard", StringComparison.OrdinalIgnoreCase))
                                   .FirstOrDefault();
            if (winPick.Name is not null) { winName = winPick.Name; winStatus = winPick.Status; }
            if (officePick.Name is not null) { officeName = officePick.Name; officeStatus = officePick.Status; }

            var (winText, winColor) = Describe(winName, winStatus);
            var (officeText, officeColor) = Describe(officeName, officeStatus);
            _winLabel.Text = "Windows: " + winText;
            _winLabel.ForeColor = winColor;
            _officeLabel.Text = "Office: " + officeText;
            _officeLabel.ForeColor = officeColor;

            if (verbose)
            {
                AppendLog("Licence report (Software Protection Platform)", Accent);
                if (all.Count == 0) AppendLog("  no licensed products found", Muted);
                foreach (var (name, status) in all.OrderBy(p => p.Name))
                    AppendLog($"  [{StatusText(status)}]  {name}", status == 1 ? Green : Muted);
                AppendLog("", null);
            }
        }
        catch (Exception ex)
        {
            _winLabel.Text = "Windows: unknown";
            _winLabel.ForeColor = Amber;
            _officeLabel.Text = "Office: unknown";
            _officeLabel.ForeColor = Amber;
            AppendLog("Status query failed: " + ex.Message, Amber);
        }
        finally
        {
            _statusRunning = false;
            _refreshButton.Enabled = _session is null;
        }
    }

    internal string? RunStatusQueryForTest() => RunStatusQueryAsync().GetAwaiter().GetResult();

    private static async Task<string?> RunStatusQueryAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "windows-activator-status.ps1");
        await File.WriteAllTextAsync(temp, StatusScript, new UTF8Encoding(true)).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{temp}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Oem,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return output.Trim();
    }

    private static string PowerShellPath
    {
        get
        {
            var path = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(path) ? path : "powershell.exe";
        }
    }

    private static (string Text, Color Color) Describe(string? name, int status)
    {
        if (name is null) return ("not detected", Muted);
        var edition = EditionOf(name);
        return status switch
        {
            1 => ($"licensed  ·  {edition}", Green),
            0 => ($"not activated  ·  {edition}", Red),
            6 => ($"grace period  ·  {edition}", Amber),
            _ => ($"{StatusText(status)}  ·  {edition}", Amber),
        };
    }

    private static string StatusText(int status) => status switch
    {
        0 => "unlicensed",
        1 => "licensed",
        2 => "OOB grace",
        3 => "OOT grace",
        4 => "non-genuine grace",
        5 => "notification",
        6 => "extended grace",
        _ => "unknown",
    };

    private static string EditionOf(string name)
    {
        var match = Regex.Match(name, @",\s*(.+?)(?:\s+edition)?$", RegexOptions.IgnoreCase);
        var edition = match.Success ? match.Groups[1].Value : name;
        edition = edition.Replace("Operating System", "", StringComparison.OrdinalIgnoreCase).Trim(' ', ',');
        edition = Regex.Replace(edition, @"^Office\d{2}", "", RegexOptions.IgnoreCase);
        edition = Regex.Replace(edition, @"\d{4}R_(Retail|VL)", " $1", RegexOptions.IgnoreCase);
        edition = Regex.Replace(edition, @"R_(Retail|VL)", " $1", RegexOptions.IgnoreCase);
        edition = Regex.Replace(edition, @"(Retail|VL)\d+$", "$1", RegexOptions.IgnoreCase);
        edition = Regex.Replace(edition, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        edition = Regex.Replace(edition, @"\s+", " ").Trim(' ', '_');
        return edition.Length == 0 ? name : edition;
    }

    // ---------------------------------------------------------------- log

    private void AppendLog(string text, Color? color)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => AppendLog(text, color))); } catch { }
            return;
        }

        _terminal.WriteLine(text, color ?? Fg);
    }

    private void Post(Action action)
    {
        if (IsDisposed) return;
        try { BeginInvoke(action); } catch { }
    }

    private void CopyLog()
    {
        if (_log.TextLength == 0) return;
        try { Clipboard.SetText(_log.Text); AppendLog("Log copied to the clipboard.", Muted); } catch { }
    }

    private void SaveLog()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save output",
            Filter = "Text file|*.txt",
            FileName = $"windows-activator-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, _log.Text, new UTF8Encoding(false));
            AppendLog("Log saved to " + dialog.FileName, Muted);
        }
        catch (Exception ex)
        {
            AppendLog("Could not save the log: " + ex.Message, Red);
        }
    }

    private Task OpenAppFolder() => OpenFolder(AppContext.BaseDirectory);

    private Task OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog("Could not open the folder: " + ex.Message, Red); }
        return Task.CompletedTask;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Every user-visible string, including tooltips, for the wording check.</summary>
    internal IEnumerable<(string Source, string Text)> VisibleTextForTest()
    {
        foreach (var control in AllControls(this))
        {
            if (!string.IsNullOrEmpty(control.Text))
                yield return (control.GetType().Name, control.Text);

            var tip = _tips.GetToolTip(control);
            if (!string.IsNullOrEmpty(tip))
                yield return ("tip:" + control.GetType().Name, tip);

            if (control is HintTextBox { Hint.Length: > 0 } hintBox)
                yield return ("hint:" + control.GetType().Name, hintBox.Hint);
        }
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var grandchild in AllControls(child)) yield return grandchild;
        }
    }

    // ---------------------------------------------------------------- misc

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static Encoding CreateOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch { return Encoding.UTF8; }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        var session = _session;
        if (session is not null)
        {
            var answer = MessageBox.Show(this,
                "An action is still running. Stop it and close the app?",
                "WinAct", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            try { session.Kill(); } catch { }
            try { session.Dispose(); } catch { }
            _session = null;
        }

        Engine.Cleanup();
    }

    private sealed class Item
    {
        public string Text = "";
        public string Sub = "";
        public string Tip = "";
        public string Args = "";
        public Func<Task>? Run;
    }
}
