using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

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
                TestKeyboardTransitions();
                TestHidTransitions();
                TestCodecActions();
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
            Assert(warning != null, "missing settings warns");

            string path = Path.Combine(root, "settings.json");
            ManualSettings settings = ManualSettings.CreateDefault();
            settings.TimerEnabled = false;
            settings.Action = "switch";
            settings.RestartDelayMilliseconds = 123;
            settings.Binding = new ManualBinding { Kind = "keyboard", VirtualKey = 0x4b, Modifiers = (int)KeyboardModifiers.Control };
            ManualSettingsStore.SaveTo(path, settings);
            ManualSettings loaded = ManualSettingsStore.LoadFrom(path, out warning);
            Assert(warning == null, "valid settings do not warn");
            Assert(!loaded.TimerEnabled && loaded.GetAction() == ManualAction.Switch, "settings round trip");
            Assert(loaded.Binding.VirtualKey == 0x4b && loaded.Binding.Modifiers == 1, "binding round trip");
            string savedJson = File.ReadAllText(path);
            Assert(!savedJson.Contains("ManualEnabled") && !savedJson.Contains("IsValid") && !savedJson.Contains("DisplayText"), "settings contain only the supported preferences");

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
        }

        private static void TestOptionsPrecedence()
        {
            ManualSettings saved = ManualSettings.CreateDefault();
            saved.TimerEnabled = false;
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
            AssertThrows(delegate { new Rotator(options, fake).PerformManualAction(rotation); }, "temporary switch failure remains fatal");
            Assert(fake.SetCalls.SequenceEqual(new[] { "H264", "HEVC" }), "temporary failure attempts original restoration");

            options.ManualAction = ManualAction.Switch;
            fake = new FakeCodecClient("HEVC");
            new Rotator(options, fake).PerformManualAction(rotation);
            Assert(fake.SetCalls.SequenceEqual(new[] { "H264" }), "manual switch uses normal rotation");
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
