using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using EasyHook;

namespace VirtualDesktopSwitcher
{
    internal static class Program
    {
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
        private readonly CodecInjectionClient client;
        private readonly ManualResetEvent cancelSignal = new ManualResetEvent(false);
        private volatile bool cancelRequested;

        public Rotator(Options options)
        {
            this.options = options;
            this.client = new CodecInjectionClient(options);
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

            List<CodecInfo> codecs = ResolveCodecRotation();
            if (options.SwitchBack && codecs.Count != 2)
            {
                throw new InvalidOperationException("--switch-back requires exactly two codecs: the preferred codec first and the temporary codec second.");
            }

            if (options.SwitchBack)
            {
                EnsurePreferredCodec(codecs[0]);
            }

            if (options.Once)
            {
                PerformSwitch(codecs);
                return 0;
            }

            Log("VirtualDesktopSwitcher started. Backend: in-process; Codecs: " +
                string.Join(", ", codecs.Select(c => c.Code).ToArray()) +
                "; interval: " + options.IntervalMinutes + " minute(s)" +
                "; beep warning: " + (options.BeepWarning ? "on" : "off") +
                "; switch back: " + (options.SwitchBack ? "on (delay " + options.SwitchBackDelayMilliseconds + " ms)" : "off") + ".");

            if (options.SwitchImmediately)
            {
                PerformSwitch(codecs);
            }

            while (true)
            {
                if (!WaitForNextSwitch())
                {
                    break;
                }

                PerformSwitch(codecs);
                if (cancelRequested)
                {
                    break;
                }
            }

            Log("Stopped by user.");
            return 0;
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

        private bool WaitForNextSwitch()
        {
            TimeSpan interval = TimeSpan.FromMinutes(options.IntervalMinutes);
            TimeSpan warning = TimeSpan.FromSeconds(5);

            if (!options.BeepWarning)
            {
                return !cancelSignal.WaitOne(interval);
            }

            if (interval > warning)
            {
                if (cancelSignal.WaitOne(interval - warning))
                {
                    return false;
                }
                return BeepBeforeSwitch((int)warning.TotalSeconds);
            }

            return !cancelSignal.WaitOne(interval);
        }

        private bool BeepBeforeSwitch(int seconds)
        {
            Log("Switching codec in " + seconds + " second(s).");

            for (int remaining = seconds; remaining > 0; remaining--)
            {
                Stopwatch tick = Stopwatch.StartNew();
                try
                {
                    Console.Beep(1200, 160);
                }
                catch
                {
                    // Beep support depends on the active Windows audio/session setup.
                }

                int remainingMilliseconds = Math.Max(0, 1000 - (int)tick.ElapsedMilliseconds);
                if (cancelSignal.WaitOne(remainingMilliseconds))
                {
                    return false;
                }
            }

            return true;
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
            ToggleSelection selection = ToggleSelectionDialog.Show(currentCodec, options.IntervalMinutes, options.BeepWarning, options.SwitchBack, options.SwitchBackDelayMilliseconds);
            options.IntervalMinutes = selection.IntervalMinutes;
            options.BeepWarning = selection.BeepWarning;
            options.SwitchBack = selection.SwitchBack;
            options.SwitchBackDelayMilliseconds = selection.SwitchBackDelayMilliseconds;

            CodecInfo first = selection.SwitchBack ? selection.SwitchBackCodec : currentCodec;

            Log("Toggle pair selected: " + first.Code + ", " + selection.Codec.Code +
                "; interval: " + options.IntervalMinutes + " minute(s)" +
                "; beep warning: " + (options.BeepWarning ? "on" : "off") + ".");

            return new List<CodecInfo> { first, selection.Codec };
        }

        private void SwitchToNextCodec(List<CodecInfo> codecs)
        {
            CodecOperationResult current = client.GetCodec();
            CodecInfo next = GetNextCodec(current.Current, codecs);
            CodecOperationResult result = client.SetCodec(next.Code);
            Log("Preferred Codec: " + result.Before + " -> " + result.After);
        }

        private static CodecInfo GetNextCodec(string currentCodec, List<CodecInfo> codecs)
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

    internal sealed class CodecInjectionClient
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
    }

    internal static class ToggleSelectionDialog
    {
        public static ToggleSelection Show(CodecInfo currentCodec, int initialIntervalMinutes, bool initialBeepWarning, bool initialSwitchBack, int initialSwitchBackDelayMilliseconds)
        {
            Application.EnableVisualStyles();

            List<CodecInfo> options = CodecCatalog.All
                .Where(c => !string.Equals(c.Code, currentCodec.Code, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Form form = new Form();
            form.Text = "VirtualDesktopSwitcher";
            form.StartPosition = FormStartPosition.CenterScreen;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = true;
            form.TopMost = true;
            form.ClientSize = new Size(420, 290);

            Label label = new Label();
            label.AutoSize = false;
            label.Location = new Point(16, 16);
            label.Size = new Size(388, 48);
            label.Text = "Current codec is " + currentCodec.Display + ". Choose the different codec to toggle with.";
            form.Controls.Add(label);

            ComboBox combo = new ComboBox();
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Location = new Point(16, 72);
            combo.Size = new Size(388, 24);
            combo.DisplayMember = "Label";
            foreach (CodecInfo option in options)
            {
                combo.Items.Add(new CodecOption(option));
            }
            combo.SelectedIndex = GetSuggestedIndex(currentCodec, options);
            form.Controls.Add(combo);

            Label intervalLabel = new Label();
            intervalLabel.AutoSize = true;
            intervalLabel.Location = new Point(16, 118);
            intervalLabel.Text = "Switch every";
            form.Controls.Add(intervalLabel);

            NumericUpDown intervalInput = new NumericUpDown();
            intervalInput.Location = new Point(100, 114);
            intervalInput.Size = new Size(72, 24);
            intervalInput.Minimum = 1;
            intervalInput.Maximum = 10080;
            intervalInput.Value = initialIntervalMinutes;
            form.Controls.Add(intervalInput);

            Label minutesLabel = new Label();
            minutesLabel.AutoSize = true;
            minutesLabel.Location = new Point(180, 118);
            minutesLabel.Text = "minute(s)";
            form.Controls.Add(minutesLabel);

            CheckBox beepWarningInput = new CheckBox();
            beepWarningInput.AutoSize = true;
            beepWarningInput.Location = new Point(16, 150);
            beepWarningInput.Text = "Beep 5 seconds before each timer switch";
            beepWarningInput.Checked = initialBeepWarning;
            form.Controls.Add(beepWarningInput);

            CheckBox switchBackInput = new CheckBox();
            switchBackInput.AutoSize = true;
            switchBackInput.Location = new Point(16, 181);
            switchBackInput.Text = "Switch back to";
            switchBackInput.Checked = initialSwitchBack;
            form.Controls.Add(switchBackInput);

            ComboBox switchBackCombo = new ComboBox();
            switchBackCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            switchBackCombo.Location = new Point(120, 178);
            switchBackCombo.Size = new Size(200, 24);
            switchBackCombo.DisplayMember = "Label";
            foreach (CodecInfo option in CodecCatalog.All)
            {
                switchBackCombo.Items.Add(new CodecOption(option));
            }
            switchBackCombo.SelectedIndex = 0;
            for (int i = 0; i < switchBackCombo.Items.Count; i++)
            {
                if (string.Equals(((CodecOption)switchBackCombo.Items[i]).Codec.Code, currentCodec.Code, StringComparison.OrdinalIgnoreCase))
                {
                    switchBackCombo.SelectedIndex = i;
                    break;
                }
            }
            switchBackCombo.Enabled = switchBackInput.Checked;
            form.Controls.Add(switchBackCombo);

            Label switchBackDelayLabel = new Label();
            switchBackDelayLabel.AutoSize = true;
            switchBackDelayLabel.Location = new Point(33, 212);
            switchBackDelayLabel.Text = "Switch back after";
            form.Controls.Add(switchBackDelayLabel);

            NumericUpDown switchBackDelayInput = new NumericUpDown();
            switchBackDelayInput.Location = new Point(150, 208);
            switchBackDelayInput.Size = new Size(72, 24);
            switchBackDelayInput.Minimum = 0;
            switchBackDelayInput.Maximum = 60000;
            switchBackDelayInput.Value = initialSwitchBackDelayMilliseconds;
            switchBackDelayInput.Enabled = switchBackInput.Checked;
            form.Controls.Add(switchBackDelayInput);

            Label switchBackDelayUnitLabel = new Label();
            switchBackDelayUnitLabel.AutoSize = true;
            switchBackDelayUnitLabel.Location = new Point(230, 212);
            switchBackDelayUnitLabel.Text = "millisecond(s)";
            form.Controls.Add(switchBackDelayUnitLabel);

            switchBackInput.CheckedChanged += delegate
            {
                switchBackCombo.Enabled = switchBackInput.Checked;
                switchBackDelayInput.Enabled = switchBackInput.Checked;
                RefreshTemporaryCodecOptions(combo, currentCodec, switchBackInput.Checked, switchBackCombo);
            };

            switchBackCombo.SelectedIndexChanged += delegate
            {
                if (switchBackInput.Checked)
                {
                    RefreshTemporaryCodecOptions(combo, currentCodec, true, switchBackCombo);
                }
            };

            Button okButton = new Button();
            okButton.Text = "OK";
            okButton.Location = new Point(248, 244);
            okButton.Size = new Size(75, 28);
            okButton.DialogResult = DialogResult.OK;
            okButton.Click += delegate
            {
                if (switchBackInput.Checked &&
                    string.Equals(((CodecOption)combo.SelectedItem).Codec.Code, ((CodecOption)switchBackCombo.SelectedItem).Codec.Code, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(form, "The switch-back codec must be different from the toggle codec.", "VirtualDesktopSwitcher");
                    form.DialogResult = DialogResult.None;
                }
            };
            form.AcceptButton = okButton;
            form.Controls.Add(okButton);

            Button cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.Location = new Point(329, 244);
            cancelButton.Size = new Size(75, 28);
            cancelButton.DialogResult = DialogResult.Cancel;
            form.CancelButton = cancelButton;
            form.Controls.Add(cancelButton);

            if (form.ShowDialog() != DialogResult.OK)
            {
                throw new InvalidOperationException("Codec selection cancelled.");
            }

            CodecOption selected = (CodecOption)combo.SelectedItem;
            return new ToggleSelection
            {
                Codec = selected.Codec,
                IntervalMinutes = (int)intervalInput.Value,
                BeepWarning = beepWarningInput.Checked,
                SwitchBack = switchBackInput.Checked,
                SwitchBackCodec = ((CodecOption)switchBackCombo.SelectedItem).Codec,
                SwitchBackDelayMilliseconds = (int)switchBackDelayInput.Value
            };
        }

        private static void RefreshTemporaryCodecOptions(ComboBox combo, CodecInfo currentCodec, bool switchBack, ComboBox switchBackCombo)
        {
            string selectedCode = combo.SelectedItem == null
                ? null
                : ((CodecOption)combo.SelectedItem).Codec.Code;
            CodecInfo excludedCodec = currentCodec;
            if (switchBack && switchBackCombo.SelectedItem != null)
            {
                excludedCodec = ((CodecOption)switchBackCombo.SelectedItem).Codec;
            }

            List<CodecInfo> options = CodecCatalog.All
                .Where(c => !string.Equals(c.Code, excludedCodec.Code, StringComparison.OrdinalIgnoreCase))
                .ToList();

            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                foreach (CodecInfo option in options)
                {
                    combo.Items.Add(new CodecOption(option));
                }

                combo.SelectedIndex = -1;
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    if (string.Equals(((CodecOption)combo.Items[i]).Codec.Code, selectedCode, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedIndex = i;
                        break;
                    }
                }

                if (combo.SelectedIndex < 0)
                {
                    combo.SelectedIndex = GetSuggestedIndex(currentCodec, options);
                }
            }
            finally
            {
                combo.EndUpdate();
            }
        }

        private static int GetSuggestedIndex(CodecInfo currentCodec, List<CodecInfo> options)
        {
            string[] order = CodecCatalog.GetSuggestedToggleOrder(currentCodec.Code);
            for (int i = 0; i < order.Length; i++)
            {
                for (int j = 0; j < options.Count; j++)
                {
                    if (string.Equals(order[i], options[j].Code, StringComparison.OrdinalIgnoreCase))
                    {
                        return j;
                    }
                }
            }

            return 0;
        }

        private sealed class CodecOption
        {
            public readonly CodecInfo Codec;

            public CodecOption(CodecInfo codec)
            {
                Codec = codec;
            }

            public string Label
            {
                get { return Codec.Display + " (" + Codec.Code + ")"; }
            }
        }
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
            Console.WriteLine("  --streamer-path <path>       Path to VirtualDesktop.Streamer.exe.");
            Console.WriteLine("  --timeout-seconds <n>        Helper timeout. Default: 15.");
            Console.WriteLine();
            Console.WriteLine("Known codecs: Automatic, H264, H264Plus, HEVC, HEVC10bit, AV110bit");
        }
    }
}
