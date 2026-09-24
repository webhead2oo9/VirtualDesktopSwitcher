using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;
using System.Web.Script.Serialization;

namespace VirtualDesktopSwitcher.Tests
{
    internal static class ManualFeatureTests
    {
        private static int assertions;

        public static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "VirtualDesktopSwitcher-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                TestSettings(root);
                TestOptionsPrecedence();
                TestSettingsEditor();
                TestCountdown();
                TestKeyboardTransitions();
                TestKeyboardCapture();
                TestHidTransitions();
                TestCodecActions();
                TestScheduledStartupOrdering();
                Console.WriteLine("Manual feature tests passed (" + assertions + " assertions). ");
                return 0;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void TestSettings(string root)
        {
            string warning;
            string missing = Path.Combine(root, "missing.json");
            ManualSettings defaults = ManualSettingsStore.LoadFrom(missing, out warning);
            Assert(defaults.TimerEnabled, "timer defaults on");
            Assert(defaults.Binding == null, "manual binding defaults empty");
            Assert(defaults.GetAction() == ManualAction.Restart, "manual action defaults to restart");
            Assert(warning == null, "first launch uses defaults without a warning");
            Assert(defaults.CountdownSeconds == 0 && !defaults.CountdownBeep, "new installs reset immediately and silently");

            string path = Path.Combine(root, "settings.json");
            ManualSettings settings = ManualSettings.CreateDefault();
            settings.TimerEnabled = false;
            settings.ManualEnabled = true;
            settings.Action = "switch";
            settings.RestartDelayMilliseconds = 123;
            settings.Binding = new ManualBinding { Kind = "keyboard", VirtualKey = 0x4b, Modifiers = (int)KeyboardModifiers.Control };
            ManualSettingsStore.SaveTo(path, settings);
            ManualSettings loaded = ManualSettingsStore.LoadFrom(path, out warning);
            Assert(warning == null, "valid settings do not warn");
            Assert(!loaded.TimerEnabled && loaded.GetAction() == ManualAction.Switch, "settings round trip");
            Assert(loaded.Binding.VirtualKey == 0x4b && loaded.Binding.Modifiers == 1, "binding round trip");
            string savedJson = File.ReadAllText(path);
            Assert(savedJson.Contains("ManualEnabled") && !savedJson.Contains("IsValid") && !savedJson.Contains("DisplayText"), "enabled preference is persisted without computed properties");

            settings.Action = "restart";
            ManualSettingsStore.SaveTo(path, settings);
            loaded = ManualSettingsStore.LoadFrom(path, out warning);
            Assert(loaded.GetAction() == ManualAction.Restart, "atomic replacement updates destination");
            Assert(Directory.GetFiles(root, "*.tmp").Length == 0, "atomic replacement leaves no temporary file");

            File.WriteAllText(path, "not json");
            loaded = ManualSettingsStore.LoadFrom(path, out warning);
            Assert(warning != null && loaded.TimerEnabled, "malformed settings fall back");

            File.WriteAllText(path, "{\"Version\":99,\"TimerEnabled\":false,\"Action\":\"restart\",\"RestartDelayMilliseconds\":50}");
            loaded = ManualSettingsStore.LoadFrom(path, out warning);
            Assert(warning != null && loaded.Version == ManualSettings.CurrentVersion, "unsupported version falls back");

            settings.Version = 1;
            settings.ManualEnabled = false;
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(settings));
            loaded = ManualSettingsStore.LoadFrom(path, out warning);
            Assert(warning == null && loaded.Version == 2 && loaded.ManualEnabled, "v1 binding migrates as enabled");
            Assert(loaded.CountdownSeconds == 3 && loaded.CountdownBeep, "v1 countdown behavior is preserved");
            loaded.ManualEnabled = false;
            loaded.TimerEnabled = true;
            loaded.CountdownSeconds = 1;
            loaded.CountdownBeep = false;
            ManualSettingsStore.SaveTo(path, loaded);
            loaded = ManualSettingsStore.LoadFrom(path, out warning);
            Assert(!loaded.ManualEnabled && loaded.Binding != null, "disabled binding survives save and reload");
            Assert(loaded.CountdownSeconds == 1 && !loaded.CountdownBeep, "warning preferences survive save and reload");
            loaded.CountdownSeconds = 2;
            AssertThrows(delegate { ManualSettingsStore.SaveTo(path, loaded); }, "unsupported countdown rejected");
        }

        private static void TestOptionsPrecedence()
        {
            ManualSettings saved = ManualSettings.CreateDefault();
            saved.TimerEnabled = false;
            saved.ManualEnabled = true;
            saved.Action = "restart";
            saved.RestartDelayMilliseconds = 321;
            saved.Binding = new ManualBinding { Kind = "keyboard", VirtualKey = 0x51 };

            Options options = Options.Parse(new[] { "--enable-timer", "--manual-action", "switch" });
            options.ApplyManualSettings(saved);
            Assert(options.TimerEnabled, "CLI timer override wins");
            Assert(options.ManualEnabled, "saved binding enables manual trigger");
            Assert(options.ManualAction == ManualAction.Switch, "CLI action override wins");
            Assert(options.ManualRestartDelayMilliseconds == 321, "saved restart delay applies");

            options = Options.Parse(new[] { "--manual-only", "--no-manual-trigger" });
            options.ApplyManualSettings(saved);
            Assert(!options.TimerEnabled && !options.ManualEnabled, "CLI can disable both for validation to reject");

            AssertThrows(delegate { Options.Parse(new[] { "--manual-action", "invalid" }); }, "invalid manual action is rejected");
            saved.TimerEnabled = true;
            saved.ManualEnabled = false;
            saved.CountdownSeconds = 1;
            options = Options.Parse(new string[0]);
            options.ApplyManualSettings(saved);
            Assert(!options.ManualEnabled && options.ManualBinding != null, "retained binding does not enable manual control");
            Assert(options.ManualCountdownSeconds == 1, "saved countdown applies");
            options = Options.Parse(new[] { "--manual-only" });
            options.ApplyManualSettings(saved);
            Assert(options.ManualEnabled && !options.TimerEnabled, "manual-only explicitly activates retained binding");
        }

        private static void TestSettingsEditor()
        {
            ManualSettings initial = ManualSettings.CreateDefault();
            initial.Binding = new ManualBinding { Kind = "keyboard", VirtualKey = 0x4b, Modifiers = 1 };
            using (ManualSettingsEditor editor = new ManualSettingsEditor(initial))
            {
                ComboBox schedule = (ComboBox)editor.Controls["schedule"];
                ComboBox countdown = (ComboBox)editor.Controls["countdown"];
                ManualSettings saved;
                Assert(editor.TryCreateSettings(null, out saved) && saved.TimerEnabled && !saved.ManualEnabled, "default editor selects automatic scheduling");
                Assert(saved.Binding != null, "automatic mode keeps the saved binding");
                schedule.SelectedIndex = 1;
                countdown.SelectedIndex = 2;
                Assert(editor.TryCreateSettings(null, out saved) && !saved.TimerEnabled && saved.ManualEnabled, "manual-only can reuse the saved binding");
                Assert(saved.CountdownSeconds == 3, "warning choice saves seconds");
                schedule.SelectedIndex = 2;
                Assert(editor.TryCreateSettings(null, out saved) && saved.TimerEnabled && saved.ManualEnabled, "both scheduling methods can be enabled");
                editor.Controls.OfType<Button>().Single(button => button.Text == "Clear").PerformClick();
                Assert(editor.ValidationError != null, "missing binding is rejected in the editor");
                schedule.SelectedIndex = 0;
                Assert(editor.ValidationError == null, "automatic mode does not require a binding");
            }
        }

        private static void TestCountdown()
        {
            Options options = new Options();
            Rotator rotator = new Rotator(options, new FakeCodecClient("HEVC"));
            Stopwatch watch = Stopwatch.StartNew();
            Assert(rotator.ManualCountdown(), "immediate action proceeds");
            Assert(watch.ElapsedMilliseconds < 500, "immediate action has no fixed countdown");
            options.ManualCountdownSeconds = 1;
            watch.Restart();
            Assert(rotator.ManualCountdown(), "silent countdown proceeds");
            Assert(watch.ElapsedMilliseconds >= 900, "one-second warning waits before action");
        }

        private static void TestKeyboardTransitions()
        {
            Assert(!new ManualBinding { Kind = "keyboard", VirtualKey = 0x11 }.IsValid, "modifier-only binding rejected");
            Assert(!new ManualBinding { Kind = "keyboard", VirtualKey = 0xa2 }.IsValid, "left/right modifier-only binding rejected");
            ManualBinding chord = new ManualBinding { Kind = "keyboard", VirtualKey = 0x4b, Modifiers = 1 };
            Assert(chord.IsValid && chord.DisplayText == "Ctrl+K", "keyboard chord formatting");

            KeyboardStateTracker tracker = new KeyboardStateTracker();
            Assert(tracker.Process(0x11, false) == null, "modifier alone does not trigger");
            KeyboardPress press = tracker.Process(0x4b, false);
            Assert(press != null && press.VirtualKey == 0x4b && press.Modifiers == KeyboardModifiers.Control, "chord is captured");
            Assert(tracker.Process(0x4b, false) == null, "keyboard auto-repeat suppressed");
            tracker.Process(0x4b, true);
            Assert(tracker.Process(0x4b, false) != null, "keyboard release and repress triggers");

            HashSet<int> physicallyDown = new HashSet<int>();
            KeyboardStateTracker live = new KeyboardStateTracker(physicallyDown.Contains);
            live.Process(0x11, false);
            physicallyDown.Add(0x4b);
            Assert(live.Process(0x4b, false).Modifiers == KeyboardModifiers.None, "modifier whose key-up was missed is dropped");
            live = new KeyboardStateTracker(physicallyDown.Contains);
            physicallyDown.Clear();
            live.Process(0x11, false);
            Assert(live.Process(0x4b, false).Modifiers == KeyboardModifiers.Control, "unavailable live key state keeps tracked modifiers");
        }

        private static void TestKeyboardCapture()
        {
            KeyboardCaptureSession session = new KeyboardCaptureSession(new KeyboardStateTracker());
            session.Process(0xa2, false);
            session.Process(0x4b, false);
            Assert(session.Binding != null && session.Binding.VirtualKey == 0x4b && session.Binding.Modifiers == (int)KeyboardModifiers.Control, "capture records the chord on press");
            Assert(!session.Completed, "capture stays open while the chord is held");
            session.Process(0x4b, true);
            Assert(!session.Completed, "capture stays open while a modifier is still held");
            session.Process(0xa2, true);
            Assert(session.Completed, "capture completes once every key is released");

            session = new KeyboardCaptureSession(new KeyboardStateTracker());
            session.Process(0x1b, false);
            Assert(session.Cancelled && session.Binding == null && !session.Completed, "plain Esc cancels once released");
            session.Process(0x1b, true);
            Assert(session.Completed && session.Binding == null, "cancelled capture completes without a binding");

            session = new KeyboardCaptureSession(new KeyboardStateTracker());
            session.Process(0x10, false);
            session.Process(0x1b, false);
            Assert(!session.Cancelled && session.Binding.VirtualKey == 0x1b && session.Binding.Modifiers == (int)KeyboardModifiers.Shift, "Esc with a modifier can be bound");
        }

        private static void TestHidTransitions()
        {
            Assert(Marshal.SizeOf(typeof(RawInputNative.HIDP_CAPS)) == 64, "HID capabilities layout matches Windows");
            Assert(Marshal.SizeOf(typeof(RawInputNative.HIDP_BUTTON_CAPS)) == 72, "HID button capabilities layout matches Windows");
            HidButtonStateTracker tracker = new HidButtonStateTracker();
            int usage = RawInputHelpers.MakeUsageKey(9, 7);
            IntPtr firstDevice = new IntPtr(1);
            IntPtr secondDevice = new IntPtr(2);
            Assert(tracker.Update(firstDevice, Reports(usage)).SequenceEqual(new[] { usage }), "first HID press triggers");
            Assert(!tracker.Update(firstDevice, Reports(usage)).Any(), "held HID button suppressed");
            Assert(!tracker.Update(firstDevice, Reports()).Any(), "HID release does not trigger");
            Assert(tracker.Update(firstDevice, Reports(usage)).Single() == usage, "HID release and repress triggers");
            Assert(tracker.Update(secondDevice, Reports(usage)).Single() == usage, "same usage on another device triggers");
            int otherUsage = RawInputHelpers.MakeUsageKey(9, 8);
            Assert(tracker.Update(firstDevice, ReportsWithId(2, otherUsage)).Single() == otherUsage, "another HID report ID triggers independently");
            Assert(!tracker.Update(firstDevice, Reports(usage)).Any(), "alternating report IDs do not retrigger held buttons");

            const string wheel = @"\\?\HID#VID_346E&PID_0006&MI_02#8&2f0f1e8&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string wheelOtherPort = @"\\?\HID#VID_346E&PID_0006&MI_02#8&11aa22bb&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            const string pedals = @"\\?\HID#VID_346E&PID_1000#7&3c4d5e6f&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            ManualBinding deviceBinding = new ManualBinding { Kind = "hid", UsagePage = 9, ButtonUsage = 7, DevicePath = wheel };
            Assert(RawInputHelpers.MatchesDevice(deviceBinding, wheel.ToUpperInvariant(), true), "bound device comparison is case insensitive");
            Assert(!RawInputHelpers.MatchesDevice(deviceBinding, pedals, false), "another controller cannot trigger the binding");
            Assert(RawInputHelpers.MatchesDevice(deviceBinding, wheelOtherPort, false), "bound controller on another USB port still triggers");
            Assert(!RawInputHelpers.MatchesDevice(deviceBinding, wheelOtherPort, true), "identical second controller is ignored while the bound one is connected");
            Assert(!RawInputHelpers.IsBoundDevicePresent(deviceBinding, new[] { pedals }), "missing bound controller is detected");
            Assert(RawInputHelpers.IsBoundDevicePresent(deviceBinding, new[] { pedals, wheelOtherPort }), "moved bound controller counts as present");
            ManualBinding unparsed = new ManualBinding { Kind = "hid", UsagePage = 9, ButtonUsage = 7, DevicePath = "wheel-a" };
            Assert(!RawInputHelpers.MatchesDevice(unparsed, "wheel-b", false), "paths without a hardware ID only match exactly");
            Assert(deviceBinding.Clone().DevicePath == wheel, "device identity survives cloning");
            deviceBinding.DevicePath = null;
            Assert(RawInputHelpers.MatchesDevice(deviceBinding, pedals, false), "legacy wildcard bindings remain supported");

            RawInputNative.HIDP_BUTTON_CAPS[] caps = new[]
            {
                new RawInputNative.HIDP_BUTTON_CAPS { UsagePage = 9, ReportID = 0 }
            };
            HashSet<int> pressed = RawInputHelpers.ReadPressedButtons(caps, 1, 0, delegate { return new ushort[] { 7, 8 }; });
            Assert(pressed.SetEquals(new[] { usage, otherUsage }), "multiple held buttons survive a non-range capability");
            pressed = RawInputHelpers.ReadPressedButtons(caps, 1, 0, delegate { return null; });
            Assert(pressed == null, "failed button parsing returns unknown state");
            Assert(!tracker.Update(firstDevice, new HidButtonReport[0]).Any(), "unknown report produces no transition");
            Assert(!tracker.Update(firstDevice, Reports(usage)).Any(), "held button is not retriggered after failed parsing");
        }

        private static void TestCodecActions()
        {
            List<CodecInfo> rotation = new List<CodecInfo> { CodecCatalog.Resolve("HEVC"), CodecCatalog.Resolve("H264") };
            Assert(Rotator.GetNextCodec("Automatic", rotation).Code == "HEVC", "outside codec starts at first rotation entry");
            Assert(Rotator.GetNextCodec("HEVC", rotation).Code == "H264", "codec rotation advances");

            Options options = new Options();
            options.ManualAction = ManualAction.Restart;
            options.ManualRestartDelayMilliseconds = 0;
            FakeCodecClient fake = new FakeCodecClient("Automatic");
            new Rotator(options, fake).PerformManualAction(rotation);
            Assert(fake.SetCalls.SequenceEqual(new[] { "HEVC", "Automatic" }), "restart restores exact original codec");

            fake = new FakeCodecClient("HEVC");
            fake.FailSetCallNumbers.Add(2);
            new Rotator(options, fake).PerformManualAction(rotation);
            Assert(fake.SetCalls.SequenceEqual(new[] { "H264", "HEVC", "HEVC" }), "restore retries once");

            fake = new FakeCodecClient("HEVC");
            fake.FailSetCallNumbers.Add(1);
            AssertThrows(delegate { new Rotator(options, fake).PerformManualAction(rotation); }, "temporary switch failure is reported to the caller");
            Assert(fake.SetCalls.SequenceEqual(new[] { "H264", "HEVC" }), "temporary failure attempts original restoration");

            fake = new FakeCodecClient("HEVC");
            fake.FailSetCallNumbers.Add(1);
            Assert(new Rotator(options, fake).RunManualAction(rotation) == Rotator.ManualResult.Failed, "failed manual action keeps the run going");

            options.ManualAction = ManualAction.Switch;
            fake = new FakeCodecClient("HEVC");
            new Rotator(options, fake).PerformManualAction(rotation);
            Assert(fake.SetCalls.SequenceEqual(new[] { "H264" }), "manual switch uses normal rotation");
        }

        private static void TestScheduledStartupOrdering()
        {
            List<CodecInfo> rotation = new List<CodecInfo> { CodecCatalog.Resolve("HEVC"), CodecCatalog.Resolve("H264") };

            Options options = CreateSwitchBackOptions();
            options.TimerEnabled = false;
            options.ManualEnabled = false;
            options.ManualBinding = new ManualBinding { Kind = "keyboard", VirtualKey = 0x51 };
            FakeCodecClient fake = new FakeCodecClient("H264");
            AssertThrowsMessage(
                delegate { new Rotator(options, fake).InitializeScheduledRun(rotation); },
                "Both the automatic timer and manual trigger are disabled. Run --configure-manual to enable and bind a trigger.",
                "disabled scheduling is rejected before switch-back normalization");
            Assert(fake.SetCalls.Count == 0, "disabled scheduling makes no codec-setting calls");

            options = CreateSwitchBackOptions();
            options.TimerEnabled = false;
            options.ManualEnabled = true;
            options.ManualBinding = new ManualBinding { Kind = "keyboard", VirtualKey = 0x51 };
            fake = new FakeCodecClient("H264");
            AssertThrowsMessage(
                delegate
                {
                    new Rotator(options, fake, delegate(ManualBinding binding)
                    {
                        throw new InvalidOperationException("planned Raw Input failure");
                    }).InitializeScheduledRun(rotation);
                },
                "Manual-only mode could not start Raw Input. planned Raw Input failure",
                "manual-only Raw Input failure is reported before switch-back normalization");
            Assert(fake.SetCalls.Count == 0, "manual-only Raw Input failure makes no codec-setting calls");

            options = CreateSwitchBackOptions();
            options.TimerEnabled = true;
            options.ManualEnabled = true;
            options.ManualBinding = new ManualBinding { Kind = "keyboard", VirtualKey = 0x51 };
            fake = new FakeCodecClient("H264");
            new Rotator(options, fake, delegate(ManualBinding binding)
            {
                throw new InvalidOperationException("planned Raw Input failure");
            }).InitializeScheduledRun(rotation);
            Assert(!options.ManualEnabled, "timer mode disables unavailable manual trigger");
            Assert(fake.SetCalls.SequenceEqual(new[] { "HEVC" }), "timer mode normalizes switch-back codec after scheduling initialization");

            options = CreateSwitchBackOptions();
            options.Once = true;
            options.TimerEnabled = false;
            options.ManualEnabled = false;
            fake = new FakeCodecClient("H264");
            Assert(new Rotator(options, fake).Run() == 0, "one-shot switch-back remains exempt from scheduling validation");
            Assert(fake.SetCalls.SequenceEqual(new[] { "HEVC", "H264", "HEVC" }), "one-shot switch-back preserves switch-away-and-back behavior");
        }

        private static Options CreateSwitchBackOptions()
        {
            Options options = new Options();
            options.Codecs.Add("HEVC");
            options.Codecs.Add("H264");
            options.SwitchBack = true;
            options.SwitchBackDelayMilliseconds = 0;
            return options;
        }

        private static IEnumerable<HidButtonReport> Reports(params int[] usages)
        {
            return ReportsWithId(0, usages);
        }

        private static IEnumerable<HidButtonReport> ReportsWithId(int reportId, params int[] usages)
        {
            return new[] { new HidButtonReport { ReportId = reportId, Pressed = new HashSet<int>(usages) } };
        }

        private static void Assert(bool condition, string name)
        {
            assertions++;
            if (!condition) throw new Exception("Assertion failed: " + name);
        }

        private static void AssertThrows(Action action, string name)
        {
            bool threw = false;
            try { action(); } catch { threw = true; }
            Assert(threw, name);
        }

        private static void AssertThrowsMessage(Action action, string expectedMessage, string name)
        {
            Exception thrown = null;
            try { action(); } catch (Exception ex) { thrown = ex; }
            Assert(thrown != null && thrown.Message == expectedMessage, name);
        }

        private sealed class FakeCodecClient : ICodecClient
        {
            private string current;
            private int setCallCount;
            public readonly List<string> SetCalls = new List<string>();
            public readonly HashSet<int> FailSetCallNumbers = new HashSet<int>();

            public FakeCodecClient(string currentCodec)
            {
                current = currentCodec;
            }

            public CodecOperationResult GetCodec()
            {
                return Result(current, current);
            }

            public CodecOperationResult SetCodec(string codec)
            {
                setCallCount++;
                SetCalls.Add(codec);
                if (FailSetCallNumbers.Contains(setCallCount))
                {
                    throw new InvalidOperationException("planned failure");
                }
                string before = current;
                current = codec;
                return Result(before, current);
            }

            private static CodecOperationResult Result(string before, string after)
            {
                return new CodecOperationResult { Before = before, After = after, Current = after, Operation = "test" };
            }
        }
    }
}
