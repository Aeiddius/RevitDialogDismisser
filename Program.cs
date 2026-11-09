#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

// Standalone Windows console application (not a Revit add-in).
// Watches ALL running Revit.exe processes and dismisses only dialogs that
// match the user-maintained full warning messages in config.json.
// Uses the title-bar X / cancel path; never clicks Yes or No.
internal static class Program
{
    private const string TargetWindowTitle = "Revit";

    // Keep config.json beside the running executable, NOT in a hardcoded folder.
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "config.json");

    private static string[] _targetDialogMessages = Array.Empty<string>();
    private static string[] _normalizedTargets = Array.Empty<string>();
    private static double _similarityThreshold = 0.90;
    private static DateTime _configModifiedUtc = DateTime.MinValue;
    private static long _configFileLength = -1;
    private static bool _missingConfigWasLogged;

    private const uint EVENT_SYSTEM_DIALOGSTART = 0x0010;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const uint WM_TIMER = 0x0113;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_CLOSE = 0x0010;
    private const int SC_CLOSE = 0xF060;
    private const uint GA_ROOT = 2;
    private const uint GW_OWNER = 4;
    private const int GWL_STYLE = -16;
    private const long WS_THICKFRAME = 0x00040000;

    private const uint SweepIntervalMs = 500;
    private const int AttemptCooldownMs = 5000;
    private const int CloseWaitMs = 220;

    private static readonly object Sync = new();
    private static readonly HashSet<nint> ProcessingWindows = new();
    private static readonly Dictionary<nint, long> LastAttemptTicks = new();

    private static WinEventDelegate? _winEventProc;
    private static GCHandle _winEventProcHandle;
    private static nint _dialogStartHook;
    private static nint _objectShowHook;
    private static UIntPtr _sweepTimer;
    private static uint _mainThreadId;
    private static int _shutdownCalled;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime);

    [STAThread]
    private static void Main(string[] args)
    {
        // Config validation and matching tests do not require Revit.
        ReloadConfigurationIfChanged(force: true);
        if (args.Length == 1 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            if (_normalizedTargets.Length == 0)
                throw new InvalidOperationException("config.json has no valid warnings to test.");
            RunMatchingSelfTests();
            return;
        }

        _mainThreadId = GetCurrentThreadId();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            // Console.CancelKeyPress can run on another thread.
            // PostQuitMessage() there would NOT stop the main thread's loop.
            _ = PostThreadMessage(_mainThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        try
        {
            _winEventProc = WinEventCallback;
            _winEventProcHandle = GCHandle.Alloc(_winEventProc);

            _dialogStartHook = SetWinEventHook(
                EVENT_SYSTEM_DIALOGSTART, EVENT_SYSTEM_DIALOGSTART,
                IntPtr.Zero, _winEventProc, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            _objectShowHook = SetWinEventHook(
                EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
                IntPtr.Zero, _winEventProc, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            if (_dialogStartHook == IntPtr.Zero && _objectShowHook == IntPtr.Zero)
                throw new InvalidOperationException("Could not install Windows event hooks.");

            // Also sweep regularly: dialog-start events can arrive before its
            // text is populated, and some UI frameworks emit different events.
            _sweepTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, SweepIntervalMs, IntPtr.Zero);
            if (_sweepTimer == UIntPtr.Zero)
                throw new InvalidOperationException("Could not install periodic sweep timer.");

            Console.WriteLine("Monitoring all Revit.exe instances for dialogs listed in config.json.");
            Console.WriteLine("The config reloads automatically when saved.");
            Console.WriteLine("Close method: X-equivalent only; never Yes or No.");
            Console.WriteLine("Press Ctrl+C to exit.\n");

            SweepExistingWindows("startup sweep");
            RunMessageLoop();
        }
        finally
        {
            Shutdown();
        }
    }

    // Self-tests run against a temporary in-memory configuration, leaving the
    // actual config.json untouched. No Win32/Revit calls are needed.
    private static void RunMatchingSelfTests()
    {
        string[] savedMessages = _targetDialogMessages;
        string[] savedTargets = _normalizedTargets;
        double savedThreshold = _similarityThreshold;

        string truncation =
            "Some numerical data within the imported file was out of range. " +
            "This numerical data has been truncated.";
        string paperToModel =
            "Import detected no valid elements in the file's Paper space. " +
            "Do you want to import from the Model space?";
        string modelToPaper =
            "Import detected no valid elements in the file's Model space. " +
            "Do you want to import from the Paper space?";
        string numberedWarning =
            "The imported CAD file contains 12 invalid objects and the file cannot be loaded.";

        try
        {
            _targetDialogMessages = new[] { truncation, paperToModel, numberedWarning };
            _normalizedTargets = Array.ConvertAll(_targetDialogMessages, Normalize);
            _similarityThreshold = 0.90;

            (string Name, string Message, int ExpectedIndex)[] tests =
            {
                ("exact numerical warning", truncation, 0),
                ("exact Paper-to-Model prompt", paperToModel, 1),
                ("title/buttons surrounding warning", "Revit\n" + paperToModel + "\nYes\nNo", 1),
                ("line breaks, case, punctuation", "IMPORT DETECTED NO VALID ELEMENTS\nIN THE FILES PAPER SPACE! " +
                    "DO YOU WANT TO IMPORT FROM THE MODEL SPACE", 1),
                ("one wording change", "Import detected no valid elements in the file's Paper space. " +
                    "Do you want to import from Model space?", 1),
                ("one typo", "Some numerical data within the imported file was out of range. " +
                    "This numerical data has been truncatd.", 0),
                ("REVERSED direction must not match", modelToPaper, -1),
                ("removed NO changes the meaning", "Import detected valid elements in the file's Paper space. " +
                    "Do you want to import from the Model space?", -1),
                ("same numerical value", numberedWarning, 2),
                ("changed numerical value", "The imported CAD file contains 13 invalid objects " +
                    "and the file cannot be loaded.", -1),
                ("unrelated prompt", "Do you want to save changes?", -1),
                ("incomplete warning", "Some numerical data was truncated.", -1)
            };

            int passed = 0;
            foreach (var test in tests)
            {
                bool matched = IsTargetDialog(test.Message, out int index, out double score);
                bool correct = index == test.ExpectedIndex && matched == (test.ExpectedIndex >= 0);
                Console.WriteLine($"{(correct ? "PASS" : "FAIL")}: {test.Name}" +
                                  (matched ? $" ({score:P1})" : ""));
                if (!correct)
                    throw new InvalidOperationException("Self-test failed: " + test.Name);
                passed++;
            }
            Console.WriteLine($"All {passed} matching tests passed. " +
                              "Closing actual Revit windows still requires a Windows/Revit test.");
        }
        finally
        {
            _targetDialogMessages = savedMessages;
            _normalizedTargets = savedTargets;
            _similarityThreshold = savedThreshold;
        }

        // Validate the user's actual configured warnings as well.
        for (int i = 0; i < _targetDialogMessages.Length; i++)
        {
            if (!IsTargetDialog(_targetDialogMessages[i], out int index, out double score) ||
                index != i || score < 0.999999)
                throw new InvalidOperationException($"Configured warning {i + 1} does not match itself.");
        }
        Console.WriteLine($"All {_targetDialogMessages.Length} config.json entries match exactly.");
    }

    private static void ReloadConfigurationIfChanged(bool force = false)
    {
        if (!File.Exists(ConfigPath))
        {
            _targetDialogMessages = Array.Empty<string>(); // Fail closed, not silently permissive.
            _normalizedTargets = Array.Empty<string>();
            _configModifiedUtc = DateTime.MinValue;
            _configFileLength = -1;
            if (!_missingConfigWasLogged)
            {
                Console.WriteLine($"[CONFIG] Missing: {ConfigPath}. No dialogs will be closed.");
                _missingConfigWasLogged = true;
            }
            return;
        }

        _missingConfigWasLogged = false;
        FileInfo info = new(ConfigPath);
        if (!force && info.LastWriteTimeUtc == _configModifiedUtc &&
            info.Length == _configFileLength)
            return;

        // Record even an invalid revision: don't spam the log every 500 ms.
        _configModifiedUtc = info.LastWriteTimeUtc;
        _configFileLength = info.Length;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("The top-level JSON value must be an object.");

            double threshold = 0.90;
            if (root.TryGetProperty("similarityThreshold", out JsonElement similarity))
            {
                if (similarity.ValueKind != JsonValueKind.Number ||
                    !similarity.TryGetDouble(out threshold) ||
                    !double.IsFinite(threshold) || threshold < 0.90 || threshold > 1.0)
                    throw new FormatException("similarityThreshold must be between 0.90 and 1.00.");
            }

            if (!root.TryGetProperty("dialogTexts", out JsonElement values) ||
                values.ValueKind != JsonValueKind.Array)
                throw new FormatException("dialogTexts must be a JSON array of full warning messages.");

            var originals = new List<string>();
            var normalized = new List<string>();
            foreach (JsonElement item in values.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new FormatException("Every dialogTexts item must be a string.");

                string text = item.GetString() ?? string.Empty;
                string norm = Normalize(text);
                if (norm.Length < 25)
                    throw new FormatException("Each warning must contain at least 25 characters of message text.");
                if (normalized.Contains(norm))
                    continue;

                originals.Add(text);
                normalized.Add(norm);
            }

            _targetDialogMessages = originals.ToArray();
            _normalizedTargets = normalized.ToArray();
            _similarityThreshold = threshold;
            Console.WriteLine($"[CONFIG] Loaded {_normalizedTargets.Length} warning(s); " +
                              $"minimum similarity {_similarityThreshold:P0}.");
        }
        catch (Exception ex)
        {
            // Invalid JSON must NEVER cause the watcher to close a nonmatching popup.
            _targetDialogMessages = Array.Empty<string>();
            _normalizedTargets = Array.Empty<string>();
            Console.WriteLine($"[CONFIG] Invalid config.json: {ex.Message}");
            Console.WriteLine("[CONFIG] Closing disabled until the configuration is corrected.");
        }
    }

    private static void SweepExistingWindows(string source)
    {
        ReloadConfigurationIfChanged();
        if (_normalizedTargets.Length == 0)
            return;

        EnumWindows((hwnd, _) =>
        {
            TryCloseMatchingDialog(hwnd, source);
            return true;
        }, IntPtr.Zero);

        // Keep the cooldown dictionary bounded over long runs.
        if (LastAttemptTicks.Count > 100)
        {
            var stale = new List<nint>();
            foreach (var entry in LastAttemptTicks)
                if (!IsWindow((IntPtr)entry.Key))
                    stale.Add(entry.Key);
            foreach (nint hwnd in stale)
                LastAttemptTicks.Remove(hwnd);
        }
    }

    private static void WinEventCallback(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return;

        // EVENT_OBJECT_SHOW often refers to a child Text element or button.
        // Normalize it to the containing top-level dialog, not the child.
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero)
            root = hwnd;

        TryCloseMatchingDialog(root,
            eventType == EVENT_SYSTEM_DIALOGSTART ? "dialog start" : "object show");
    }

    private static void TryCloseMatchingDialog(IntPtr hwnd, string source)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
            return;

        if (!GetWindowTitle(hwnd).Equals(TargetWindowTitle, StringComparison.OrdinalIgnoreCase))
            return;

        if (!IsOwnedByRevit(hwnd, out int pid))
            return;

        // Safety: never send a close command to the resizable Revit main window.
        // Modal dialogs normally have an owner, and are usually not resizable.
        IntPtr owner = GetWindow(hwnd, GW_OWNER);
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        if (owner == IntPtr.Zero && (style & WS_THICKFRAME) != 0)
            return;

        if (!TryEnterWindow(hwnd))
            return;

        try
        {
            long now = Environment.TickCount64;
            if (LastAttemptTicks.TryGetValue((nint)hwnd, out long last) &&
                now - last >= 0 && now - last < AttemptCooldownMs)
                return;

            string dialogText = ReadDialogText(hwnd);
            if (!IsTargetDialog(dialogText, out int dialogIndex, out double score))
                return;

            // A failure is retried later, not hundreds of times per second.
            LastAttemptTicks[(nint)hwnd] = now;
            IntPtr previousForeground = GetForegroundWindow();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Matched config warning {dialogIndex + 1} " +
                              $"({score:P1} similarity, {source}, Revit PID {pid}, " +
                              $"HWND 0x{hwnd.ToInt64():X}).");

            bool dismissed = TryCloseLikeTitleBarX(hwnd, out string method);
            if (dismissed)
                Console.WriteLine($"  CLOSED using {method}.");
            else
                Console.WriteLine("  NOT CLOSED. Revit ignored close/cancel; " +
                                  "no Yes/No was selected. Will retry later.");

            // Best effort only; no mouse movement or forced activation.
            RestorePreviousForegroundIfNeeded(previousForeground);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Dialog handling error: {ex.Message}");
        }
        finally
        {
            ExitWindow(hwnd);
        }
    }

    // A title-bar X normally produces WM_SYSCOMMAND/SC_CLOSE, then WM_CLOSE.
    // WindowPattern.Close is another standard, non-clicking close request.
    // We NEVER send WM_COMMAND/IDYES/IDNO, mouse clicks, or kill Revit.exe.
    private static bool TryCloseLikeTitleBarX(IntPtr hwnd, out string method)
    {
        method = "already closed";
        if (IsGone(hwnd))
            return true;

        if (PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero) &&
            WaitUntilGone(hwnd, CloseWaitMs))
        {
            method = "WM_SYSCOMMAND / SC_CLOSE (X equivalent)";
            return true;
        }

        if (IsGone(hwnd))
        {
            method = "SC_CLOSE";
            return true;
        }

        if (PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero) &&
            WaitUntilGone(hwnd, CloseWaitMs))
        {
            method = "WM_CLOSE";
            return true;
        }

        if (IsGone(hwnd))
        {
            method = "WM_CLOSE";
            return true;
        }

        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root.TryGetCurrentPattern(WindowPattern.Pattern, out object patternObject) &&
                patternObject is WindowPattern windowPattern)
            {
                windowPattern.Close();
                if (WaitUntilGone(hwnd, CloseWaitMs))
                {
                    method = "UI Automation WindowPattern.Close";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  UI Automation close not supported: {ex.Message}");
        }

        return IsGone(hwnd);
    }

    private static bool IsGone(IntPtr hwnd) => !IsWindow(hwnd) || !IsWindowVisible(hwnd);

    private static bool WaitUntilGone(IntPtr hwnd, int durationMs)
    {
        const int stepMs = 20;
        for (int t = 0; t < durationMs; t += stepMs)
        {
            if (IsGone(hwnd))
                return true;
            Thread.Sleep(stepMs);
        }
        return IsGone(hwnd);
    }

    // Compare each configured FULL warning against the BEST PART of the UI's
    // collected text. The latter can include "Revit", "Yes", "No", etc.
    // Semi-global Levenshtein distance ignores that surrounding UI chrome.
    // Punctuation and case are normalized first; typos and minor wording edits
    // are tolerated up to the threshold in config.json.
    private static bool IsTargetDialog(string dialogText, out int dialogIndex,
                                       out double similarity)
    {
        string observed = Normalize(dialogText);
        dialogIndex = -1;
        similarity = 0;

        for (int i = 0; i < _normalizedTargets.Length; i++)
        {
            string expected = _normalizedTargets[i];
            if (observed.Length == 0 || expected.Length == 0)
                continue;

            double score = BestSubstringSimilarity(expected, observed,
                                                   out string matchingExcerpt);
            if (score < _similarityThreshold || score <= similarity)
                continue;

            // These two Autodesk prompts contain almost identical words.
            // Fuzzy similarity alone can mistakenly match the REVERSE prompt:
            // "Paper space -> Model space" versus "Model space -> Paper space".
            // Require the two distinctive space names to appear in the same
            // order as the configured full warning.
            if (IsReversedPaperModelDirection(expected, matchingExcerpt) ||
                HasCriticalTokenMismatch(expected, matchingExcerpt))
                continue;

            dialogIndex = i;
            similarity = score;
        }

        return dialogIndex >= 0;
    }

    private static bool IsReversedPaperModelDirection(string expected, string observed)
    {
        const string paper = "paper space";
        const string model = "model space";
        int expectedPaper = expected.IndexOf(paper, StringComparison.Ordinal);
        int expectedModel = expected.IndexOf(model, StringComparison.Ordinal);
        int observedPaper = observed.IndexOf(paper, StringComparison.Ordinal);
        int observedModel = observed.IndexOf(model, StringComparison.Ordinal);

        return expectedPaper >= 0 && expectedModel >= 0 &&
               observedPaper >= 0 && observedModel >= 0 &&
               (expectedPaper < expectedModel) != (observedPaper < observedModel);
    }

    // Words such as "no"/"not" and quantities can change the meaning of a
    // warning completely even when overall edit similarity remains high.
    private static bool HasCriticalTokenMismatch(string expected, string observed)
    {
        const string pattern = @"\b(?:no|not|never|without|\d+)\b";
        MatchCollection expectedTokens = Regex.Matches(expected, pattern);
        MatchCollection observedTokens = Regex.Matches(observed, pattern);
        if (expectedTokens.Count != observedTokens.Count)
            return true;

        for (int i = 0; i < expectedTokens.Count; i++)
            if (!expectedTokens[i].Value.Equals(observedTokens[i].Value,
                                                StringComparison.Ordinal))
                return true;
        return false;
    }

    // Edit distance between expected text and its best matching substring
    // in observed text. 1.00 means identical, 0.90 means ~10% edits.
    // Also tracks the matched substring for the Paper/Model direction check.
    private static double BestSubstringSimilarity(string expected, string observed,
                                                  out string matchingExcerpt)
    {
        int m = expected.Length;
        int n = observed.Length;
        if (m == 0 || n == 0)
        {
            matchingExcerpt = string.Empty;
            return 0;
        }

        int[] previous = new int[n + 1];
        int[] current = new int[n + 1];
        int[] prevStarts = new int[n + 1];
        int[] currStarts = new int[n + 1];
        for (int j = 0; j <= n; j++)
            prevStarts[j] = j; // Ignore text BEFORE the warning for free.

        for (int i = 1; i <= m; i++)
        {
            current[0] = i;
            currStarts[0] = 0;
            for (int j = 1; j <= n; j++)
            {
                int substitution = previous[j - 1] +
                                   (expected[i - 1] == observed[j - 1] ? 0 : 1);
                int deletion = previous[j] + 1;
                int insertion = current[j - 1] + 1;

                if (substitution <= deletion && substitution <= insertion)
                {
                    current[j] = substitution;
                    currStarts[j] = prevStarts[j - 1];
                }
                else if (deletion <= insertion)
                {
                    current[j] = deletion;
                    currStarts[j] = prevStarts[j];
                }
                else
                {
                    current[j] = insertion;
                    currStarts[j] = currStarts[j - 1];
                }
            }
            (previous, current) = (current, previous);
            (prevStarts, currStarts) = (currStarts, prevStarts);
        }

        int bestDistance = int.MaxValue;
        int bestEnd = 0;
        int bestStart = 0;
        for (int j = 0; j <= n; j++) // Ignore text AFTER the warning for free.
        {
            if (previous[j] < bestDistance)
            {
                bestDistance = previous[j];
                bestEnd = j;
                bestStart = prevStarts[j];
            }
        }

        matchingExcerpt = observed.Substring(bestStart, bestEnd - bestStart);
        return Math.Max(0.0, 1.0 - (double)bestDistance / m);
    }

    private static string ReadDialogText(IntPtr hwnd)
    {
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add(value.Trim());
        }

        Add(GetWindowTitle(hwnd));

        // Fallback for native Win32 Static text controls.
        _ = EnumChildWindows(hwnd, (child, _) =>
        {
            Add(GetWindowTitle(child));
            return true;
        }, IntPtr.Zero);

        // Skip the heavier UIA scan when native controls already expose text.
        string text = string.Join(Environment.NewLine, parts);
        if (IsTargetDialog(text, out _, out _))
            return text;

        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            AddAutomationElementText(root, Add);

            AutomationElementCollection descendants = root.FindAll(
                TreeScope.Descendants, Condition.TrueCondition);

            foreach (AutomationElement element in descendants)
                AddAutomationElementText(element, Add);
        }
        catch
        {
            // Conservative: an unreadable dialog is NOT considered a match.
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static void AddAutomationElementText(AutomationElement element, Action<string?> add)
    {
        add(SafeGet(() => element.Current.Name));
        add(SafeGet(() => element.Current.HelpText));

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern) &&
                pattern is ValuePattern valuePattern)
                add(valuePattern.Current.Value);
        }
        catch { }

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern) &&
                pattern is TextPattern textPattern)
                add(textPattern.DocumentRange.GetText(-1));
        }
        catch { }
    }

    private static string SafeGet(Func<string> getter)
    {
        try { return getter() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        // Fold whitespace and punctuation, including curly apostrophes and dashes.
        // Example: "file's" and "file’s" are treated alike.
        return Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
    }

    private static bool IsOwnedByRevit(IntPtr hwnd, out int pid)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        pid = unchecked((int)processId);
        if (pid == 0)
            return false;

        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("Revit", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;

        var sb = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool TryEnterWindow(IntPtr hwnd)
    {
        lock (Sync) return ProcessingWindows.Add((nint)hwnd);
    }

    private static void ExitWindow(IntPtr hwnd)
    {
        lock (Sync) _ = ProcessingWindows.Remove((nint)hwnd);
    }

    private static void RestorePreviousForegroundIfNeeded(IntPtr previousForeground)
    {
        if (previousForeground == IntPtr.Zero || !IsWindow(previousForeground))
            return;

        IntPtr currentForeground = GetForegroundWindow();
        if (currentForeground == previousForeground || currentForeground == IntPtr.Zero)
            return;

        if (IsOwnedByRevit(currentForeground, out _))
            _ = SetForegroundWindow(previousForeground);
    }

    private static void RunMessageLoop()
    {
        while (true)
        {
            int result = GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
            if (result == 0) break;
            if (result == -1) throw new InvalidOperationException("GetMessage failed.");

            if (msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero &&
                unchecked((ulong)msg.wParam.ToInt64()) == _sweepTimer.ToUInt64())
            {
                SweepExistingWindows("periodic sweep");
                continue;
            }

            _ = TranslateMessage(ref msg);
            _ = DispatchMessage(ref msg);
        }
    }

    private static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownCalled, 1) != 0)
            return;

        if (_sweepTimer != UIntPtr.Zero)
        {
            _ = KillTimer(IntPtr.Zero, _sweepTimer);
            _sweepTimer = UIntPtr.Zero;
        }
        if (_dialogStartHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_dialogStartHook);
            _dialogStartHook = IntPtr.Zero;
        }
        if (_objectShowHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_objectShowHook);
            _objectShowHook = IntPtr.Zero;
        }
        if (_winEventProcHandle.IsAllocated)
            _winEventProcHandle.Free();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr idEvent, uint elapse, IntPtr timerProc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr idEvent);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate callback, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG msg);
}
