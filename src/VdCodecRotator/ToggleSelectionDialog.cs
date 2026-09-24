using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VirtualDesktopSwitcher
{
    internal static class ToggleSelectionDialog
    {
        public static ToggleSelection Show(CodecInfo currentCodec, int initialIntervalMinutes, bool initialBeepWarning, bool initialSwitchBack, int initialSwitchBackDelayMilliseconds, ManualSettings manualSettings)
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
            bool configureManual = manualSettings != null;
            ManualSettings workingManual = configureManual ? manualSettings.Clone() : null;
            form.ClientSize = new Size(configureManual ? 500 : 420, configureManual ? 475 : 290);

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

            // Interval and timer options. With manual settings this panel sits under "When should the
            // stream reset?", which decides whether the timer runs at all.
            Panel timerSection = new Panel();
            timerSection.Size = new Size(388, 122);

            Label intervalLabel = new Label();
            intervalLabel.AutoSize = true;
            intervalLabel.Location = new Point(0, 4);
            intervalLabel.Text = "Switch every";
            timerSection.Controls.Add(intervalLabel);

            NumericUpDown intervalInput = new NumericUpDown();
            intervalInput.Location = new Point(84, 0);
            intervalInput.Size = new Size(72, 24);
            intervalInput.Minimum = 1;
            intervalInput.Maximum = 10080;
            intervalInput.Value = initialIntervalMinutes;
            timerSection.Controls.Add(intervalInput);

            Label minutesLabel = new Label();
            minutesLabel.AutoSize = true;
            minutesLabel.Location = new Point(164, 4);
            minutesLabel.Text = "minute(s)";
            timerSection.Controls.Add(minutesLabel);

            CheckBox beepWarningInput = new CheckBox();
            beepWarningInput.AutoSize = true;
            beepWarningInput.Location = new Point(0, 36);
            beepWarningInput.Text = "Beep 5 seconds before each timer switch";
            beepWarningInput.Checked = initialBeepWarning;
            timerSection.Controls.Add(beepWarningInput);

            CheckBox switchBackInput = new CheckBox();
            switchBackInput.AutoSize = true;
            switchBackInput.Location = new Point(0, 67);
            switchBackInput.Text = "Switch back to";
            switchBackInput.Checked = initialSwitchBack;
            timerSection.Controls.Add(switchBackInput);

            ComboBox switchBackCombo = new ComboBox();
            switchBackCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            switchBackCombo.Location = new Point(104, 64);
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
            timerSection.Controls.Add(switchBackCombo);

            Label switchBackDelayLabel = new Label();
            switchBackDelayLabel.AutoSize = true;
            switchBackDelayLabel.Location = new Point(17, 98);
            switchBackDelayLabel.Text = "Switch back after";
            timerSection.Controls.Add(switchBackDelayLabel);

            NumericUpDown switchBackDelayInput = new NumericUpDown();
            switchBackDelayInput.Location = new Point(134, 94);
            switchBackDelayInput.Size = new Size(72, 24);
            switchBackDelayInput.Minimum = 0;
            switchBackDelayInput.Maximum = 60000;
            switchBackDelayInput.Value = initialSwitchBackDelayMilliseconds;
            switchBackDelayInput.Enabled = switchBackInput.Checked;
            timerSection.Controls.Add(switchBackDelayInput);

            Label switchBackDelayUnitLabel = new Label();
            switchBackDelayUnitLabel.AutoSize = true;
            switchBackDelayUnitLabel.Location = new Point(214, 98);
            switchBackDelayUnitLabel.Text = "millisecond(s)";
            timerSection.Controls.Add(switchBackDelayUnitLabel);

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

            ManualSettingsEditor manualEditor = null;

            if (configureManual)
            {
                manualEditor = new ManualSettingsEditor(workingManual, timerSection);
                manualEditor.Location = new Point(16, 114);
                form.Controls.Add(manualEditor);

                Action refreshTimerControls = delegate
                {
                    intervalInput.Enabled = manualEditor.TimerEnabled;
                    beepWarningInput.Enabled = manualEditor.TimerEnabled;
                };
                manualEditor.TimerEnabledChanged += delegate { refreshTimerControls(); };
                refreshTimerControls();
            }
            else
            {
                timerSection.Location = new Point(16, 114);
                form.Controls.Add(timerSection);
            }

            Button okButton = new Button();
            okButton.Text = "OK";
            okButton.Location = new Point(configureManual ? 328 : 248, configureManual ? 438 : 244);
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
                else if (configureManual && !manualEditor.TryCreateSettings(form, out workingManual))
                {
                    form.DialogResult = DialogResult.None;
                }
            };
            form.AcceptButton = okButton;
            form.Controls.Add(okButton);

            Button cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.Location = new Point(configureManual ? 409 : 329, configureManual ? 438 : 244);
            cancelButton.Size = new Size(75, 28);
            cancelButton.DialogResult = DialogResult.Cancel;
            form.CancelButton = cancelButton;
            form.Controls.Add(cancelButton);

            if (configureManual)
            {
                Action resize = delegate
                {
                    int buttonsTop = manualEditor.Bottom + 12;
                    okButton.Top = cancelButton.Top = buttonsTop;
                    form.ClientSize = new Size(500, buttonsTop + 44);
                };
                manualEditor.SizeChanged += delegate { resize(); };
                resize();
            }

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
                SwitchBackDelayMilliseconds = (int)switchBackDelayInput.Value,
                ManualSettings = workingManual
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
}
