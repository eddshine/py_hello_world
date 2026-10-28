using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace MasLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Diagnostics own the process; an update prompt would block them.
        MainForm.AutoUpdateOnStart = args.Length == 0;
        MainForm.ShowPrompts = args.Length == 0;

        // Headless diagnostic: build the window, resolve the script, query licence
        // state, then dump a report. Used to verify the app without clicking through it.
        if (args.Length >= 2 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            return SelfTest.Run(args[1]);

        if (args.Length >= 2 && args[0].Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
            return SelfTest.Screenshot(args[1]);

        if (args.Length >= 2 && args[0].Equals("--updatecheck", StringComparison.OrdinalIgnoreCase))
            return SelfTest.UpdateCheck(args[1]);

        if (args.Length >= 2 && args[0].Equals("--ptytest", StringComparison.OrdinalIgnoreCase))
            return SelfTest.PtyTest(args[1]);

        if (args.Length >= 2 && args[0].Equals("--ptyecho", StringComparison.OrdinalIgnoreCase))
            return SelfTest.PtyEcho(args[1]);

        if (args.Length >= 2 && args[0].Equals("--ptyseq", StringComparison.OrdinalIgnoreCase))
            return SelfTest.PtySequence(args[1]);

        if (args.Length >= 2 && args[0].Equals("--inputtest", StringComparison.OrdinalIgnoreCase))
            return SelfTest.InputTest(args[1]);

        if (args.Length >= 2 && args[0].Equals("--termtest", StringComparison.OrdinalIgnoreCase))
            return SelfTest.TermTest(args[1]);

        if (args.Length >= 2 && args[0].Equals("--ptygap", StringComparison.OrdinalIgnoreCase))
            return SelfTest.PtyGap(args[1]);

        if (args.Length >= 2 && args[0].Equals("--ptyscroll", StringComparison.OrdinalIgnoreCase))
            return SelfTest.PtyScroll(args[1]);

        if (args.Length >= 2 && args[0].Equals("--appupdate", StringComparison.OrdinalIgnoreCase))
            return SelfTest.AppUpdate(args[1]);

        if (args.Length >= 2 && args[0].Equals("--enginetest", StringComparison.OrdinalIgnoreCase))
            return SelfTest.EngineTest(args[1]);

        if (args.Length >= 2 && args[0].Equals("--enginelaunch", StringComparison.OrdinalIgnoreCase))
            return SelfTest.EngineLaunch(args[1]);

        if (args.Length >= 2 && args[0].Equals("--wording", StringComparison.OrdinalIgnoreCase))
            return SelfTest.Wording(args[1]);

        Application.Run(new MainForm());
        return 0;
    }
}

internal static class SelfTest
{
    /// <summary>Minimal pty check: run `cmd /c echo HELLO` and report what comes back.</summary>
    public static int PtyEcho(string reportPath)
    {
        var buffer = new StringBuilder();
        using var session = new ConsoleSession();
        session.Output += chunk => { lock (buffer) buffer.Append(chunk); };

        var command = Path.Combine(Environment.SystemDirectory, "cmd.exe") + " /c \"echo HELLO\"";
        if (!session.Start(command, Path.GetTempPath()))
        {
            File.WriteAllText(reportPath, "start failed: " + session.LastError);
            return 1;
        }

        Thread.Sleep(2500);
        string snapshot;
        lock (buffer) snapshot = buffer.ToString();
        session.Write("echo AFTER-INPUT\r");
        Thread.Sleep(1500);
        lock (buffer) snapshot += "\n--- after input ---\n" + buffer;

        var report = "bytes: " + snapshot.Length + "\n" + session.Diagnostics + "\n--- text ---\n" +
                     snapshot.Replace("\u001b", "<ESC>") + "\n";
        File.WriteAllText(reportPath, report, new UTF8Encoding(false));
        return snapshot.Contains("HELLO") ? 0 : 1;
    }


    public static int PtyTest(string reportPath)
    {
        var script = Path.Combine(Path.GetTempPath(), "pty-mock.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "echo MENU-START\r\n" +
            "echo [1] one   [2] two\r\n" +
            "choice /c 12 /n /m \"pick: \"\r\n" +
            "echo PICKED=%errorlevel%\r\n" +
            "exit /b %errorlevel%\r\n",
            Encoding.ASCII);

        var buffer = new StringBuilder();
        var exited = new ManualResetEventSlim();
        var exitCode = -1;

        using var session = new ConsoleSession();
        session.Output += chunk => { lock (buffer) buffer.Append(chunk); };
        session.Exited += code => { exitCode = code; exited.Set(); };

        var command = Path.Combine(Environment.SystemDirectory, "cmd.exe") + $" /c \"\"{script}\"\"";
        if (!session.Start(command, Path.GetTempPath()))
        {
            File.WriteAllText(reportPath, "FAIL: " + session.LastError + "\n");
            return 1;
        }

        string Snapshot() { lock (buffer) return buffer.ToString(); }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !Snapshot().Contains("pick:")) Thread.Sleep(50);
        var sawPrompt = Snapshot().Contains("pick:");

        session.Write("2\r");
        var finished = exited.Wait(TimeSpan.FromSeconds(10));
        if (!finished) session.Kill();

        var report = new StringBuilder();
        report.AppendLine("WinAct · ConPTY test");
        report.AppendLine("prompt seen: " + sawPrompt);
        report.AppendLine("finished:    " + finished);
        report.AppendLine("exit code:   " + exitCode);
        report.AppendLine("sizes:       " + session.Diagnostics);
        report.AppendLine("--- raw output ---");
        report.AppendLine(Snapshot().Replace("\u001b", "<ESC>"));
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));

        return sawPrompt && exitCode == 2 ? 0 : 1;
    }

    /// <summary>
    /// Two prompts in a row: proves the Enter we append to an answer does not leak into
    /// the next `choice` prompt, and that the second answer is still delivered.
    /// </summary>
    public static int PtySequence(string reportPath)
    {
        var script = Path.Combine(Path.GetTempPath(), "pty-seq.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "choice /c 12 /n /m \"first: \"\r\n" +
            "echo FIRST=%errorlevel%\r\n" +
            "choice /c ab /n /m \"second: \"\r\n" +
            "echo SECOND=%errorlevel%\r\n" +
            "exit /b 0\r\n",
            Encoding.ASCII);

        var buffer = new StringBuilder();
        var exited = new ManualResetEventSlim();
        string Snapshot() { lock (buffer) return buffer.ToString(); }
        bool WaitFor(string text, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until) { if (Snapshot().Contains(text)) return true; Thread.Sleep(50); }
            return Snapshot().Contains(text);
        }

        using var session = new ConsoleSession();
        session.Output += chunk => { lock (buffer) buffer.Append(chunk); };
        session.Exited += _ => exited.Set();

        var command = Path.Combine(Environment.SystemDirectory, "cmd.exe") + $" /c \"\"{script}\"\"";
        if (!session.Start(command, Path.GetTempPath()))
        {
            File.WriteAllText(reportPath, "FAIL: " + session.LastError + "\n");
            return 1;
        }

        var sawFirst = WaitFor("first:", 10);
        session.Write("2\r");
        var sawFirstAnswer = WaitFor("FIRST=2", 10);
        var sawSecond = WaitFor("second:", 10);
        session.Write("b\r");
        var sawSecondAnswer = WaitFor("SECOND=2", 10);
        var finished = exited.Wait(TimeSpan.FromSeconds(10));
        if (!finished) session.Kill();

        var report = new StringBuilder();
        report.AppendLine("WinAct · ConPTY sequence test");
        report.AppendLine("first prompt:  " + sawFirst);
        report.AppendLine("first answer:  " + sawFirstAnswer);
        report.AppendLine("second prompt: " + sawSecond);
        report.AppendLine("second answer: " + sawSecondAnswer);
        report.AppendLine("finished:      " + finished);
        report.AppendLine("--- raw output ---");
        report.AppendLine(Snapshot().Replace("\u001b", "<ESC>"));
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));

        return sawFirstAnswer && sawSecondAnswer ? 0 : 1;
    }

    /// <summary>
    /// End-to-end UI check: drives the real window against a mock script that asks a
    /// question, types the answer into the input box, and captures the resulting log
    /// plus a screenshot. Proves the answer box controls a running script.
    /// </summary>
    public static int InputTest(string shotPath)
    {
        // Deliberately in a folder with a space, to exercise the command-line quoting.
        var folder = Path.Combine(Path.GetTempPath(), "wa input test");
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "input-mock.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "echo MENU-START\r\n" +
            "echo [1] Windows\r\n" +
            "echo [2] Office\r\n" +
            "echo [3] Tools\r\n" +
            "choice /c 123 /n /m \"pick: \"\r\n" +
            "echo PICKED=%errorlevel%\r\n" +
            // Mimic MAS: paint a result near the top, then jump to a low row.
            "echo \u001b[20;1H\r\n" +
            "echo BOTTOM LINE\r\n" +
            "echo \u001b[92mGREEN LINE\u001b[0m\r\n" +
            "exit /b 0\r\n",
            Encoding.ASCII);

        using var form = new MainForm();
        var raw = new StringBuilder();
        form.RawOutputForTest = raw;
        form.Shown += async (_, _) =>
        {
            try
            {
                form.UseEngineForTest(script);
                form.StartForTest("", "Interactive test");

                await WaitAsync(() => form.LogTextForTest.Contains("pick:"), 10);
                form.SendForTest("2");

                await WaitAsync(() => form.LogTextForTest.Contains("PICKED=2"), 10);
                await WaitAsync(() => !form.RunningForTest, 10);
                await Task.Delay(400);

                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                File.WriteAllText(shotPath + ".txt", form.LogTextForTest, new UTF8Encoding(false));
                File.WriteAllText(shotPath + ".raw.txt", raw.ToString().Replace("\u001b", "<ESC>"), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                File.WriteAllText(shotPath + ".txt", "exception: " + ex, new UTF8Encoding(false));
            }
            finally
            {
                form.Close();
            }
        };

        Application.Run(form);

        var log = File.Exists(shotPath + ".txt") ? File.ReadAllText(shotPath + ".txt") : "";
        var condensed = !log.Contains("\n\n\n");
        if (!condensed) File.AppendAllText(shotPath + ".txt", "\n\n[FAIL: blank-line runs remain]\n");
        return log.Contains("PICKED=2") && log.Contains("BOTTOM LINE") && condensed ? 0 : 1;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>Renderer-level check: feed a pty-style stream and inspect the resulting text.</summary>
    public static int TermTest(string reportPath)
    {
        using var form = new Form { Size = new Size(800, 400), ShowInTaskbar = false };
        var box = new RichTextBox { Dock = DockStyle.Fill, WordWrap = true, ReadOnly = true, Font = new Font("Consolas", 9F) };
        form.Controls.Add(box);
        form.Show();
        Application.DoEvents();

        var view = new TerminalView(box);
        var report = new StringBuilder();

        void Step(string label, string chunk)
        {
            if (chunk.Length > 0) view.Enqueue(chunk);
            view.Pump();
            view.Pump(); // an idle tick, as the timer would do
            report.AppendLine($"--- {label} ---");
            report.AppendLine(box.Text.Replace("\r", "\\r").Replace("\n", "\\n"));
        }

        Step("header", "MENU-START\r\n[1] Windows\r\n[2] Office\r\n");
        Step("prompt", "pick:");
        Step("colours", "\u001b[1C\u001b[?25h");
        Step("answer", "2\r\nPICKED=2\r\n");
        Step("clear", "\u001b[?25l\u001b[2J\u001b[m\u001b[H");
        Step("after clear", "\u001b[92mGREEN LINE\u001b[0m\r\n");

        var text = box.Text;
        form.Close();
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        return text.Contains("pick: 2") && text.Contains("PICKED=2") ? 0 : 1;
    }

    /// <summary>
    /// Reproduces the "huge blank gap" screen: a mock script clears the screen, prints
    /// at the top, then jumps to a low row. Dumps the raw pty bytes and what the log
    /// renderer makes of them.
    /// </summary>
    public static int PtyGap(string reportPath)
    {
        var script = Path.Combine(Path.GetTempPath(), "gap-mock.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "cls\r\n" +
            "echo TOP LINE\r\n" +
            "echo \u001b[31;1H\r\n" +
            "echo BOTTOM LINE\r\n" +
            "echo \u001b[2J\u001b[Hsecond screen\r\n" +
            "echo \u001b[3;1HLEFT-1\r\n" +
            "echo \u001b[3;40HRIGHT-1\r\n" +
            "exit /b 0\r\n",
            Encoding.ASCII);

        var stream = RunScriptThroughPty(script, out var error);
        if (stream is null)
        {
            File.WriteAllText(reportPath, "FAIL: " + error + "\n");
            return 1;
        }

        var rendered = RenderStream(stream).Replace("\r", "\\r").Replace("\n", "\\n");

        var columnsOk = RenderStream(stream)
            .Split('\n')
            .Any(line => line.Contains("LEFT-1") && line.Contains("RIGHT-1"));

        var report = new StringBuilder();
        report.AppendLine("--- raw pty stream ---");
        report.AppendLine(stream.Replace("\u001b", "<ESC>").Replace("\r", "\\r").Replace("\n", "\\n\r\n"));
        report.AppendLine("--- rendered log ---");
        report.AppendLine(rendered);
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));

        return rendered.Contains("TOP LINE") && rendered.Contains("BOTTOM LINE") &&
               !rendered.Contains("TOP LINE\\n\\n\\n\\n") && columnsOk ? 0 : 1;
    }

    /// <summary>Scrollback: 60 printed lines must all survive the 38-row screen.</summary>
    public static int PtyScroll(string reportPath)
    {
        var script = Path.Combine(Path.GetTempPath(), "scroll-mock.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "for /l %%i in (1,1,60) do echo LINE-%%i\r\n" +
            "exit /b 0\r\n",
            Encoding.ASCII);

        var stream = RunScriptThroughPty(script, out var error);
        if (stream is null)
        {
            File.WriteAllText(reportPath, "FAIL: " + error + "\n");
            return 1;
        }

        var rendered = RenderStream(stream);

        var missing = new List<string>();
        for (var i = 1; i <= 60; i++)
            if (!rendered.Contains($"LINE-{i}")) missing.Add("LINE-" + i);

        var report = new StringBuilder();
        report.AppendLine("missing: " + (missing.Count == 0 ? "none" : string.Join(", ", missing)));
        report.AppendLine("first is above last: " +
            (rendered.IndexOf("LINE-1\n", StringComparison.Ordinal) is var a && a >= 0 &&
             rendered.IndexOf("LINE-60", StringComparison.Ordinal) is var b && b > a));
        report.AppendLine("--- rendered log ---");
        report.AppendLine(rendered);
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));

        return missing.Count == 0 && rendered.Contains("LINE-1\n") ? 0 : 1;
    }

    private static string? RunScriptThroughPty(string script, out string error)
    {
        var raw = new StringBuilder();
        var exited = new ManualResetEventSlim();
        using var session = new ConsoleSession();
        session.Output += chunk => { lock (raw) raw.Append(chunk); };
        session.Exited += _ => exited.Set();

        var command = Path.Combine(Environment.SystemDirectory, "cmd.exe") + $" /c \"\"{script}\"\"";
        if (!session.Start(command, Path.GetTempPath()))
        {
            error = session.LastError;
            return null;
        }

        exited.Wait(TimeSpan.FromSeconds(10));
        Thread.Sleep(300);
        session.Dispose();

        error = "";
        lock (raw) return raw.ToString();
    }

    private static string RenderStream(string stream)
    {
        using var form = new Form { Size = new Size(900, 500), ShowInTaskbar = false };
        var box = new RichTextBox { Dock = DockStyle.Fill, WordWrap = true, ReadOnly = true, Font = new Font("Consolas", 9F) };
        form.Controls.Add(box);
        form.Show();
        Application.DoEvents();

        var view = new TerminalView(box);
        view.Enqueue(stream);
        view.Pump();
        Application.DoEvents();

        var text = box.Text;
        form.Close();
        return text;
    }

    /// <summary>
    /// Exercises the launcher channel: version comparison, release parsing, asset selection,
    /// and the image swap, without touching the running build. Also reports what the live
    /// release feed currently holds.
    /// </summary>
    public static int AppUpdate(string reportPath)
    {
        var report = new StringBuilder();
        var folder = Path.Combine(Path.GetTempPath(), "wa-app-test-" + Guid.NewGuid().ToString("N")[..8]);
        var pass = false;

        try
        {
            Directory.CreateDirectory(folder);

            var newer = AppInfo.Compare("1.0.8", "1.0.7") > 0;
            var same = AppInfo.Compare("1.0.7", "1.0.7") == 0;
            var older = AppInfo.Compare("1.0.6", "1.0.7") < 0;
            var padded = AppInfo.Compare("1.0.7.0", "1.0.7") == 0;
            report.AppendLine("compare:     " + (newer && same && older && padded ? "OK" : "FAIL"));

            const string json = """
                {
                  "tag_name": "v1.0.8",
                  "html_url": "https://github.com/eddshine/py_hello_world/releases/tag/v1.0.8",
                  "draft": false,
                  "prerelease": false,
                  "assets": [
                    { "name": "notes.txt", "size": 10 },
                    {
                      "name": "WinAct 1.0.8.exe",
                      "size": 65915955,
                      "browser_download_url": "https://example.invalid/WinAct%201.0.8.exe",
                      "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    }
                  ]
                }
                """;

            var release = AppUpdater.Parse(json);
            var parsed = release is not null
                && release.Version == "1.0.8"
                && release.DownloadUrl.EndsWith("WinAct%201.0.8.exe", StringComparison.Ordinal)
                && release.Sha256 == new string('a', 64);
            report.AppendLine("parse:       " + (parsed ? "OK" : "FAIL"));

            var refused = false;
            try { _ = AppUpdater.Parse("""{"tag_name":"v9.9.9","assets":[]}"""); }
            catch (InvalidDataException) { refused = true; }
            report.AppendLine("no asset:    " + (refused ? "rejected" : "ACCEPTED (!!)"));

            var target = Path.Combine(folder, "WinAct 1.0.7.exe");
            var staged = Path.Combine(folder, "staged.exe");
            File.WriteAllText(target, "old build");
            File.WriteAllText(staged, "new build");
            AppUpdater.Install(staged, target, out var backup);
            var installed = File.ReadAllText(target) == "new build" && File.ReadAllText(backup) == "old build";
            report.AppendLine("install:     " + (installed ? "OK (old kept as .old)" : "FAIL"));

            var selfOk = AppUpdater.CanSelfInstall(out var reason);
            report.AppendLine("self-install: " + (selfOk ? "allowed" : "blocked (" + reason + ")"));

            var live = AppUpdater.GetLatestAsync().GetAwaiter().GetResult();
            report.AppendLine("live feed:   " + (live is null ? "no release yet" : "release " + live.Version));

            pass = newer && same && older && padded && parsed && refused && installed;
            report.AppendLine("result:      " + (pass ? "PASS" : "FAIL"));
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.AppendLine("exception:   " + ex);
            report.AppendLine("result:      FAIL");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            return 1;
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    /// <summary>
    /// Checks the embedded engine without running it: the resource is present, extracts to a
    /// per-process data folder, validates, uses CRLF, is outside the temp folder the engine
    /// refuses, and carries the version the launcher was built for.
    /// </summary>
    public static int EngineTest(string reportPath)
    {
        var report = new StringBuilder();
        var pass = false;

        try
        {
            var embedded = Engine.ReadEmbedded();
            report.AppendLine("embedded:    " + embedded.Length.ToString("N0") + " bytes");
            report.AppendLine("sha256:      " + Convert.ToHexString(SHA256.HashData(embedded)).ToLowerInvariant());

            var path = Engine.Prepare(out var error);
            report.AppendLine("extract:     " + (path ?? "FAILED (" + error + ")"));

            if (path is not null)
            {
                var problem = Engine.Validate(path, out var version);
                report.AppendLine("validate:    " + (problem ?? "OK") + " (version " + version + ")");

                var bytes = File.ReadAllBytes(path);
                var loneLf = false;
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] != (byte)'\n') continue;
                    if (i == 0 || bytes[i - 1] != (byte)'\r') { loneLf = true; break; }
                }
                report.AppendLine("crlf:        " + (loneLf ? "FAIL (bare LF found)" : "OK"));
                report.AppendLine("temp gate:   " + (Engine.InTempFolder(path) ? "FAIL (the engine refuses to run from a temp folder)" : "OK (outside temp)"));
                report.AppendLine("app:         " + AppInfo.Version);
                report.AppendLine("engine:      " + version + " (separate version scheme, shown only in release notes)");

                var appReadable = AppInfo.FromTag(AppInfo.Version) is not null;
                pass = problem is null && !loneLf && !Engine.InTempFolder(path) && appReadable;
            }

            report.AppendLine("result:      " + (pass ? "PASS" : "FAIL"));
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.AppendLine("exception:   " + ex);
            report.AppendLine("result:      FAIL");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            return 1;
        }
        finally
        {
            Engine.Cleanup();
        }
    }

    /// <summary>
    /// Starts the embedded engine exactly the way the app does and waits for its main menu. This
    /// is what proves the extraction folder is accepted: a temp folder makes the engine print a
    /// refusal and exit instead of showing the menu. Needs an elevated process, because the engine
    /// asks for UAC otherwise; when it is not elevated the check reports the gate result only.
    /// </summary>
    public static int EngineLaunch(string reportPath)
    {
        var report = new StringBuilder();
        var buffer = new StringBuilder();
        var pass = false;
        ConsoleSession? session = null;

        void Save()
        {
            report.AppendLine("result:      " + (pass ? "PASS" : "FAIL"));
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        }

        try
        {
            var path = Engine.Prepare(out var error);
            report.AppendLine("engine:      " + (path ?? "FAILED (" + error + ")"));

            if (path is not null)
            {
                var inTemp = Engine.InTempFolder(path);
                report.AppendLine("temp gate:   " + (inTemp
                    ? "FAIL (the engine refuses to run from a temp folder)"
                    : "OK (outside " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Temp") + ")"));

                if (!IsElevated())
                {
                    report.AppendLine("launch:      skipped (not elevated; the engine would request UAC)");
                    pass = !inTemp;
                    Save();
                    return pass ? 0 : 1;
                }

                session = new ConsoleSession();
                session.Output += chunk => { lock (buffer) buffer.Append(chunk); };

                var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                var command = $"{cmd} /c \"\"{path}\" -qedit\"";
                var started = session.Start(command, Path.GetDirectoryName(path)!);
                report.AppendLine("start:       " + (started ? "OK" : "FAILED (" + session.LastError + ")"));
                report.AppendLine("command:     " + command);

                if (started)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline && !Snapshot().Contains("Choose a menu option"))
                        Thread.Sleep(150);

                    var text = Snapshot();
                    var menu = text.Contains("Choose a menu option");
                    var refused = text.Contains("launched from the temp folder", StringComparison.OrdinalIgnoreCase);

                    report.AppendLine("refused:     " + refused);
                    report.AppendLine("menu:        " + (menu ? "OK (main menu shown)" : "missing"));
                    report.AppendLine("--- output ---");
                    report.AppendLine(text.Replace("\u001b", "<ESC>"));
                    pass = menu && !refused;
                }
            }

            Save();
            return pass ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.AppendLine("exception:   " + ex);
            Save();
            return 1;
        }
        finally
        {
            try { session?.Kill(); } catch { }
            try { session?.Dispose(); } catch { }
            Engine.Cleanup();
        }

        string Snapshot()
        {
            lock (buffer) return buffer.ToString();
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Fails if any visible string or tooltip still names the engine or calls the work a
    /// script. The About dialog is deliberately outside this check: it carries the credit.
    /// </summary>
    public static int Wording(string reportPath)
    {
        using var form = new MainForm();
        form.Show();

        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }

        var report = new StringBuilder();
        var problems = new List<string>();

        foreach (var (source, text) in form.VisibleTextForTest())
        {
            report.AppendLine($"{source}: {text.Replace("\r", "").Replace("\n", " ⏎ ")}");
            if (Regex.IsMatch(text, @"\bMAS\b|\bMAS_AIO\b|script|component", RegexOptions.IgnoreCase))
                problems.Add($"{source}: {text}");
        }

        form.Close();

        report.AppendLine();
        report.AppendLine(problems.Count == 0
            ? "result:    PASS"
            : "result:    FAIL" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        return problems.Count == 0 ? 0 : 1;
    }

    /// <summary>Drives the real update check through the message loop and dumps the log.</summary>
    public static int UpdateCheck(string reportPath)
    {
        using var form = new MainForm();
        form.Shown += async (_, _) =>
        {
            try
            {
                await form.UpdateCheckForTest();
                File.WriteAllText(reportPath, form.LogTextForTest, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                File.WriteAllText(reportPath, "exception: " + ex, new UTF8Encoding(false));
            }
            finally
            {
                form.Close();
            }
        };
        Application.Run(form);
        return File.Exists(reportPath) ? 0 : 1;
    }

    /// <summary>Renders the window to a PNG so the layout can be inspected without clicking.</summary>
    public static int Screenshot(string path)
    {
        using var form = new MainForm();
        form.Show();

        var until = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
        return 0;
    }

    public static int Run(string reportPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinAct self-test");
        sb.AppendLine("time:    " + DateTime.Now.ToString("u"));
        sb.AppendLine("base:    " + AppContext.BaseDirectory);
        sb.AppendLine("exe:     " + Environment.ProcessPath);
        sb.AppendLine("64-bit:  " + Environment.Is64BitProcess);
        sb.AppendLine("os:      " + Environment.OSVersion.VersionString);

        try
        {
            using var form = new MainForm();
            _ = form.Handle;              // force handle creation + layout
            form.CreateControl();
            sb.AppendLine("form:    OK " + form.Size);
            var status = form.RunStatusQueryForTest();
            var engine = form.EnginePathForTest;
            sb.AppendLine("engine:  " + (engine ?? "<not prepared>"));
            sb.AppendLine("status:  " + (status ?? "<no output>"));
            var ok = engine is not null;
            if (engine is not null)
            {
                var problem = Engine.Validate(engine, out var version);
                sb.AppendLine("validate good: " + (problem ?? "OK") + " (version " + version + ")");
                var junk = Path.Combine(Path.GetTempPath(), "not-mas.cmd");
                File.WriteAllText(junk, "@echo off\r\necho hello\r\n");
                sb.AppendLine("validate junk: " + (Engine.Validate(junk, out _) ?? "OK (!!)"));
                File.Delete(junk);
                ok = problem is null;
            }
            sb.AppendLine("result:  " + (ok ? "PASS" : "FAIL (engine not prepared)"));
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
            return ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            sb.AppendLine("exception: " + ex);
            sb.AppendLine("result:  FAIL");
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
            return 1;
        }
    }
}
