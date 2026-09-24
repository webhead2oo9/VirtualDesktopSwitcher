using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace VirtualDesktopSwitcher
{
    internal sealed class RawInputListener : IDisposable
    {
        private readonly ManualBinding binding;
        private readonly bool deviceBound;
        private readonly object triggerGate = new object();
        private readonly AutoResetEvent triggerSignal = new AutoResetEvent(false);
        private readonly ManualResetEvent readySignal = new ManualResetEvent(false);
        private readonly KeyboardStateTracker keyboardState = new KeyboardStateTracker(RawInputNative.IsKeyDown);
        private readonly HidButtonStateTracker hidState = new HidButtonStateTracker();
        private readonly HidDeviceCache hidDevices = new HidDeviceCache();
        private readonly Dictionary<IntPtr, string> devicePaths = new Dictionary<IntPtr, string>();
        private Thread thread;
        private RawInputWindow window;
        private Exception initializationError;
        private bool enabled = true;
        private bool pending;
        private bool disposed;
        private bool exactDevicePresent;
        private bool boundDevicePresent = true;

        public RawInputListener(ManualBinding binding)
        {
            if (binding == null || !binding.IsValid)
            {
                throw new ArgumentException("A valid manual binding is required.", "binding");
            }
            this.binding = binding.Clone();
            deviceBound = RawInputHelpers.IsHid(this.binding) && !string.IsNullOrEmpty(this.binding.DevicePath);

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

        // False when a controller binding has no matching controller connected at startup.
        public bool BoundDevicePresent
        {
            get { return boundDevicePresent; }
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
                bool hid = RawInputHelpers.IsHid(binding);
                window = new RawInputWindow(ProcessPacket, DeviceArrived, DeviceRemoved, !hid, hid);
                if (deviceBound)
                {
                    ScanDevices();
                }
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
            hidDevices.Dispose();
        }

        private void ScanDevices()
        {
            List<string> paths = new List<string>();
            foreach (IntPtr device in RawInputNative.GetHidDeviceHandles())
            {
                string path = GetDevicePath(device);
                if (path != null) paths.Add(path);
            }
            exactDevicePresent = RawInputHelpers.ContainsExactDevice(binding, paths);
            boundDevicePresent = RawInputHelpers.IsBoundDevicePresent(binding, paths);
        }

        private string GetDevicePath(IntPtr device)
        {
            string path;
            if (!devicePaths.TryGetValue(device, out path))
            {
                path = RawInputNative.GetDevicePath(device);
                if (path == null) return null;
                devicePaths[device] = path;
            }
            return path;
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
            if (!RawInputHelpers.IsHid(binding))
            {
                return;
            }

            if (deviceBound)
            {
                string path = GetDevicePath(packet.Device);
                if (path == null || !RawInputHelpers.MatchesDevice(binding, path, exactDevicePresent)) return;
            }

            HidDeviceInfo info = hidDevices.Get(packet.Device);
            if (info == null)
            {
                return;
            }

            int requested = RawInputHelpers.MakeUsageKey(binding.UsagePage, binding.ButtonUsage);
            foreach (int pressed in hidState.Update(packet.Device, RawInputHelpers.GetHidButtonReports(packet, info)))
            {
                if (pressed == requested)
                {
                    RaiseTrigger();
                }
            }
        }

        private void DeviceArrived(IntPtr device)
        {
            if (!deviceBound) return;
            string path = GetDevicePath(device);
            if (path != null && RawInputHelpers.IsExactDevice(binding, path))
            {
                exactDevicePresent = true;
            }
        }

        private void DeviceRemoved(IntPtr device)
        {
            hidState.RemoveDevice(device);
            hidDevices.Remove(device);
            string path;
            if (devicePaths.TryGetValue(device, out path))
            {
                devicePaths.Remove(device);
                if (deviceBound && RawInputHelpers.IsExactDevice(binding, path))
                {
                    exactDevicePresent = RawInputHelpers.ContainsExactDevice(binding, devicePaths.Values);
                }
            }
        }

        private void RaiseTrigger()
        {
            lock (triggerGate)
            {
                if (disposed || !enabled || pending)
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
            // The input thread may outlive the join timeout; stop it from signalling a closed handle.
            lock (triggerGate)
            {
                disposed = true;
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

        private sealed class CaptureForm : Form, IMessageFilter
        {
            private readonly KeyboardCaptureSession keyboard = new KeyboardCaptureSession(new KeyboardStateTracker(RawInputNative.IsKeyDown));
            private readonly HidButtonStateTracker hidState = new HidButtonStateTracker();
            private readonly HidDeviceCache hidDevices = new HidDeviceCache();
            private readonly System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer();
            private bool finished;
            public ManualBinding Binding;

            public CaptureForm()
            {
                Text = "Bind reset button";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ClientSize = new System.Drawing.Size(430, 125);
                TopMost = true;

                Label instructions = new Label();
                instructions.AutoSize = false;
                instructions.Location = new System.Drawing.Point(16, 16);
                instructions.Size = new System.Drawing.Size(398, 56);
                instructions.Text = "Press the key or controller button you want to use. For a key combination, hold Ctrl, Alt, Shift, or Win first. Press Esc to cancel.";
                Controls.Add(instructions);

                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.Location = new System.Drawing.Point(339, 79);
                cancel.Size = new System.Drawing.Size(75, 28);
                cancel.DialogResult = DialogResult.Cancel;
                Controls.Add(cancel);

                // WM_TIMER is only delivered once the queue is empty, so the dialog closes after the
                // key messages of the captured keystroke have been swallowed below.
                closeTimer.Interval = 1;
                closeTimer.Tick += delegate
                {
                    closeTimer.Stop();
                    DialogResult = Binding != null ? DialogResult.OK : DialogResult.Cancel;
                };
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                RawInputNative.Register(Handle, true, true);
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Application.AddMessageFilter(this);
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                Application.RemoveMessageFilter(this);
                base.OnFormClosed(e);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    closeTimer.Dispose();
                    hidDevices.Dispose();
                }
                base.Dispose(disposing);
            }

            // Keys are read through Raw Input. Letting the key messages through would deliver them to
            // the settings window once this dialog closes (Esc cancels it, Enter clicks Bind again).
            public bool PreFilterMessage(ref Message m)
            {
                return RawInputHelpers.IsKeyMessage(m.Msg);
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
                    hidDevices.Remove(m.LParam);
                }
                base.WndProc(ref m);
            }

            private void ProcessPacket(RawInputPacket packet)
            {
                if (finished)
                {
                    return;
                }

                if (packet.Type == RawInputNative.RIM_TYPEKEYBOARD)
                {
                    keyboard.Process(packet.VirtualKey, packet.KeyUp);
                    if (keyboard.Completed)
                    {
                        finished = true;
                        Binding = keyboard.Binding;
                        closeTimer.Start();
                    }
                }
                else if (packet.Type == RawInputNative.RIM_TYPEHID && !keyboard.Started)
                {
                    HidDeviceInfo info = hidDevices.Get(packet.Device);
                    if (info == null)
                    {
                        return;
                    }
                    foreach (int usageKey in hidState.Update(packet.Device, RawInputHelpers.GetHidButtonReports(packet, info)))
                    {
                        string devicePath = RawInputNative.GetDevicePath(packet.Device);
                        if (string.IsNullOrEmpty(devicePath)) return;
                        finished = true;
                        Binding = new ManualBinding
                        {
                            Kind = "hid",
                            UsagePage = (usageKey >> 16) & 0xffff,
                            ButtonUsage = usageKey & 0xffff,
                            DevicePath = devicePath,
                            DeviceName = RawInputNative.GetProductName(devicePath)
                        };
                        DialogResult = DialogResult.OK;
                        return;
                    }
                }
            }
        }
    }

    internal sealed class RawInputWindow : NativeWindow, IDisposable
    {
        private readonly Action<RawInputPacket> packetHandler;
        private readonly Action<IntPtr> arrivalHandler;
        private readonly Action<IntPtr> removalHandler;

        public RawInputWindow(Action<RawInputPacket> packetHandler, Action<IntPtr> arrivalHandler, Action<IntPtr> removalHandler, bool keyboard, bool controllers)
        {
            this.packetHandler = packetHandler;
            this.arrivalHandler = arrivalHandler;
            this.removalHandler = removalHandler;
            CreateParams parameters = new CreateParams();
            parameters.Caption = "VirtualDesktopSwitcherRawInput";
            CreateHandle(parameters);
            RawInputNative.Register(Handle, keyboard, controllers);
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
            else if (m.Msg == RawInputNative.WM_INPUT_DEVICE_CHANGE)
            {
                int change = m.WParam.ToInt32();
                if (change == RawInputNative.GIDC_ARRIVAL)
                {
                    arrivalHandler(m.LParam);
                }
                else if (change == RawInputNative.GIDC_REMOVAL)
                {
                    removalHandler(m.LParam);
                }
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
        internal static bool IsHid(ManualBinding binding)
        {
            return string.Equals(binding.Kind, "hid", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsKeyMessage(int message)
        {
            // WM_KEYDOWN (0x100) through WM_UNICHAR (0x109), including the WM_SYS* variants.
            return message >= 0x100 && message <= 0x109;
        }

        // "\\?\HID#VID_346E&PID_0006&MI_02#8&2f0f1e8&0&0000#{guid}": the second segment names the
        // controller model and interface; the third is an instance ID that, for controllers without
        // a serial number, changes when the controller is plugged into a different USB port.
        internal static string GetHardwareId(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;
            string[] parts = devicePath.Split('#');
            return parts.Length >= 4 && parts[1].Length > 0 ? parts[1] : null;
        }

        internal static bool IsExactDevice(ManualBinding binding, string devicePath)
        {
            return string.Equals(binding.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase);
        }

        // The bound controller wins while it is connected; otherwise the same model (for example the
        // same wheel on another USB port) is accepted.
        internal static bool MatchesDevice(ManualBinding binding, string devicePath, bool exactDevicePresent)
        {
            if (string.IsNullOrEmpty(binding.DevicePath) || IsExactDevice(binding, devicePath))
            {
                return true;
            }
            if (exactDevicePresent)
            {
                return false;
            }
            string boundHardwareId = GetHardwareId(binding.DevicePath);
            return boundHardwareId != null &&
                string.Equals(boundHardwareId, GetHardwareId(devicePath), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ContainsExactDevice(ManualBinding binding, IEnumerable<string> devicePaths)
        {
            foreach (string path in devicePaths)
            {
                if (IsExactDevice(binding, path)) return true;
            }
            return false;
        }

        internal static bool IsBoundDevicePresent(ManualBinding binding, IEnumerable<string> devicePaths)
        {
            if (string.IsNullOrEmpty(binding.DevicePath)) return true;
            foreach (string path in devicePaths)
            {
                if (MatchesDevice(binding, path, false)) return true;
            }
            return false;
        }

        // A failed read is unknown state, not a release of every held button.
        internal static HashSet<int> ReadPressedButtons(RawInputNative.HIDP_BUTTON_CAPS[] caps, int count,
            int reportId, Func<RawInputNative.HIDP_BUTTON_CAPS, ushort[]> readUsages)
        {
            HashSet<int> pressed = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                if (caps[i].ReportID != reportId) continue;
                ushort[] usages = readUsages(caps[i]);
                if (usages == null) return null;
                foreach (ushort usage in usages) pressed.Add(MakeUsageKey(caps[i].UsagePage, usage));
            }
            return pressed;
        }

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

        public static IEnumerable<HidButtonReport> GetHidButtonReports(RawInputPacket packet, HidDeviceInfo device)
        {
            List<HidButtonReport> results = new List<HidButtonReport>();
            if (device.ButtonCapCount == 0 || packet.HidData == null || packet.HidReportSize <= 0 || packet.HidReportCount <= 0)
            {
                return results;
            }

            GCHandle pinned = GCHandle.Alloc(packet.HidData, GCHandleType.Pinned);
            try
            {
                IntPtr data = pinned.AddrOfPinnedObject();
                for (int reportIndex = 0; reportIndex < packet.HidReportCount; reportIndex++)
                {
                    int reportOffset = reportIndex * packet.HidReportSize;
                    if (reportOffset + packet.HidReportSize > packet.HidData.Length)
                    {
                        break;
                    }
                    IntPtr report = IntPtr.Add(data, reportOffset);
                    int reportId = device.UsesReportIds ? packet.HidData[reportOffset] : 0;
                    HashSet<int> pressed = ReadPressedButtons(device.ButtonCaps, device.ButtonCapCount, reportId, delegate(RawInputNative.HIDP_BUTTON_CAPS cap)
                    {
                        // GetUsages returns all buttons on the page/collection, not just this cap.
                        uint maximumUsages = RawInputNative.HidP_MaxUsageListLength(0, cap.UsagePage, device.Preparsed);
                        if (maximumUsages == 0) return null;
                        ushort[] usages = new ushort[maximumUsages];
                        uint usageLength = (uint)usages.Length;
                        int status = RawInputNative.HidP_GetUsages(0, cap.UsagePage, cap.LinkCollection, usages, ref usageLength, device.Preparsed, report, (uint)packet.HidReportSize);
                        if (status != RawInputNative.HIDP_STATUS_SUCCESS || usageLength > usages.Length) return null;
                        Array.Resize(ref usages, (int)usageLength);
                        return usages;
                    });
                    if (pressed != null) results.Add(new HidButtonReport { ReportId = reportId, Pressed = pressed });
                }
            }
            finally
            {
                pinned.Free();
            }
            return results;
        }
    }

    // Preparsed data and button capabilities are fixed for a device handle. Reading them once instead
    // of per report matters because wheel bases and pedals typically report around 1000 times a second.
    internal sealed class HidDeviceInfo : IDisposable
    {
        public IntPtr Preparsed;
        public RawInputNative.HIDP_BUTTON_CAPS[] ButtonCaps;
        public int ButtonCapCount;
        public bool UsesReportIds;

        // Returns null when the device cannot be read; a device without buttons yields ButtonCapCount 0.
        public static HidDeviceInfo Load(IntPtr device)
        {
            uint preparsedSize = 0;
            RawInputNative.GetRawInputDeviceInfo(device, RawInputNative.RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize);
            if (preparsedSize == 0)
            {
                return null;
            }

            HidDeviceInfo info = new HidDeviceInfo();
            info.Preparsed = Marshal.AllocHGlobal((int)preparsedSize);
            if (RawInputNative.GetRawInputDeviceInfo(device, RawInputNative.RIDI_PREPARSEDDATA, info.Preparsed, ref preparsedSize) == uint.MaxValue)
            {
                info.Dispose();
                return null;
            }

            RawInputNative.HIDP_CAPS caps;
            if (RawInputNative.HidP_GetCaps(info.Preparsed, out caps) != RawInputNative.HIDP_STATUS_SUCCESS)
            {
                info.Dispose();
                return null;
            }

            ushort capCount = caps.NumberInputButtonCaps;
            info.ButtonCaps = new RawInputNative.HIDP_BUTTON_CAPS[capCount];
            if (capCount > 0 && RawInputNative.HidP_GetButtonCaps(0, info.ButtonCaps, ref capCount, info.Preparsed) != RawInputNative.HIDP_STATUS_SUCCESS)
            {
                info.Dispose();
                return null;
            }

            info.ButtonCapCount = capCount;
            for (int capIndex = 0; capIndex < capCount; capIndex++)
            {
                if (info.ButtonCaps[capIndex].ReportID != 0)
                {
                    info.UsesReportIds = true;
                    break;
                }
            }
            return info;
        }

        public void Dispose()
        {
            if (Preparsed != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Preparsed);
                Preparsed = IntPtr.Zero;
            }
        }
    }

    internal sealed class HidDeviceCache : IDisposable
    {
        private readonly Dictionary<IntPtr, HidDeviceInfo> devices = new Dictionary<IntPtr, HidDeviceInfo>();

        public HidDeviceInfo Get(IntPtr device)
        {
            HidDeviceInfo info;
            if (!devices.TryGetValue(device, out info))
            {
                info = HidDeviceInfo.Load(device);
                if (info != null)
                {
                    devices[device] = info;
                }
            }
            return info;
        }

        public void Remove(IntPtr device)
        {
            HidDeviceInfo info;
            if (devices.TryGetValue(device, out info))
            {
                devices.Remove(device);
                info.Dispose();
            }
        }

        public void Dispose()
        {
            foreach (HidDeviceInfo info in devices.Values)
            {
                info.Dispose();
            }
            devices.Clear();
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
        private readonly Func<int, bool> isKeyDown;

        public KeyboardStateTracker()
            : this(null)
        {
        }

        // isKeyDown reads the live key state. It drops modifiers whose key-up never arrived, for
        // example because they were released while the secure desktop (Ctrl+Alt+Del, UAC) had input.
        public KeyboardStateTracker(Func<int, bool> isKeyDown)
        {
            this.isKeyDown = isKeyDown;
        }

        public bool AnyKeysDown
        {
            get { return keysDown.Count > 0; }
        }

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
            DropReleasedModifiers(key);
            return new KeyboardPress
            {
                VirtualKey = key,
                Modifiers = RawInputHelpers.GetModifiers(keysDown)
            };
        }

        private void DropReleasedModifiers(int pressedKey)
        {
            // If the live state does not even show the key being pressed right now, it is unavailable
            // rather than evidence that the modifiers were released.
            if (isKeyDown == null || !isKeyDown(pressedKey))
            {
                return;
            }
            keysDown.RemoveWhere(key => RawInputHelpers.IsModifierKey(key) && !isKeyDown(key));
        }
    }

    // Keyboard half of the Bind dialog. The dialog finishes only after every key is released so that
    // no part of the captured keystroke reaches the settings window afterwards.
    internal sealed class KeyboardCaptureSession
    {
        private const int EscapeKey = 0x1b;
        private readonly KeyboardStateTracker keys;
        private ManualBinding binding;
        private bool cancelled;

        public KeyboardCaptureSession(KeyboardStateTracker keys)
        {
            this.keys = keys;
        }

        public ManualBinding Binding
        {
            get { return binding; }
        }

        public bool Cancelled
        {
            get { return cancelled; }
        }

        public bool Started
        {
            get { return binding != null || cancelled; }
        }

        public bool Completed
        {
            get { return Started && !keys.AnyKeysDown; }
        }

        public void Process(int virtualKey, bool keyUp)
        {
            KeyboardPress press = keys.Process(virtualKey, keyUp);
            if (press == null || Started)
            {
                return;
            }
            if (press.VirtualKey == EscapeKey && press.Modifiers == KeyboardModifiers.None)
            {
                cancelled = true;
                return;
            }
            binding = new ManualBinding
            {
                Kind = "keyboard",
                VirtualKey = press.VirtualKey,
                Modifiers = (int)press.Modifiers
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
        internal static string GetDevicePath(IntPtr device)
        {
            uint length = 0;
            if (GetRawInputDeviceName(device, RIDI_DEVICENAME, null, ref length) == uint.MaxValue || length == 0) return null;
            StringBuilder name = new StringBuilder((int)length);
            return GetRawInputDeviceName(device, RIDI_DEVICENAME, name, ref length) == uint.MaxValue ? null : name.ToString();
        }

        internal static List<IntPtr> GetHidDeviceHandles()
        {
            List<IntPtr> handles = new List<IntPtr>();
            uint size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            // The list can grow between the count query and the read; retry a few times.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint count = 0;
                if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count == 0) return handles;
                RAWINPUTDEVICELIST[] list = new RAWINPUTDEVICELIST[count];
                uint returned = GetRawInputDeviceList(list, ref count, size);
                if (returned == uint.MaxValue) continue;
                for (int i = 0; i < returned; i++)
                {
                    if (list[i].Type == RIM_TYPEHID) handles.Add(list[i].Device);
                }
                return handles;
            }
            return handles;
        }

        internal static string GetProductName(string devicePath)
        {
            using (SafeFileHandle file = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (file.IsInvalid) return null;
                StringBuilder name = new StringBuilder(256);
                if (!HidD_GetProductString(file, name, name.Capacity * 2)) return null;
                string text = name.ToString().Trim();
                return text.Length == 0 ? null : text;
            }
        }

        internal static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        public const int WM_INPUT = 0x00ff;
        public const int WM_INPUT_DEVICE_CHANGE = 0x00fe;
        public const int WM_CLOSE = 0x0010;
        public const int GIDC_ARRIVAL = 1;
        public const int GIDC_REMOVAL = 2;
        public const uint RIM_TYPEKEYBOARD = 1;
        public const uint RIM_TYPEHID = 2;
        public const uint RID_INPUT = 0x10000003;
        public const uint RIDI_PREPARSEDDATA = 0x20000005;
        public const uint RIDI_DEVICENAME = 0x20000007;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RIDEV_DEVNOTIFY = 0x00002000;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        // Registers only the input the binding needs: a controller binding does not need to see every
        // keystroke, and a keyboard binding does not need every wheel and pedal report.
        public static void Register(IntPtr handle, bool keyboard, bool controllers)
        {
            List<RAWINPUTDEVICE> devices = new List<RAWINPUTDEVICE>();
            if (keyboard)
            {
                devices.Add(NewDevice(0x01, 0x06, handle));
            }
            if (controllers)
            {
                devices.Add(NewDevice(0x01, 0x04, handle));
                devices.Add(NewDevice(0x01, 0x05, handle));
                devices.Add(NewDevice(0x01, 0x08, handle));
            }
            RAWINPUTDEVICE[] registrations = devices.ToArray();
            if (!RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
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

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTDEVICELIST
        {
            public IntPtr Device;
            public uint Type;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint deviceCount, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

        [DllImport("hid.dll")]
        internal static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsedData);

        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetRawInputDeviceName(IntPtr device, uint command, StringBuilder data, ref uint size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] devices, ref uint deviceCount, uint size);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetProductString(SafeFileHandle device, StringBuilder buffer, int bufferLength);

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
