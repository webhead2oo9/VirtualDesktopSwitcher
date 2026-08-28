using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using EasyHook;

namespace VirtualDesktopSwitcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Options options = Options.Parse(args);
                if (options.ShowHelp)
                {
                    Usage.Print();
                    return 0;
                }

                if (options.ConfigureManual)
                {
                    string warning;
                    ManualSettings settings = ManualSettingsStore.Load(out warning);
                    if (!string.IsNullOrEmpty(warning))
                    {
                        Console.Error.WriteLine("WARNING: " + warning);
                    }
                    settings = ManualSettingsConfigurationDialog.Show(settings);
                    ManualSettingsStore.Save(settings);
                    Console.WriteLine("Saved manual settings to " + ManualSettingsStore.SettingsPath);
                    return 0;
                }

                return new Rotator(options).Run();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                if (Environment.GetEnvironmentVariable("VIRTUALDESKTOPSWITCHER_DEBUG") == "1")
                {
                    Console.Error.WriteLine(ex);
                }
                return 1;
            }
        }
    }

    internal sealed class Rotator
    {
        private readonly Options options;
        private readonly ICodecClient client;
        private readonly Func<ManualBinding, RawInputListener> rawInputFactory;
        private readonly ManualResetEvent cancelSignal = new ManualResetEvent(false);
        private volatile bool cancelRequested;
        private RawInputListener manualInput;
        private ManualSettings savedManualSettings;

        public Rotator(Options options)
            : this(options, new CodecInjectionClient(options), CreateRawInputListener)
        {
        }

        internal Rotator(Options options, ICodecClient client)
            : this(options, client, CreateRawInputListener)
        {
        }

        internal Rotator(Options options, ICodecClient client, Func<ManualBinding, RawInputListener> rawInputFactory)
        {
            this.options = options;
            this.client = client;
            this.rawInputFactory = rawInputFactory;
        }

        public int Run()
        {
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                if (!cancelRequested)
                {
                    cancelRequested = true;
                    cancelSignal.Set();
                    e.Cancel = true;
                    Log("Stopping; press Ctrl+C again to force quit.");
                }
            };

            if (!string.IsNullOrEmpty(options.TargetCodec))
            {
                CodecInfo target = CodecCatalog.Resolve(options.TargetCodec);
                CodecOperationResult result = client.SetCodec(target.Code);
                Log("Preferred Codec: " + result.Before + " -> " + result.After);
                return 0;
            }

            if (!options.Once)
            {
                string warning;
                savedManualSettings = ManualSettingsStore.Load(out warning);
                if (!string.IsNullOrEmpty(warning))
                {
                    Log("WARNING: " + warning);
                }
                options.ApplyManualSettings(savedManualSettings);
            }

            List<CodecInfo> codecs = ResolveCodecRotation();
            if (options.SwitchBack && codecs.Count != 2)
            {
                throw new InvalidOperationException("--switch-back requires exactly two codecs: the preferred codec first and the temporary codec second.");
            }

            if (options.Once)
            {
                if (options.SwitchBack)
                {
                    EnsurePreferredCodec(codecs[0]);
                }
                PerformSwitch(codecs);
                return 0;
            }

            try
            {
                InitializeScheduledRun(codecs);

                Log("VirtualDesktopSwitcher started. Backend: in-process; Codecs: " +
                    string.Join(", ", codecs.Select(c => c.Code).ToArray()) +
                    "; timer: " + (options.TimerEnabled ? "on (" + options.IntervalMinutes + " minute(s))" : "off") +
                    "; manual trigger: " + (options.ManualEnabled ? "on (" + options.ManualBinding.DisplayText + ", " + options.ManualAction.ToString().ToLowerInvariant() + ")" : "off") +
                    "; beep warning: " + (options.BeepWarning ? "on" : "off") +
                    "; switch back: " + (options.SwitchBack ? "on (delay " + options.SwitchBackDelayMilliseconds + " ms)" : "off") + ".");

                if (options.SwitchImmediately)
                {
                    PerformAutomaticSwitch(codecs);
                }

                DateTime nextTimerDeadline = DateTime.UtcNow.AddMinutes(options.IntervalMinutes);
                while (true)
                {
                    WorkKind work = WaitForWork(nextTimerDeadline);
                    if (work == WorkKind.Cancel)
                    {
                        break;
                    }

                    bool automaticInputSuppressed = false;
                    if (work == WorkKind.Timer && manualInput != null)
                    {
                        automaticInputSuppressed = manualInput.TryBeginAutomaticOperation();
                        if (!automaticInputSuppressed)
                        {
                            work = WorkKind.Manual;
                        }
                    }

                    if (work == WorkKind.Manual)
                    {
                        manualInput.ConsumeTrigger();
                        manualInput.SetEnabled(false);
                        try
                        {
                            if (!ManualCountdown())
                            {
                                break;
                            }
                            PerformManualAction(codecs);
                            nextTimerDeadline = DateTime.UtcNow.AddMinutes(options.IntervalMinutes);
                        }
                        finally
                        {
                            manualInput.SetEnabled(true);
                        }
                    }
                    else
                    {
                        try
                        {
                            PerformSwitch(codecs);
                        }
                        finally
                        {
                            if (automaticInputSuppressed)
                            {
                                manualInput.SetEnabled(true);
                            }
                        }
                        nextTimerDeadline = DateTime.UtcNow.AddMinutes(options.IntervalMinutes);
                    }

                    if (cancelRequested)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (manualInput != null)
                {
                    manualInput.Dispose();
                    manualInput = null;
                }
            }

            Log("Stopped by user.");
            return 0;
        }

        internal void InitializeScheduledRun(List<CodecInfo> codecs)
        {
            ValidateScheduledRun();
            StartManualInput();
            if (options.SwitchBack)
            {
                EnsurePreferredCodec(codecs[0]);
            }
        }

        private void ValidateScheduledRun()
        {
            if (!options.TimerEnabled && (options.ManualBinding == null || !options.ManualBinding.IsValid))
            {
                throw new InvalidOperationException("Manual-only mode requires a saved binding. Run --configure-manual first.");
            }
            if (!options.TimerEnabled && !options.ManualEnabled)
            {
                throw new InvalidOperationException("Both the automatic timer and manual trigger are disabled. Run --configure-manual to enable and bind a trigger.");
            }
        }

        private void StartManualInput()
        {
            if (!options.ManualEnabled)
            {
                return;
            }
            try
            {
                manualInput = rawInputFactory(options.ManualBinding);
            }
            catch (Exception ex)
            {
                if (!options.TimerEnabled)
                {
                    throw new InvalidOperationException("Manual-only mode could not start Raw Input. " + ex.Message, ex);
                }
                options.ManualEnabled = false;
                Log("WARNING: manual trigger unavailable; continuing in timer mode (" + ex.Message + ").");
            }
        }

        private static RawInputListener CreateRawInputListener(ManualBinding binding)
        {
            return new RawInputListener(binding);
        }

        private void PerformSwitch(List<CodecInfo> codecs)
        {
            if (options.SwitchBack)
            {
                SwitchAwayAndBack(codecs[0], codecs[1]);
                return;
            }

            SwitchToNextCodec(codecs);
        }

        private void PerformAutomaticSwitch(List<CodecInfo> codecs)
        {
            if (manualInput == null)
            {
                PerformSwitch(codecs);
                return;
            }
            manualInput.SetEnabled(false);
            try
            {
                PerformSwitch(codecs);
            }
            finally
            {
                manualInput.SetEnabled(true);
            }
        }

        internal void PerformManualAction(List<CodecInfo> codecs)
        {
            if (options.ManualAction == ManualAction.Switch)
            {
                SwitchToNextCodec(codecs);
                return;
            }

            CodecOperationResult snapshot = client.GetCodec();
            CodecInfo temporary = GetNextCodec(snapshot.Current, codecs);
            SwitchAwayAndRestore(snapshot.Current, temporary);
        }

        private void EnsurePreferredCodec(CodecInfo preferred)
        {
            CodecOperationResult current = client.GetCodec();
            if (string.Equals(current.Current, preferred.Code, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CodecOperationResult result = client.SetCodec(preferred.Code);
            Log("Preferred Codec: " + result.Before + " -> " + result.After + " (initial switch-back target)");
        }

        private void SwitchAwayAndBack(CodecInfo preferred, CodecInfo temporary)
        {
            try
            {
                CodecOperationResult awayResult = client.SetCodec(temporary.Code);
                Log("Preferred Codec: " + awayResult.Before + " -> " + awayResult.After + " (temporary)");
                if (options.SwitchBackDelayMilliseconds > 0)
                {
                    cancelSignal.WaitOne(options.SwitchBackDelayMilliseconds);
                }
            }
            catch
            {
                try
                {
                    client.SetCodec(preferred.Code);
                }
                catch (Exception restoreEx)
                {
                    Log("WARNING: could not restore preferred codec '" + preferred.Code + "' (" + restoreEx.Message + ").");
                }
                throw;
            }

            CodecOperationResult restoreResult = RestorePreferredCodec(preferred);
            Log("Preferred Codec: " + restoreResult.Before + " -> " + restoreResult.After + " (restored)");
        }

        private CodecOperationResult RestorePreferredCodec(CodecInfo preferred)
        {
            try
            {
                return client.SetCodec(preferred.Code);
            }
            catch (Exception ex)
            {
                Log("WARNING: could not restore preferred codec '" + preferred.Code + "' (" + ex.Message + "); retrying once.");
                return client.SetCodec(preferred.Code);
            }
        }

        private void SwitchAwayAndRestore(string originalCodec, CodecInfo temporary)
        {
            try
            {
                CodecOperationResult awayResult = client.SetCodec(temporary.Code);
                Log("Preferred Codec: " + awayResult.Before + " -> " + awayResult.After + " (manual restart temporary)");
                if (options.ManualRestartDelayMilliseconds > 0)
                {
                    cancelSignal.WaitOne(options.ManualRestartDelayMilliseconds);
                }
            }
            catch
            {
                TryRestoreAfterFailure(originalCodec);
                throw;
            }

            CodecOperationResult restoreResult = RestoreCodec(originalCodec);
            Log("Preferred Codec: " + restoreResult.Before + " -> " + restoreResult.After + " (manual restart restored)");
        }

        private void TryRestoreAfterFailure(string codec)
        {
            try
            {
                client.SetCodec(codec);
            }
            catch (Exception restoreEx)
            {
                Log("WARNING: could not restore original codec '" + codec + "' (" + restoreEx.Message + ").");
            }
        }

        private CodecOperationResult RestoreCodec(string codec)
        {
            try
            {
                return client.SetCodec(codec);
            }
            catch (Exception ex)
            {
                Log("WARNING: could not restore original codec '" + codec + "' (" + ex.Message + "); retrying once.");
                return client.SetCodec(codec);
            }
        }

        private WorkKind WaitForWork(DateTime timerDeadline)
        {
            TimeSpan warning = TimeSpan.FromSeconds(5);
            WaitHandle[] handles = manualInput == null
                ? new WaitHandle[] { cancelSignal }
                : new WaitHandle[] { cancelSignal, manualInput.TriggerSignal };

            if (!options.TimerEnabled)
            {
                int signal = WaitHandle.WaitAny(handles);
                return signal == 0 ? WorkKind.Cancel : WorkKind.Manual;
            }

            DateTime warningDeadline = options.BeepWarning ? timerDeadline.Subtract(warning) : timerDeadline;
            int waitMilliseconds = MillisecondsUntil(warningDeadline);
            int result = WaitHandle.WaitAny(handles, waitMilliseconds);
            if (result == 0)
            {
                return WorkKind.Cancel;
            }
            if (result == 1)
            {
                return WorkKind.Manual;
            }

            if (!options.BeepWarning || DateTime.UtcNow >= timerDeadline)
            {
                return WorkKind.Timer;
            }

            Log("Switching codec in 5 second(s).");
            for (int remaining = 5; remaining > 0; remaining--)
            {
                Stopwatch tick = Stopwatch.StartNew();
                PlayBeep();
                int remainingMilliseconds = Math.Max(0, 1000 - (int)tick.ElapsedMilliseconds);
                result = WaitHandle.WaitAny(handles, remainingMilliseconds);
                if (result == 0) return WorkKind.Cancel;
                if (result == 1) return WorkKind.Manual;
            }

            if (manualInput != null && manualInput.TriggerSignal.WaitOne(0))
            {
                return WorkKind.Manual;
            }
            return WorkKind.Timer;
        }

        private static int MillisecondsUntil(DateTime deadline)
        {
            double milliseconds = (deadline - DateTime.UtcNow).TotalMilliseconds;
            if (milliseconds <= 0) return 0;
            if (milliseconds >= int.MaxValue) return int.MaxValue;
            return (int)Math.Ceiling(milliseconds);
        }

        private bool ManualCountdown()
        {
            for (int remaining = 3; remaining > 0; remaining--)
            {
                Stopwatch tick = Stopwatch.StartNew();
                Log("Manual action in " + remaining + ".");
                PlayBeep();

                int remainingMilliseconds = Math.Max(0, 1000 - (int)tick.ElapsedMilliseconds);
                if (cancelSignal.WaitOne(remainingMilliseconds))
                {
                    return false;
                }
            }

            return true;
        }

        private static void PlayBeep()
        {
            try
            {
                Console.Beep(1200, 160);
            }
            catch
            {
                // Beep support depends on the active Windows audio/session setup.
            }
        }

        private enum WorkKind
        {
            Cancel,
            Manual,
            Timer
        }

        private List<CodecInfo> ResolveCodecRotation()
        {
            if (options.Codecs.Count > 0)
            {
                List<CodecInfo> explicitCodecs = options.Codecs.Select(CodecCatalog.Resolve).ToList();
                if (explicitCodecs.Select(c => c.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
                {
                    throw new InvalidOperationException("Specify at least two different codecs for rotation, or use --target-codec to set one exact codec.");
                }
                return explicitCodecs;
            }

            CodecOperationResult current = client.GetCodec();
            CodecInfo currentCodec = CodecCatalog.Resolve(current.Current);
            ToggleSelection selection = ToggleSelectionDialog.Show(currentCodec, options.IntervalMinutes, options.BeepWarning, options.SwitchBack, options.SwitchBackDelayMilliseconds, options.Once ? null : savedManualSettings);
            options.IntervalMinutes = selection.IntervalMinutes;
            options.BeepWarning = selection.BeepWarning;
            options.SwitchBack = selection.SwitchBack;
            options.SwitchBackDelayMilliseconds = selection.SwitchBackDelayMilliseconds;
            if (!options.Once && selection.ManualSettings != null)
            {
                ManualSettingsStore.Save(selection.ManualSettings);
                savedManualSettings = selection.ManualSettings;
                options.ApplyManualSettings(savedManualSettings);
            }

            CodecInfo first = selection.SwitchBack ? selection.SwitchBackCodec : currentCodec;

            Log("Toggle pair selected: " + first.Code + ", " + selection.Codec.Code +
                "; interval: " + options.IntervalMinutes + " minute(s)" +
                "; beep warning: " + (options.BeepWarning ? "on" : "off") + ".");

            return new List<CodecInfo> { first, selection.Codec };
        }

        internal void SwitchToNextCodec(List<CodecInfo> codecs)
        {
            CodecOperationResult current = client.GetCodec();
            CodecInfo next = GetNextCodec(current.Current, codecs);
            CodecOperationResult result = client.SetCodec(next.Code);
            Log("Preferred Codec: " + result.Before + " -> " + result.After);
        }

        internal static CodecInfo GetNextCodec(string currentCodec, List<CodecInfo> codecs)
        {
            int index = -1;
            for (int i = 0; i < codecs.Count; i++)
            {
                if (string.Equals(codecs[i].Code, currentCodec, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                Log("Current codec '" + currentCodec + "' is not in the rotation list; starting from '" + codecs[0].Code + "'.");
                return codecs[0];
            }

            return codecs[(index + 1) % codecs.Count];
        }

        private static void Log(string message)
        {
            Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message);
        }
    }

    internal interface ICodecClient
    {
        CodecOperationResult GetCodec();
        CodecOperationResult SetCodec(string codec);
    }

    internal sealed class CodecInjectionClient : ICodecClient
    {
        private readonly Options options;
        private readonly string baseDirectory;
        private readonly string payloadPath;

        public CodecInjectionClient(Options options)
        {
            this.options = options;
            this.baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            this.payloadPath = Path.Combine(baseDirectory, "VdCodecPayload.dll");
            UnblockHelperFiles();
        }

        // A downloaded release carries the "downloaded from the Internet" Mark-of-the-Web
        // (a Zone.Identifier stream) on every bundled file. The .exe itself gets cleared by
        // the SmartScreen "Run anyway" prompt, but the helper DLLs stay blocked, and Windows
        // refuses to load a blocked DLL into the target during injection -- which surfaces as
        // "STATUS_INTERNAL_ERROR: Unknown error in injected C++ completion routine". Strip the
        // stream off our siblings so injection works straight out of a downloaded zip.
        private void UnblockHelperFiles()
        {
            string[] files =
            {
                "VdCodecPayload.dll",
                "VdCodecWorker.dll",
                "EasyHook.dll",
                "EasyHook64.dll",
                "EasyLoad64.dll",
                "EasyHook64Svc.exe"
            };

            foreach (string file in files)
            {
                try
                {
                    string path = Path.Combine(baseDirectory, file);
                    if (File.Exists(path))
                    {
                        DeleteFile(path + ":Zone.Identifier");
                    }
                }
                catch
                {
                    // Best effort: if we can't clear the stream, injection may still fail with
                    // its own error, but unblocking must never abort startup itself.
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFile(string name);

        public CodecOperationResult GetCodec()
        {
            return Invoke("Get", string.Empty);
        }

        public CodecOperationResult SetCodec(string codec)
        {
            return Invoke("Set", codec);
        }

        private CodecOperationResult Invoke(string operation, string codec)
        {
            if (!File.Exists(payloadPath))
            {
                throw new FileNotFoundException("Missing payload DLL.", payloadPath);
            }

            Process process = FindStreamerProcess();
            string resultPath = Path.Combine(Path.GetTempPath(), "VirtualDesktopSwitcher-" + Guid.NewGuid().ToString("N") + ".txt");

            try
            {
                RemoteHooking.Inject(
                    process.Id,
                    InjectionOptions.DoNotRequireStrongName,
                    payloadPath,
                    payloadPath,
                    operation,
                    codec ?? string.Empty,
                    resultPath);

                return WaitForResult(resultPath);
            }
            finally
            {
                try { File.Delete(resultPath); } catch { }
            }
        }

        private Process FindStreamerProcess()
        {
            DateTime deadline = DateTime.Now.AddSeconds(options.TimeoutSeconds);
            bool started = false;

            while (DateTime.Now < deadline)
            {
                Process process = Process.GetProcessesByName("VirtualDesktop.Streamer")
                    .Where(p => !p.HasExited)
                    .OrderBy(p => p.Id)
                    .FirstOrDefault();

                if (process != null)
                {
                    return process;
                }

                if (!started && File.Exists(options.StreamerPath))
                {
                    Process.Start(options.StreamerPath);
                    started = true;
                }

                Thread.Sleep(250);
            }

            throw new InvalidOperationException("Could not find or start VirtualDesktop.Streamer.exe.");
        }

        private CodecOperationResult WaitForResult(string resultPath)
        {
            DateTime deadline = DateTime.Now.AddSeconds(options.TimeoutSeconds);

            while (DateTime.Now < deadline)
            {
                if (File.Exists(resultPath))
                {
                    string line = File.ReadAllLines(resultPath).FirstOrDefault();
                    if (!string.IsNullOrEmpty(line))
                    {
                        return CodecOperationResult.Parse(line);
                    }
                }

                Thread.Sleep(100);
            }

            throw new TimeoutException("Timed out waiting for the in-process codec helper to report a result.");
        }
    }

    internal sealed class CodecOperationResult
    {
        public string Operation;
        public string Before;
        public string After;
        public string Current;

        public static CodecOperationResult Parse(string line)
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 0)
            {
                throw new InvalidOperationException("Empty helper result.");
            }

            if (parts[0] == "ERROR")
            {
                string message = parts.Length > 1 ? Decode(parts[1]) : "Unknown helper error.";
                throw new InvalidOperationException(message);
            }

            if (parts[0] != "OK" || parts.Length < 5)
            {
                throw new InvalidOperationException("Unexpected helper result: " + line);
            }

            return new CodecOperationResult
            {
                Operation = Decode(parts[1]),
                Before = Decode(parts[2]),
                After = Decode(parts[3]),
                Current = Decode(parts[4])
            };
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
    }

    internal sealed class ToggleSelection
    {
        public CodecInfo Codec;
        public int IntervalMinutes;
        public bool BeepWarning;
        public bool SwitchBack;
        public CodecInfo SwitchBackCodec;
        public int SwitchBackDelayMilliseconds;
        public ManualSettings ManualSettings;
    }

    internal sealed class CodecInfo
    {
        public readonly string Code;
        public readonly string Display;

        public CodecInfo(string code, string display)
        {
            Code = code;
            Display = display;
        }
    }

    internal static class CodecCatalog
    {
        public static readonly List<CodecInfo> All = new List<CodecInfo>
        {
            new CodecInfo("Automatic", "Automatic"),
            new CodecInfo("H264", "H.264"),
            new CodecInfo("H264Plus", "H.264+"),
            new CodecInfo("HEVC", "HEVC"),
            new CodecInfo("HEVC10bit", "HEVC 10-bit"),
            new CodecInfo("AV110bit", "AV1 10-bit")
        };

        public static CodecInfo Resolve(string name)
        {
            string key = ToKey(name);

            foreach (CodecInfo codec in All)
            {
                if (ToKey(codec.Code) == key || ToKey(codec.Display) == key)
                {
                    return codec;
                }
            }

            throw new InvalidOperationException("Unknown codec '" + name + "'. Known codec codes: " +
                string.Join(", ", All.Select(c => c.Code).ToArray()));
        }

        public static string[] GetSuggestedToggleOrder(string currentCode)
        {
            switch (ToKey(currentCode))
            {
                case "HEVC10BIT":
                    return new[] { "HEVC", "H264Plus", "H264", "AV110bit", "Automatic" };
                case "HEVC":
                    return new[] { "HEVC10bit", "H264Plus", "H264", "AV110bit", "Automatic" };
                case "H264PLUS":
                    return new[] { "HEVC10bit", "HEVC", "H264", "AV110bit", "Automatic" };
                case "H264":
                    return new[] { "H264Plus", "HEVC10bit", "HEVC", "AV110bit", "Automatic" };
                case "AV110BIT":
                    return new[] { "HEVC10bit", "HEVC", "H264Plus", "H264", "Automatic" };
                default:
                    return new[] { "HEVC10bit", "HEVC", "H264Plus", "H264", "AV110bit" };
            }
        }

        private static string ToKey(string name)
        {
            StringBuilder builder = new StringBuilder();
            string normalized = (name ?? string.Empty).ToUpperInvariant().Replace("+", "PLUS");
            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }
    }

    internal sealed class Options
    {
        public readonly List<string> Codecs = new List<string>();
        public int IntervalMinutes = 25;
        public bool BeepWarning;
        public string TargetCodec;
        public bool Once;
        public bool SwitchImmediately;
        public bool SwitchBack;
        public int SwitchBackDelayMilliseconds = 50;
        public bool ConfigureManual;
        public bool? TimerEnabledOverride;
        public bool NoManualTrigger;
        public ManualAction? ManualActionOverride;
        public bool TimerEnabled = true;
        public bool ManualEnabled;
        public ManualBinding ManualBinding;
        public ManualAction ManualAction = ManualAction.Restart;
        public int ManualRestartDelayMilliseconds = 50;
        public bool ShowHelp;
        public string StreamerPath = @"C:\Program Files\Virtual Desktop Streamer\VirtualDesktop.Streamer.exe";
        public int TimeoutSeconds = 15;

        public static Options Parse(string[] args)
        {
            Options options = new Options();
            bool switchBackDelaySpecified = false;

            for (int i = 0; i < args.Length; i++)
            {
                string name = NormalizeOption(args[i]);
                switch (name)
                {
                    case "HELP":
                    case "H":
                    case "?":
                        options.ShowHelp = true;
                        break;
                    case "CODECS":
                        options.Codecs.AddRange(SplitCodecList(RequireValue(args, ref i, args[i])));
                        break;
                    case "INTERVALMINUTES":
                    case "INTERVAL":
                        options.IntervalMinutes = ParseRange(RequireValue(args, ref i, args[i]), "IntervalMinutes", 1, 10080);
                        break;
                    case "BEEPWARNING":
                    case "BEEP":
                    case "WARNINGBEEP":
                        options.BeepWarning = true;
                        break;
                    case "NOBEEPWARNING":
                    case "NOBEEP":
                    case "SILENT":
                        options.BeepWarning = false;
                        break;
                    case "TARGETCODEC":
                    case "TARGET":
                        options.TargetCodec = RequireValue(args, ref i, args[i]);
                        break;
                    case "ONCE":
                        options.Once = true;
                        break;
                    case "SWITCHIMMEDIATELY":
                        options.SwitchImmediately = true;
                        break;
                    case "SWITCHBACK":
                        options.SwitchBack = true;
                        break;
                    case "SWITCHBACKDELAYMS":
                    case "SWITCHBACKDELAY":
                        options.SwitchBackDelayMilliseconds = ParseRange(RequireValue(args, ref i, args[i]), "SwitchBackDelayMs", 0, 60000);
                        switchBackDelaySpecified = true;
                        break;
                    case "CONFIGUREMANUAL":
                        options.ConfigureManual = true;
                        break;
                    case "MANUALONLY":
                        options.TimerEnabledOverride = false;
                        break;
                    case "ENABLETIMER":
                        options.TimerEnabledOverride = true;
                        break;
                    case "MANUALACTION":
                        string action = RequireValue(args, ref i, args[i]);
                        if (string.Equals(action, "switch", StringComparison.OrdinalIgnoreCase))
                        {
                            options.ManualActionOverride = ManualAction.Switch;
                        }
                        else if (string.Equals(action, "restart", StringComparison.OrdinalIgnoreCase))
                        {
                            options.ManualActionOverride = ManualAction.Restart;
                        }
                        else
                        {
                            throw new InvalidOperationException("--manual-action must be 'switch' or 'restart'.");
                        }
                        break;
                    case "NOMANUALTRIGGER":
                        options.NoManualTrigger = true;
                        break;
                    case "STREAMERPATH":
                        options.StreamerPath = RequireValue(args, ref i, args[i]);
                        break;
                    case "TIMEOUTSECONDS":
                    case "TIMEOUT":
                        options.TimeoutSeconds = ParseRange(RequireValue(args, ref i, args[i]), "TimeoutSeconds", 1, 300);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown argument: " + args[i]);
                }
            }

            if (options.IntervalMinutes < 1 || options.IntervalMinutes > 10080)
            {
                throw new InvalidOperationException("IntervalMinutes must be between 1 and 10080.");
            }

            if (options.SwitchBack && !string.IsNullOrEmpty(options.TargetCodec))
            {
                throw new InvalidOperationException("--switch-back cannot be combined with --target-codec.");
            }

            if (switchBackDelaySpecified && !options.SwitchBack)
            {
                throw new InvalidOperationException("--switch-back-delay-ms requires --switch-back.");
            }

            return options;
        }

        public void ApplyManualSettings(ManualSettings settings)
        {
            TimerEnabled = TimerEnabledOverride.HasValue ? TimerEnabledOverride.Value : settings.TimerEnabled;
            ManualBinding = settings.Binding == null ? null : settings.Binding.Clone();
            ManualEnabled = !NoManualTrigger && ManualBinding != null && ManualBinding.IsValid;
            ManualAction = ManualActionOverride.HasValue ? ManualActionOverride.Value : settings.GetAction();
            ManualRestartDelayMilliseconds = settings.RestartDelayMilliseconds;
        }

        private static string NormalizeOption(string value)
        {
            string result = value.Trim();
            while (result.StartsWith("-") || result.StartsWith("/"))
            {
                result = result.Substring(1);
            }
            return result.Replace("-", string.Empty).ToUpperInvariant();
        }

        private static string RequireValue(string[] args, ref int index, string optionName)
        {
            if (index + 1 >= args.Length)
            {
                throw new InvalidOperationException("Missing value for " + optionName + ".");
            }

            index++;
            return args[index];
        }

        private static int ParseRange(string value, string name, int min, int max)
        {
            int parsed;
            if (!int.TryParse(value, out parsed) || parsed < min || parsed > max)
            {
                throw new InvalidOperationException(name + " must be between " + min + " and " + max + ".");
            }
            return parsed;
        }

        private static IEnumerable<string> SplitCodecList(string value)
        {
            return value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0);
        }
    }

    internal static class Usage
    {
        public static void Print()
        {
            Console.WriteLine("VirtualDesktopSwitcher");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  VirtualDesktopSwitcher.exe");
            Console.WriteLine("  VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --interval-minutes 25");
            Console.WriteLine("  VirtualDesktopSwitcher.exe --codecs HEVC10bit,HEVC --once");
            Console.WriteLine("  VirtualDesktopSwitcher.exe --target-codec HEVC10bit");
            Console.WriteLine("  VirtualDesktopSwitcher.exe --configure-manual");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --codecs <list>              Comma-separated codec rotation list.");
            Console.WriteLine("  --interval-minutes <n>       Minutes between switches. Default: 25.");
            Console.WriteLine("  --beep-warning              Beep during the final 5 seconds before timer switches.");
            Console.WriteLine("  --no-beep-warning           Keep timer switches silent. Default.");
            Console.WriteLine("  --target-codec <codec>       Set one exact codec and exit.");
            Console.WriteLine("  --once                       Switch once and exit.");
            Console.WriteLine("  --switch-immediately         Switch immediately before entering the timer loop.");
            Console.WriteLine("  --switch-back                Temporarily use the second codec, then restore the first.");
            Console.WriteLine("  --switch-back-delay-ms <n>   Milliseconds before restoring. Default: 50.");
            Console.WriteLine("  --configure-manual           Configure and save timer/manual settings, then exit.");
            Console.WriteLine("  --manual-only                Disable automatic timer switching for this run.");
            Console.WriteLine("  --enable-timer               Force automatic timer switching on for this run.");
            Console.WriteLine("  --manual-action <action>     Override saved action: switch or restart.");
            Console.WriteLine("  --no-manual-trigger          Disable the saved manual binding for this run.");
            Console.WriteLine("  --streamer-path <path>       Path to VirtualDesktop.Streamer.exe.");
            Console.WriteLine("  --timeout-seconds <n>        Helper timeout. Default: 15.");
            Console.WriteLine();
            Console.WriteLine("Known codecs: Automatic, H264, H264Plus, HEVC, HEVC10bit, AV110bit");
        }
    }
}
