using System;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VirtualDesktopSwitcher
{
    internal enum ManualAction
    {
        Restart,
        Switch
    }

    [Flags]
    internal enum KeyboardModifiers
    {
        None = 0,
        Control = 1,
        Alt = 2,
        Shift = 4,
        Windows = 8
    }

    internal sealed class ManualBinding
    {
        public string Kind { get; set; }
        public int VirtualKey { get; set; }
        public int Modifiers { get; set; }
        public int UsagePage { get; set; }
        public int ButtonUsage { get; set; }

        [ScriptIgnore]
        public bool IsValid
        {
            get
            {
                if (string.Equals(Kind, "keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    return VirtualKey > 0 && VirtualKey <= 255 && (Modifiers & ~15) == 0 && !RawInputHelpers.IsModifierKey(VirtualKey);
                }
                if (string.Equals(Kind, "hid", StringComparison.OrdinalIgnoreCase))
                {
                    return UsagePage > 0 && UsagePage <= 65535 && ButtonUsage > 0 && ButtonUsage <= 65535;
                }
                return false;
            }
        }

        [ScriptIgnore]
        public string DisplayText
        {
            get
            {
                if (!IsValid)
                {
                    return "Not bound";
                }
                if (string.Equals(Kind, "hid", StringComparison.OrdinalIgnoreCase))
                {
                    return "HID button (usage page 0x" + UsagePage.ToString("X") + ", usage " + ButtonUsage + ")";
                }

                string text = string.Empty;
                KeyboardModifiers modifiers = (KeyboardModifiers)Modifiers;
                if ((modifiers & KeyboardModifiers.Control) != 0) text += "Ctrl+";
                if ((modifiers & KeyboardModifiers.Alt) != 0) text += "Alt+";
                if ((modifiers & KeyboardModifiers.Shift) != 0) text += "Shift+";
                if ((modifiers & KeyboardModifiers.Windows) != 0) text += "Win+";
                return text + ((Keys)VirtualKey).ToString();
            }
        }

        public ManualBinding Clone()
        {
            return new ManualBinding
            {
                Kind = Kind,
                VirtualKey = VirtualKey,
                Modifiers = Modifiers,
                UsagePage = UsagePage,
                ButtonUsage = ButtonUsage
            };
        }
    }

    internal sealed class ManualSettings
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; }
        public bool TimerEnabled { get; set; }
        public string Action { get; set; }
        public int RestartDelayMilliseconds { get; set; }
        public ManualBinding Binding { get; set; }

        public static ManualSettings CreateDefault()
        {
            return new ManualSettings
            {
                Version = CurrentVersion,
                TimerEnabled = true,
                Action = "restart",
                RestartDelayMilliseconds = 50,
                Binding = null
            };
        }

        public ManualAction GetAction()
        {
            return string.Equals(Action, "switch", StringComparison.OrdinalIgnoreCase)
                ? ManualAction.Switch
                : ManualAction.Restart;
        }

        public ManualSettings Clone()
        {
            return new ManualSettings
            {
                Version = Version,
                TimerEnabled = TimerEnabled,
                Action = Action,
                RestartDelayMilliseconds = RestartDelayMilliseconds,
                Binding = Binding == null ? null : Binding.Clone()
            };
        }

        public bool IsValid()
        {
            if (Version != CurrentVersion || RestartDelayMilliseconds < 0 || RestartDelayMilliseconds > 60000)
            {
                return false;
            }
            if (!string.Equals(Action, "restart", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Action, "switch", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return Binding == null || Binding.IsValid;
        }
    }

    internal static class ManualSettingsStore
    {
        public static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VirtualDesktopSwitcher",
                    "manual-settings.json");
            }
        }

        public static ManualSettings Load(out string warning)
        {
            return LoadFrom(SettingsPath, out warning);
        }

        internal static ManualSettings LoadFrom(string path, out string warning)
        {
            warning = null;
            if (!File.Exists(path))
            {
                warning = "Manual settings were not found at '" + path + "'; using defaults.";
                return ManualSettings.CreateDefault();
            }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                ManualSettings settings = serializer.Deserialize<ManualSettings>(File.ReadAllText(path));
                if (settings == null || !settings.IsValid())
                {
                    throw new InvalidDataException("The settings version or values are not supported.");
                }
                return settings;
            }
            catch (Exception ex)
            {
                warning = "Could not read manual settings at '" + path + "' (" + ex.Message + "); using defaults.";
                return ManualSettings.CreateDefault();
            }
        }

        public static void Save(ManualSettings settings)
        {
            SaveTo(SettingsPath, settings);
        }

        internal static void SaveTo(string path, ManualSettings settings)
        {
            if (settings == null || !settings.IsValid())
            {
                throw new InvalidOperationException("Manual settings are invalid and were not saved.");
            }

            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                File.WriteAllText(temporaryPath, serializer.Serialize(settings));
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
    }

    internal sealed class ManualSettingsEditor : UserControl
    {
        private readonly ManualSettings working;
        private readonly CheckBox timer;
        private readonly CheckBox manual;
        private readonly ComboBox action;
        private readonly TextBox binding;
        private readonly Button bind;
        private readonly Button clear;
        private readonly NumericUpDown delay;

        public event EventHandler TimerEnabledChanged;

        public ManualSettingsEditor(ManualSettings initial)
        {
            working = initial.Clone();
            Size = new Size(468, 184);

            Label header = new Label();
            header.AutoSize = true;
            header.Font = new Font(header.Font, FontStyle.Bold);
            header.Location = new Point(0, 0);
            header.Text = "Scheduling and manual trigger";
            Controls.Add(header);

            timer = new CheckBox();
            timer.AutoSize = true;
            timer.Location = new Point(0, 30);
            timer.Text = "Enable automatic timer";
            timer.Checked = working.TimerEnabled;
            Controls.Add(timer);

            manual = new CheckBox();
            manual.AutoSize = true;
            manual.Location = new Point(0, 60);
            manual.Text = "Enable manual trigger";
            manual.Checked = working.Binding != null && working.Binding.IsValid;
            Controls.Add(manual);

            Label actionLabel = new Label();
            actionLabel.AutoSize = true;
            actionLabel.Location = new Point(17, 94);
            actionLabel.Text = "Action";
            Controls.Add(actionLabel);

            action = new ComboBox();
            action.DropDownStyle = ComboBoxStyle.DropDownList;
            action.Location = new Point(96, 90);
            action.Size = new Size(170, 24);
            action.Items.Add("Restart stream");
            action.Items.Add("Switch codec");
            action.SelectedIndex = working.GetAction() == ManualAction.Switch ? 1 : 0;
            Controls.Add(action);

            Label bindingLabel = new Label();
            bindingLabel.AutoSize = true;
            bindingLabel.Location = new Point(17, 128);
            bindingLabel.Text = "Binding";
            Controls.Add(bindingLabel);

            binding = new TextBox();
            binding.ReadOnly = true;
            binding.Location = new Point(96, 124);
            binding.Size = new Size(210, 24);
            binding.Text = working.Binding == null ? "Not bound" : working.Binding.DisplayText;
            Controls.Add(binding);

            bind = new Button();
            bind.Location = new Point(314, 122);
            bind.Size = new Size(68, 27);
            bind.Text = "Bind...";
            bind.Click += delegate
            {
                ManualBinding captured = RawInputCaptureDialog.Show(this);
                if (captured != null)
                {
                    working.Binding = captured;
                    binding.Text = captured.DisplayText;
                }
            };
            Controls.Add(bind);

            clear = new Button();
            clear.Location = new Point(390, 122);
            clear.Size = new Size(68, 27);
            clear.Text = "Clear";
            clear.Click += delegate
            {
                working.Binding = null;
                binding.Text = "Not bound";
            };
            Controls.Add(clear);

            Label delayLabel = new Label();
            delayLabel.AutoSize = true;
            delayLabel.Location = new Point(17, 162);
            delayLabel.Text = "Restart delay";
            Controls.Add(delayLabel);

            delay = new NumericUpDown();
            delay.Location = new Point(96, 158);
            delay.Size = new Size(90, 24);
            delay.Minimum = 0;
            delay.Maximum = 60000;
            delay.Value = working.RestartDelayMilliseconds;
            Controls.Add(delay);

            Label delayUnit = new Label();
            delayUnit.AutoSize = true;
            delayUnit.Location = new Point(194, 162);
            delayUnit.Text = "millisecond(s)";
            Controls.Add(delayUnit);

            timer.CheckedChanged += delegate
            {
                EventHandler handler = TimerEnabledChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };
            manual.CheckedChanged += delegate { RefreshEnabled(); };
            action.SelectedIndexChanged += delegate { RefreshEnabled(); };
            RefreshEnabled();
        }

        public bool TimerEnabled
        {
            get { return timer.Checked; }
        }

        public bool TryCreateSettings(IWin32Window owner, out ManualSettings settings)
        {
            settings = null;
            if (manual.Checked && (working.Binding == null || !working.Binding.IsValid))
            {
                MessageBox.Show(owner, "Bind a non-modifier keyboard key/chord or a discrete HID button, or disable the manual trigger.", "VirtualDesktopSwitcher");
                return false;
            }

            working.Version = ManualSettings.CurrentVersion;
            working.TimerEnabled = timer.Checked;
            if (!manual.Checked)
            {
                working.Binding = null;
            }
            working.Action = action.SelectedIndex == 1 ? "switch" : "restart";
            working.RestartDelayMilliseconds = (int)delay.Value;
            settings = working.Clone();
            return true;
        }

        private void RefreshEnabled()
        {
            action.Enabled = manual.Checked;
            binding.Enabled = manual.Checked;
            bind.Enabled = manual.Checked;
            clear.Enabled = manual.Checked;
            delay.Enabled = manual.Checked && action.SelectedIndex == 0;
        }
    }

    internal static class ManualSettingsConfigurationDialog
    {
        public static ManualSettings Show(ManualSettings initial)
        {
            Application.EnableVisualStyles();

            Form form = new Form();
            form.Text = "Manual trigger settings";
            form.StartPosition = FormStartPosition.CenterScreen;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.TopMost = true;
            form.ClientSize = new Size(500, 280);

            ManualSettingsEditor editor = new ManualSettingsEditor(initial);
            editor.Location = new Point(16, 16);
            form.Controls.Add(editor);

            ManualSettings result = null;

            Button ok = new Button();
            ok.Text = "Save";
            ok.Location = new Point(318, 235);
            ok.Size = new Size(75, 28);
            ok.DialogResult = DialogResult.OK;
            ok.Click += delegate
            {
                if (!editor.TryCreateSettings(form, out result))
                {
                    form.DialogResult = DialogResult.None;
                }
            };
            form.AcceptButton = ok;
            form.Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Location = new Point(401, 235);
            cancel.Size = new Size(75, 28);
            cancel.DialogResult = DialogResult.Cancel;
            form.CancelButton = cancel;
            form.Controls.Add(cancel);

            if (form.ShowDialog() != DialogResult.OK)
            {
                throw new InvalidOperationException("Manual trigger configuration cancelled.");
            }
            return result;
        }
    }
}
