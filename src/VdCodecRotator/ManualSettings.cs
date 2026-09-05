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
        public string DevicePath { get; set; }

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
                    return "Controller button " + ButtonUsage + (string.IsNullOrEmpty(DevicePath) ? " (any device)" : " (bound device)");
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
                ButtonUsage = ButtonUsage,
                DevicePath = DevicePath
            };
        }
    }

    internal sealed class ManualSettings
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; }
        public bool TimerEnabled { get; set; }
        public bool ManualEnabled { get; set; }
        public int CountdownSeconds { get; set; }
        public bool CountdownBeep { get; set; }
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
                ManualEnabled = ManualEnabled,
                CountdownSeconds = CountdownSeconds,
                CountdownBeep = CountdownBeep,
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
            if (CountdownSeconds != 0 && CountdownSeconds != 1 && CountdownSeconds != 3)
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
                return ManualSettings.CreateDefault();
            }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                ManualSettings settings = serializer.Deserialize<ManualSettings>(File.ReadAllText(path));
                if (settings != null && settings.Version == 1)
                {
                    settings.Version = ManualSettings.CurrentVersion;
                    settings.ManualEnabled = settings.Binding != null;
                    // Preserve the behavior of existing installations on upgrade.
                    settings.CountdownSeconds = 3;
                    settings.CountdownBeep = true;
                }
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
        private readonly ComboBox schedule;
        private readonly ComboBox countdown;
        private readonly CheckBox beep;
        private readonly ComboBox action;
        private readonly Label actionDescription;
        private readonly TextBox binding;
        private readonly Button bind;
        private readonly Button clear;
        private readonly NumericUpDown delay;
        private readonly Panel advancedPanel;
        private readonly CheckBox advanced;

        public event EventHandler TimerEnabledChanged;

        public ManualSettingsEditor(ManualSettings initial)
        {
            working = initial.Clone();
            Size = new Size(468, 235);
            AddLabel(this, "When should the stream reset?", 0, 0);
            schedule = NewChoice(this, "schedule", 0, 24, 458,
                "Automatically", "When I press a button", "Both");
            schedule.SelectedIndex = working.ManualEnabled ? (working.TimerEnabled ? 2 : 1) : 0;

            AddLabel(this, "Reset button", 0, 66);
            binding = new TextBox { ReadOnly = true, Location = new Point(96, 62), Size = new Size(210, 24) };
            binding.Text = working.Binding == null ? "Not bound" : working.Binding.DisplayText;
            Controls.Add(binding);
            bind = new Button { Text = "Bind...", Location = new Point(314, 60), Size = new Size(68, 27) };
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
            clear = new Button { Text = "Clear", Location = new Point(390, 60), Size = new Size(68, 27) };
            clear.Click += delegate { working.Binding = null; binding.Text = "Not bound"; };
            Controls.Add(clear);

            AddLabel(this, "Warning", 0, 104);
            countdown = NewChoice(this, "countdown", 96, 100, 210, "None (immediate)", "1 second", "3 seconds");
            countdown.SelectedIndex = working.CountdownSeconds == 3 ? 2 : working.CountdownSeconds;
            beep = new CheckBox { Text = "Beep during warning", AutoSize = true, Location = new Point(96, 132), Checked = working.CountdownBeep };
            Controls.Add(beep);

            actionDescription = new Label { Name = "actionDescription", Location = new Point(0, 162), Size = new Size(458, 36), ForeColor = SystemColors.GrayText };
            Controls.Add(actionDescription);
            advanced = new CheckBox { Text = "Advanced manual options", AutoSize = true, Location = new Point(0, 205) };
            Controls.Add(advanced);
            advancedPanel = new Panel { Location = new Point(0, 238), Size = new Size(468, 84), Visible = false };
            Controls.Add(advancedPanel);
            AddLabel(advancedPanel, "Action", 0, 4);
            action = NewChoice(advancedPanel, "action", 96, 0, 362,
                "Refresh and restore current codec", "Change codec and stay there");
            action.SelectedIndex = working.GetAction() == ManualAction.Switch ? 1 : 0;
            AddLabel(advancedPanel, "Refresh delay", 0, 42);
            delay = new NumericUpDown { Location = new Point(96, 38), Size = new Size(90, 24), Minimum = 0, Maximum = 60000, Value = working.RestartDelayMilliseconds };
            advancedPanel.Controls.Add(delay);
            AddLabel(advancedPanel, "milliseconds on the temporary codec", 194, 42);

            schedule.SelectedIndexChanged += delegate
            {
                RefreshEnabled();
                EventHandler handler = TimerEnabledChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            countdown.SelectedIndexChanged += delegate { RefreshEnabled(); };
            action.SelectedIndexChanged += delegate { RefreshEnabled(); };
            advanced.CheckedChanged += delegate
            {
                advancedPanel.Visible = advanced.Checked;
                Height = advanced.Checked ? 330 : 235;
            };
            RefreshEnabled();
        }

        private static void AddLabel(Control parent, string text, int x, int y)
        {
            parent.Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) });
        }

        private static ComboBox NewChoice(Control parent, string name, int x, int y, int width, params string[] items)
        {
            ComboBox choice = new ComboBox { Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y), Size = new Size(width, 24) };
            choice.Items.AddRange(items);
            parent.Controls.Add(choice);
            return choice;
        }

        public bool TimerEnabled { get { return schedule.SelectedIndex != 1; } }
        private bool ManualEnabled { get { return schedule.SelectedIndex != 0; } }

        internal string ValidationError
        {
            get
            {
                return ManualEnabled && (working.Binding == null || !working.Binding.IsValid)
                    ? "Bind a keyboard shortcut or controller button, or choose Automatically."
                    : null;
            }
        }

        public bool TryCreateSettings(IWin32Window owner, out ManualSettings settings)
        {
            settings = null;
            if (ValidationError != null)
            {
                MessageBox.Show(owner, ValidationError, "VirtualDesktopSwitcher");
                return false;
            }
            working.Version = ManualSettings.CurrentVersion;
            working.TimerEnabled = TimerEnabled;
            working.ManualEnabled = ManualEnabled;
            working.CountdownSeconds = countdown.SelectedIndex == 2 ? 3 : countdown.SelectedIndex;
            working.CountdownBeep = beep.Checked;
            working.Action = action.SelectedIndex == 1 ? "switch" : "restart";
            working.RestartDelayMilliseconds = (int)delay.Value;
            settings = working.Clone();
            return true;
        }

        private void RefreshEnabled()
        {
            binding.Enabled = bind.Enabled = clear.Enabled = ManualEnabled;
            countdown.Enabled = ManualEnabled;
            beep.Enabled = ManualEnabled && countdown.SelectedIndex != 0;
            advanced.Enabled = ManualEnabled;
            action.Enabled = ManualEnabled;
            delay.Enabled = ManualEnabled && action.SelectedIndex == 0;
            actionDescription.Text = !ManualEnabled
                ? "Uses the automatic interval. Your saved button is kept."
                : action.SelectedIndex == 1
                    ? "Changes codec and stays there. Works while your game has focus."
                    : "Briefly switches codec, then restores it. Works while your game has focus.";
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

            Action resize = delegate
            {
                int buttonsTop = editor.Bottom + 16;
                ok.Top = cancel.Top = buttonsTop;
                form.ClientSize = new Size(500, buttonsTop + 44);
            };
            editor.SizeChanged += delegate { resize(); };
            resize();

            if (form.ShowDialog() != DialogResult.OK)
            {
                throw new InvalidOperationException("Manual trigger configuration cancelled.");
            }
            return result;
        }
    }
}
