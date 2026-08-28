using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VirtualDesktopSwitcher
{
    internal sealed class RawInputListener : IDisposable
    {
        private readonly ManualBinding binding;
        private readonly object triggerGate = new object();
        private readonly AutoResetEvent triggerSignal = new AutoResetEvent(false);
        private readonly ManualResetEvent readySignal = new ManualResetEvent(false);
        private readonly KeyboardStateTracker keyboardState = new KeyboardStateTracker();
        private readonly HidButtonStateTracker hidState = new HidButtonStateTracker();
        private Thread thread;
        private RawInputWindow window;
        private Exception initializationError;
        private bool enabled = true;
        private bool pending;

        public RawInputListener(ManualBinding binding)
        {
            if (binding == null || !binding.IsValid)
            {
                throw new ArgumentException("A valid manual binding is required.", "binding");
            }
            this.binding = binding.Clone();

            thread = new Thread(RunMessageLoop);
            thread.IsBackground = true;
            thread.Name = "VirtualDesktopSwitcher Raw Input";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            readySignal.WaitOne();
            if (initializationError != null)
            {
                throw new InvalidOperationException("Raw Input initialization failed: " + initializationError.Message, initializationError);
            }
        }

        public WaitHandle TriggerSignal
        {
            get { return triggerSignal; }
        }

        public void SetEnabled(bool value)
        {
            lock (triggerGate)
            {
                enabled = value;
                if (!value)
                {
                    pending = false;
                    triggerSignal.Reset();
                }
            }
        }

        public void ConsumeTrigger()
        {
            lock (triggerGate)
            {
                pending = false;
            }
        }

        public bool TryBeginAutomaticOperation()
        {
            lock (triggerGate)
            {
                enabled = false;
                if (pending)
                {
                    pending = false;
                    triggerSignal.Reset();
                    enabled = true;
                    return false;
                }
                return true;
            }
        }

        private void RunMessageLoop()
        {
            try
            {
                window = new RawInputWindow(ProcessPacket, DeviceRemoved);
            }
            catch (Exception ex)
            {
                initializationError = ex;
            }
            finally
            {
                readySignal.Set();
            }

            if (initializationError == null)
            {
                Application.Run();
                window.Dispose();
            }
        }

        private void ProcessPacket(RawInputPacket packet)
        {
            if (packet.Type == RawInputNative.RIM_TYPEKEYBOARD)
            {
                ProcessKeyboard(packet);
            }
            else if (packet.Type == RawInputNative.RIM_TYPEHID)
            {
                ProcessHid(packet);
            }
        }

        private void ProcessKeyboard(RawInputPacket packet)
        {
            KeyboardPress press = keyboardState.Process(packet.VirtualKey, packet.KeyUp);
            if (press == null)
            {
                return;
            }

            if (string.Equals(binding.Kind, "keyboard", StringComparison.OrdinalIgnoreCase) &&
                binding.VirtualKey == press.VirtualKey && binding.Modifiers == (int)press.Modifiers)
            {
                RaiseTrigger();
            }
        }

        private void ProcessHid(RawInputPacket packet)
        {
            if (!string.Equals(binding.Kind, "hid", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int requested = RawInputHelpers.MakeUsageKey(binding.UsagePage, binding.ButtonUsage);
            foreach (int pressed in hidState.Update(packet.Device, RawInputHelpers.GetHidButtonReports(packet)))
            {
                if (pressed == requested)
                {
                    RaiseTrigger();
                }
            }
        }

        private void DeviceRemoved(IntPtr device)
        {
            hidState.RemoveDevice(device);
        }

        private void RaiseTrigger()
        {
            lock (triggerGate)
            {
                if (!enabled || pending)
                {
                    return;
                }
                pending = true;
                triggerSignal.Set();
            }
        }

        public void Dispose()
        {
            RawInputWindow current = window;
            if (current != null && current.Handle != IntPtr.Zero)
            {
                RawInputNative.PostMessage(current.Handle, RawInputNative.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            if (thread != null && thread.IsAlive)
            {
                thread.Join(3000);
            }
            triggerSignal.Close();
            readySignal.Close();
        }
    }

    internal static class RawInputCaptureDialog
    {
        public static ManualBinding Show(IWin32Window owner)
        {
            using (CaptureForm form = new CaptureForm())
            {
                return form.ShowDialog(owner) == DialogResult.OK ? form.Binding : null;
            }
        }

        private sealed class CaptureForm : Form
        {
            private readonly KeyboardStateTracker keyboardState = new KeyboardStateTracker();
            private readonly HidButtonStateTracker hidState = new HidButtonStateTracker();
            public ManualBinding Binding;

            public CaptureForm()
            {
                Text = "Bind manual trigger";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ClientSize = new System.Drawing.Size(430, 125);
                TopMost = true;

                Label instructions = new Label();
                instructions.AutoSize = false;
                instructions.Location = new System.Drawing.Point(16, 16);
                instructions.Size = new System.Drawing.Size(398, 48);
                instructions.Text = "Press a keyboard key with optional Ctrl, Alt, Shift, or Windows modifiers, or press a discrete controller button.";
                Controls.Add(instructions);

                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.Location = new System.Drawing.Point(339, 79);
                cancel.Size = new System.Drawing.Size(75, 28);
                cancel.DialogResult = DialogResult.Cancel;
                CancelButton = cancel;
                Controls.Add(cancel);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                RawInputNative.Register(Handle);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == RawInputNative.WM_INPUT)
                {
                    RawInputPacket packet = RawInputNative.ReadPacket(m.LParam);
                    if (packet != null)
                    {
                        ProcessPacket(packet);
                    }
                }
                else if (m.Msg == RawInputNative.WM_INPUT_DEVICE_CHANGE && m.WParam.ToInt32() == RawInputNative.GIDC_REMOVAL)
                {
                    hidState.RemoveDevice(m.LParam);
                }
                base.WndProc(ref m);
            }

            private void ProcessPacket(RawInputPacket packet)
            {
                if (packet.Type == RawInputNative.RIM_TYPEKEYBOARD)
                {
                    KeyboardPress press = keyboardState.Process(packet.VirtualKey, packet.KeyUp);
                    if (press != null)
                    {
                        Accept(new ManualBinding
                        {
                            Kind = "keyboard",
                            VirtualKey = press.VirtualKey,
                            Modifiers = (int)press.Modifiers
                        });
                    }
                }
                else if (packet.Type == RawInputNative.RIM_TYPEHID)
                {
                    foreach (int usageKey in hidState.Update(packet.Device, RawInputHelpers.GetHidButtonReports(packet)))
                    {
                        Accept(new ManualBinding
                        {
                            Kind = "hid",
                            UsagePage = (usageKey >> 16) & 0xffff,
                            ButtonUsage = usageKey & 0xffff
                        });
                        return;
                    }
                }
            }

            private void Accept(ManualBinding binding)
            {
                Binding = binding;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
    }

    internal sealed class RawInputWindow : NativeWindow, IDisposable
    {
        private readonly Action<RawInputPacket> packetHandler;
        private readonly Action<IntPtr> removalHandler;

        public RawInputWindow(Action<RawInputPacket> packetHandler, Action<IntPtr> removalHandler)
        {
            this.packetHandler = packetHandler;
            this.removalHandler = removalHandler;
            CreateParams parameters = new CreateParams();
            parameters.Caption = "VirtualDesktopSwitcherRawInput";
            CreateHandle(parameters);
            RawInputNative.Register(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == RawInputNative.WM_INPUT)
            {
                RawInputPacket packet = RawInputNative.ReadPacket(m.LParam);
                if (packet != null)
                {
                    packetHandler(packet);
                }
            }
            else if (m.Msg == RawInputNative.WM_INPUT_DEVICE_CHANGE && m.WParam.ToInt32() == RawInputNative.GIDC_REMOVAL)
            {
                removalHandler(m.LParam);
            }
            else if (m.Msg == RawInputNative.WM_CLOSE)
            {
                Application.ExitThread();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }

    internal sealed class RawInputPacket
    {
        public uint Type;
        public IntPtr Device;
        public int VirtualKey;
        public bool KeyUp;
        public int HidReportSize;
        public int HidReportCount;
        public byte[] HidData;
    }

    internal static class RawInputHelpers
    {
        public static bool IsModifierKey(int key)
        {
            return key == 0x10 || key == 0x11 || key == 0x12 || key == 0x5b || key == 0x5c ||
                key == 0xa0 || key == 0xa1 || key == 0xa2 || key == 0xa3 || key == 0xa4 || key == 0xa5;
        }

        public static int NormalizeKey(int key)
        {
            if (key == 0xa0 || key == 0xa1) return 0x10;
            if (key == 0xa2 || key == 0xa3) return 0x11;
            if (key == 0xa4 || key == 0xa5) return 0x12;
            return key;
        }

        public static KeyboardModifiers GetModifiers(HashSet<int> keysDown)
        {
            KeyboardModifiers result = KeyboardModifiers.None;
            if (keysDown.Contains(0x11)) result |= KeyboardModifiers.Control;
            if (keysDown.Contains(0x12)) result |= KeyboardModifiers.Alt;
            if (keysDown.Contains(0x10)) result |= KeyboardModifiers.Shift;
            if (keysDown.Contains(0x5b) || keysDown.Contains(0x5c)) result |= KeyboardModifiers.Windows;
            return result;
        }

        public static int MakeUsageKey(int usagePage, int usage)
        {
            return ((usagePage & 0xffff) << 16) | (usage & 0xffff);
        }

        public static IEnumerable<HidButtonReport> GetHidButtonReports(RawInputPacket packet)
        {
            List<HidButtonReport> results = new List<HidButtonReport>();
            if (packet.HidData == null || packet.HidReportSize <= 0 || packet.HidReportCount <= 0)
            {
                return results;
            }

            uint preparsedSize = 0;
            RawInputNative.GetRawInputDeviceInfo(packet.Device, RawInputNative.RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize);
            if (preparsedSize == 0)
            {
                return results;
            }

            IntPtr preparsed = Marshal.AllocHGlobal((int)preparsedSize);
            try
            {
                if (RawInputNative.GetRawInputDeviceInfo(packet.Device, RawInputNative.RIDI_PREPARSEDDATA, preparsed, ref preparsedSize) == uint.MaxValue)
                {
                    return results;
                }

                RawInputNative.HIDP_CAPS caps;
                if (RawInputNative.HidP_GetCaps(preparsed, out caps) != RawInputNative.HIDP_STATUS_SUCCESS || caps.NumberInputButtonCaps == 0)
                {
                    return results;
                }

                ushort capCount = caps.NumberInputButtonCaps;
                RawInputNative.HIDP_BUTTON_CAPS[] buttonCaps = new RawInputNative.HIDP_BUTTON_CAPS[capCount];
                if (RawInputNative.HidP_GetButtonCaps(0, buttonCaps, ref capCount, preparsed) != RawInputNative.HIDP_STATUS_SUCCESS)
                {
                    return results;
                }

                bool usesReportIds = false;
                for (int capIndex = 0; capIndex < capCount; capIndex++)
                {
                    if (buttonCaps[capIndex].ReportID != 0)
                    {
                        usesReportIds = true;
                        break;
                    }
                }

                for (int reportIndex = 0; reportIndex < packet.HidReportCount; reportIndex++)
                {
                    int reportOffset = reportIndex * packet.HidReportSize;
                    if (reportOffset + packet.HidReportSize > packet.HidData.Length)
                    {
                        break;
                    }
                    IntPtr report = Marshal.AllocHGlobal(packet.HidReportSize);
                    try
                    {
                        Marshal.Copy(packet.HidData, reportOffset, report, packet.HidReportSize);
                        int reportId = usesReportIds ? packet.HidData[reportOffset] : 0;
                        HashSet<int> pressed = new HashSet<int>();
                        for (int capIndex = 0; capIndex < capCount; capIndex++)
                        {
                            RawInputNative.HIDP_BUTTON_CAPS cap = buttonCaps[capIndex];
                            if (cap.ReportID != reportId)
                            {
                                continue;
                            }
                            int maximumUsages = cap.IsRange != 0
                                ? Math.Max(1, cap.Range.UsageMax - cap.Range.UsageMin + 1)
                                : 1;
                            ushort[] usages = new ushort[maximumUsages];
                            uint usageLength = (uint)usages.Length;
                            int status = RawInputNative.HidP_GetUsages(0, cap.UsagePage, cap.LinkCollection, usages, ref usageLength, preparsed, report, (uint)packet.HidReportSize);
                            if (status == RawInputNative.HIDP_STATUS_SUCCESS)
                            {
                                for (int usageIndex = 0; usageIndex < usageLength; usageIndex++)
                                {
                                    pressed.Add(MakeUsageKey(cap.UsagePage, usages[usageIndex]));
                                }
                            }
                        }
                        results.Add(new HidButtonReport { ReportId = reportId, Pressed = pressed });
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(report);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(preparsed);
            }
            return results;
        }
    }

    internal sealed class KeyboardPress
    {
        public int VirtualKey;
        public KeyboardModifiers Modifiers;
    }

    internal sealed class KeyboardStateTracker
    {
        private readonly HashSet<int> keysDown = new HashSet<int>();

        public KeyboardPress Process(int virtualKey, bool keyUp)
        {
            int key = RawInputHelpers.NormalizeKey(virtualKey);
            if (key <= 0 || key == 255)
            {
                return null;
            }
            if (keyUp)
            {
                keysDown.Remove(key);
                return null;
            }

            bool firstPress = keysDown.Add(key);
            if (!firstPress || RawInputHelpers.IsModifierKey(key))
            {
                return null;
            }
            return new KeyboardPress
            {
                VirtualKey = key,
                Modifiers = RawInputHelpers.GetModifiers(keysDown)
            };
        }
    }

    internal sealed class HidButtonStateTracker
    {
        private readonly Dictionary<HidReportStateKey, HashSet<int>> buttonsDown = new Dictionary<HidReportStateKey, HashSet<int>>();

        public IEnumerable<int> Update(IntPtr device, IEnumerable<HidButtonReport> reports)
        {
            List<int> presses = new List<int>();
            foreach (HidButtonReport report in reports)
            {
                HidReportStateKey key = new HidReportStateKey(device, report.ReportId);
                HashSet<int> previous;
                if (!buttonsDown.TryGetValue(key, out previous))
                {
                    previous = new HashSet<int>();
                }
                foreach (int usage in report.Pressed)
                {
                    if (!previous.Contains(usage))
                    {
                        presses.Add(usage);
                    }
                }
                buttonsDown[key] = new HashSet<int>(report.Pressed);
            }
            return presses;
        }

        public void RemoveDevice(IntPtr device)
        {
            List<HidReportStateKey> keys = new List<HidReportStateKey>(buttonsDown.Keys);
            foreach (HidReportStateKey key in keys)
            {
                if (key.Device == device)
                {
                    buttonsDown.Remove(key);
                }
            }
        }

        private struct HidReportStateKey : IEquatable<HidReportStateKey>
        {
            public readonly IntPtr Device;
            private readonly int reportId;

            public HidReportStateKey(IntPtr device, int reportId)
            {
                Device = device;
                this.reportId = reportId;
            }

            public bool Equals(HidReportStateKey other)
            {
                return Device == other.Device && reportId == other.reportId;
            }

            public override bool Equals(object value)
            {
                return value is HidReportStateKey && Equals((HidReportStateKey)value);
            }

            public override int GetHashCode()
            {
                return Device.GetHashCode() ^ reportId;
            }
        }
    }

    internal sealed class HidButtonReport
    {
        public int ReportId;
        public HashSet<int> Pressed;
    }

    internal static class RawInputNative
    {
        public const int WM_INPUT = 0x00ff;
        public const int WM_INPUT_DEVICE_CHANGE = 0x00fe;
        public const int WM_CLOSE = 0x0010;
        public const int GIDC_REMOVAL = 2;
        public const uint RIM_TYPEKEYBOARD = 1;
        public const uint RIM_TYPEHID = 2;
        public const uint RID_INPUT = 0x10000003;
        public const uint RIDI_PREPARSEDDATA = 0x20000005;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RIDEV_DEVNOTIFY = 0x00002000;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        public static void Register(IntPtr handle)
        {
            RAWINPUTDEVICE[] devices =
            {
                NewDevice(0x01, 0x06, handle),
                NewDevice(0x01, 0x04, handle),
                NewDevice(0x01, 0x05, handle),
                NewDevice(0x01, 0x08, handle)
            };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static RAWINPUTDEVICE NewDevice(ushort page, ushort usage, IntPtr handle)
        {
            RAWINPUTDEVICE device = new RAWINPUTDEVICE();
            device.UsagePage = page;
            device.Usage = usage;
            device.Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY;
            device.Target = handle;
            return device;
        }

        public static RawInputPacket ReadPacket(IntPtr rawInputHandle)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            if (GetRawInputData(rawInputHandle, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(rawInputHandle, RID_INPUT, buffer, ref size, headerSize) != size)
                {
                    return null;
                }
                RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
                IntPtr data = IntPtr.Add(buffer, (int)headerSize);
                RawInputPacket packet = new RawInputPacket();
                packet.Type = header.Type;
                packet.Device = header.Device;

                if (header.Type == RIM_TYPEKEYBOARD)
                {
                    RAWKEYBOARD keyboard = (RAWKEYBOARD)Marshal.PtrToStructure(data, typeof(RAWKEYBOARD));
                    packet.VirtualKey = keyboard.VKey;
                    packet.KeyUp = (keyboard.Flags & 0x0001) != 0;
                }
                else if (header.Type == RIM_TYPEHID)
                {
                    packet.HidReportSize = Marshal.ReadInt32(data, 0);
                    packet.HidReportCount = Marshal.ReadInt32(data, 4);
                    int byteCount = checked(packet.HidReportSize * packet.HidReportCount);
                    packet.HidData = new byte[byteCount];
                    Marshal.Copy(IntPtr.Add(data, 8), packet.HidData, 0, byteCount);
                }
                return packet;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTHEADER
        {
            public uint Type;
            public uint Size;
            public IntPtr Device;
            public IntPtr WParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_BUTTON_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            public byte IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            public byte IsRange;
            public byte IsStringRange;
            public byte IsDesignatorRange;
            public byte IsAbsolute;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public uint[] Reserved;
            public HIDP_BUTTON_CAPS_RANGE Range;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_BUTTON_CAPS_RANGE
        {
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint deviceCount, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetButtonCaps(int reportType, [Out] HIDP_BUTTON_CAPS[] buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsedData, IntPtr report, uint reportLength);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
