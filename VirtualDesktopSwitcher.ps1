<#
.SYNOPSIS
Rotates the Virtual Desktop Streamer preferred codec on a timer.

.DESCRIPTION
Uses Windows UI Automation to change the Preferred Codec dropdown in the
Virtual Desktop Streamer app. This triggers the same live setting path as a
manual UI change, which direct edits to StreamerSettings.json do not.

When -Codecs and -TargetCodec are omitted, the script detects the current codec
and shows a popup asking which different codec should be used as the toggle pair
and how many minutes to wait between switches.

.PARAMETER Codecs
Comma-separated or array list of codec codes to rotate through.
Known codes: Automatic, H264, H264Plus, HEVC, HEVC10bit, AV110bit.

.PARAMETER IntervalMinutes
Minutes to wait between codec switches. Defaults to 25. In the interactive
setup popup, this value is used as the initial timer value and can be changed.

.PARAMETER TargetCodec
Sets one exact codec and exits.

.PARAMETER Once
Switches to the next codec once and exits.

.PARAMETER SwitchImmediately
Switches immediately before entering the timed loop.
#>
[CmdletBinding()]
param(
    [string[]] $Codecs,
    [ValidateRange(1, 10080)]
    [int] $IntervalMinutes = 25,
    [string] $TargetCodec,
    [switch] $Once,
    [switch] $SwitchImmediately,
    [string] $StreamerPath = 'C:\Program Files\Virtual Desktop Streamer\VirtualDesktop.Streamer.exe',
    [int] $UiTimeoutSeconds = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ('VirtualDesktopSwitcher.User32' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

namespace VirtualDesktopSwitcher
{
    public static class User32
    {
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
'@
}

$Script:CodecInfos = @{
    Automatic = [pscustomobject]@{ Code = 'Automatic'; Display = 'Automatic' }
    H264      = [pscustomobject]@{ Code = 'H264';      Display = 'H.264' }
    H264Plus  = [pscustomobject]@{ Code = 'H264Plus';  Display = 'H.264+' }
    HEVC      = [pscustomobject]@{ Code = 'HEVC';      Display = 'HEVC' }
    HEVC10bit = [pscustomobject]@{ Code = 'HEVC10bit'; Display = 'HEVC 10-bit' }
    AV110bit  = [pscustomobject]@{ Code = 'AV110bit';  Display = 'AV1 10-bit' }
}

function Write-Status {
    param([string] $Message)
    '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
}

function ConvertTo-CodecKey {
    param([string] $Name)

    return $Name.ToUpperInvariant().Replace('+', 'PLUS') -replace '[^A-Z0-9]', ''
}

function Resolve-Codec {
    param([string] $Name)

    $key = ConvertTo-CodecKey $Name
    switch ($key) {
        'AUTOMATIC' { return $Script:CodecInfos.Automatic }
        'H264' { return $Script:CodecInfos.H264 }
        'H264PLUS' { return $Script:CodecInfos.H264Plus }
        'HEVC' { return $Script:CodecInfos.HEVC }
        'HEVC10BIT' { return $Script:CodecInfos.HEVC10bit }
        'AV110BIT' { return $Script:CodecInfos.AV110bit }
        default {
            $known = ($Script:CodecInfos.Values | ForEach-Object { $_.Code }) -join ', '
            throw "Unknown codec '$Name'. Known codec codes: $known"
        }
    }
}

function Test-RectIsEmpty {
    param($Rect)

    return $Rect.IsEmpty -or $Rect.Width -le 0 -or $Rect.Height -le 0
}

function New-ControlTypeCondition {
    param($ControlType)

    return [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $ControlType
    )
}

function New-NameCondition {
    param([string] $Name)

    return [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name
    )
}

function Get-StreamerRoot {
    $deadline = (Get-Date).AddSeconds($UiTimeoutSeconds)
    $started = $false

    while ((Get-Date) -lt $deadline) {
        $process = Get-Process -Name 'VirtualDesktop.Streamer' -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1

        if (-not $process -and -not $started -and (Test-Path -LiteralPath $StreamerPath)) {
            Start-Process -FilePath $StreamerPath | Out-Null
            $started = $true
        }

        if ($process) {
            [VirtualDesktopSwitcher.User32]::ShowWindow($process.MainWindowHandle, 9) | Out-Null
            [VirtualDesktopSwitcher.User32]::SetForegroundWindow($process.MainWindowHandle) | Out-Null

            $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            if ($root) {
                return $root
            }
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Could not find the Virtual Desktop Streamer window. Open the Streamer once, then retry."
}

function Select-OptionsTab {
    param([System.Windows.Automation.AutomationElement] $Root)

    $condition = [System.Windows.Automation.AndCondition]::new(
        (New-ControlTypeCondition ([System.Windows.Automation.ControlType]::TabItem)),
        (New-NameCondition 'OPTIONS')
    )
    $tab = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $tab) {
        throw "Could not find the OPTIONS tab in Virtual Desktop Streamer."
    }

    $selection = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    if (-not $selection.Current.IsSelected) {
        $selection.Select()
        Start-Sleep -Milliseconds 200
    }
}

function Get-CodecComboBox {
    param([System.Windows.Automation.AutomationElement] $Root)

    $textCondition = [System.Windows.Automation.AndCondition]::new(
        (New-ControlTypeCondition ([System.Windows.Automation.ControlType]::Text)),
        (New-NameCondition 'Preferred Codec')
    )
    $labels = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
    $labelRects = @(
        for ($i = 0; $i -lt $labels.Count; $i++) {
            $rect = $labels.Item($i).Current.BoundingRectangle
            if (-not (Test-RectIsEmpty $rect)) {
                $rect
            }
        }
    )

    $comboCondition = New-ControlTypeCondition ([System.Windows.Automation.ControlType]::ComboBox)
    $combos = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboCondition)
    $visibleCombos = @(
        for ($i = 0; $i -lt $combos.Count; $i++) {
            $combo = $combos.Item($i)
            $rect = $combo.Current.BoundingRectangle
            if (-not (Test-RectIsEmpty $rect)) {
                [pscustomobject]@{ Element = $combo; Rect = $rect }
            }
        }
    )

    if ($visibleCombos.Count -eq 0) {
        throw "Could not find any visible ComboBox controls on the OPTIONS tab."
    }

    foreach ($labelRect in $labelRects) {
        $candidate = $visibleCombos |
            Where-Object {
                $_.Rect.Top -ge $labelRect.Top -and
                $_.Rect.Top -le ($labelRect.Bottom + 60) -and
                [Math]::Abs($_.Rect.Left - $labelRect.Left) -le 20
            } |
            Sort-Object { $_.Rect.Top } |
            Select-Object -First 1

        if ($candidate) {
            return $candidate.Element
        }
    }

    if ($visibleCombos.Count -gt 1) {
        Write-Warning ("Could not match the 'Preferred Codec' label to a dropdown; using the topmost of {0} visible ComboBoxes. If the wrong setting changes, the Streamer layout may have changed." -f $visibleCombos.Count)
    }

    return ($visibleCombos | Sort-Object { $_.Rect.Top } | Select-Object -First 1).Element
}

function Get-CodecListItems {
    param(
        [System.Windows.Automation.AutomationElement] $Root,
        [System.Windows.Automation.AutomationElement] $Combo
    )

    $comboRect = $Combo.Current.BoundingRectangle
    $itemCondition = New-ControlTypeCondition ([System.Windows.Automation.ControlType]::ListItem)
    $deadline = (Get-Date).AddSeconds(3)

    while ((Get-Date) -lt $deadline) {
        $items = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)
        $matches = @(
            for ($i = 0; $i -lt $items.Count; $i++) {
                $item = $items.Item($i)
                $rect = $item.Current.BoundingRectangle
                $name = $item.Current.Name
                if (
                    -not (Test-RectIsEmpty $rect) -and
                    $name -match '^\[[^,]+, .+\]$' -and
                    [Math]::Abs($rect.Left - $comboRect.Left) -le 25
                ) {
                    $item
                }
            }
        )

        if ($matches.Count -gt 0) {
            return $matches
        }

        Start-Sleep -Milliseconds 100
    }

    throw "The codec dropdown opened, but no codec list items were visible."
}

function Get-CodecFromListItemName {
    param([string] $Name)

    if ($Name -match '^\[(?<code>[^,]+), (?<display>.+)\]$') {
        return $Matches.code
    }

    foreach ($info in $Script:CodecInfos.Values) {
        if ($Name -eq $info.Display -or $Name -eq $info.Code) {
            return $info.Code
        }
    }

    return $Name
}

function Test-ListItemMatchesCodec {
    param(
        [System.Windows.Automation.AutomationElement] $Item,
        $CodecInfo
    )

    $name = $Item.Current.Name
    $expected = '[{0}, {1}]' -f $CodecInfo.Code, $CodecInfo.Display

    return $name -eq $expected -or $name -eq $CodecInfo.Code -or $name -eq $CodecInfo.Display
}

function Get-SelectionItemPatternOrNull {
    param([System.Windows.Automation.AutomationElement] $Element)

    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref] $pattern)) {
        return $pattern
    }

    return $null
}

function Open-CodecDropdown {
    param([System.Windows.Automation.AutomationElement] $Combo)

    $expandCollapse = $Combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($expandCollapse.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $expandCollapse.Expand()
        Start-Sleep -Milliseconds 200
    }

    return $expandCollapse
}

function Get-VdCodecState {
    $root = Get-StreamerRoot
    Select-OptionsTab $root
    $combo = Get-CodecComboBox $root
    $expandCollapse = Open-CodecDropdown $combo

    try {
        $items = Get-CodecListItems -Root $root -Combo $combo
        $availableByCode = [ordered]@{}
        $currentCodec = $null

        foreach ($item in $items) {
            $selection = Get-SelectionItemPatternOrNull $item
            if ($selection) {
                $codecInfo = Resolve-Codec (Get-CodecFromListItemName $item.Current.Name)
                if (-not $availableByCode.Contains($codecInfo.Code)) {
                    $availableByCode[$codecInfo.Code] = $codecInfo
                }

                if ($selection.Current.IsSelected) {
                    $currentCodec = $codecInfo
                }
            }
        }

        if (-not $currentCodec) {
            throw "Could not determine the currently selected codec."
        }

        return [pscustomobject]@{
            Current = $currentCodec
            Available = @($availableByCode.Values)
        }
    }
    finally {
        try { $expandCollapse.Collapse() } catch { }
    }
}

function Get-VdPreferredCodec {
    return (Get-VdCodecState).Current.Code
}

function Set-VdPreferredCodec {
    param([string] $Codec)

    $codecInfo = Resolve-Codec $Codec
    $root = Get-StreamerRoot
    Select-OptionsTab $root
    $combo = Get-CodecComboBox $root
    $expandCollapse = Open-CodecDropdown $combo

    try {
        $items = Get-CodecListItems -Root $root -Combo $combo
        $target = $items |
            Where-Object {
                (Test-ListItemMatchesCodec -Item $_ -CodecInfo $codecInfo) -and
                (Get-SelectionItemPatternOrNull $_)
            } |
            Select-Object -First 1

        if (-not $target) {
            $available = ($items | ForEach-Object { $_.Current.Name }) -join ', '
            throw "Codec '$Codec' is not available in the current dropdown. Available items: $available"
        }

        $selection = Get-SelectionItemPatternOrNull $target
        if (-not $selection) {
            throw "Codec '$Codec' was found, but it is not selectable through UI Automation."
        }

        if (-not $selection.Current.IsSelected) {
            $selection.Select()
            Start-Sleep -Milliseconds 500
            if (-not $selection.Current.IsSelected) {
                throw "Selected codec '$Codec' in the dropdown, but the Streamer did not apply it."
            }
        }

        return $codecInfo.Code
    }
    finally {
        try { $expandCollapse.Collapse() } catch { }
    }
}

function Get-SuggestedToggleCodec {
    param(
        $CurrentCodec,
        [object[]] $AvailableCodecs
    )

    $candidateOrder = switch ($CurrentCodec.Code) {
        'HEVC10bit' { @('HEVC', 'H264Plus', 'H264', 'AV110bit', 'Automatic') }
        'HEVC' { @('HEVC10bit', 'H264Plus', 'H264', 'AV110bit', 'Automatic') }
        'H264Plus' { @('HEVC10bit', 'HEVC', 'H264', 'AV110bit', 'Automatic') }
        'H264' { @('H264Plus', 'HEVC10bit', 'HEVC', 'AV110bit', 'Automatic') }
        'AV110bit' { @('HEVC10bit', 'HEVC', 'H264Plus', 'H264', 'Automatic') }
        default { @('HEVC10bit', 'HEVC', 'H264Plus', 'H264', 'AV110bit') }
    }

    foreach ($candidateCode in $candidateOrder) {
        $candidate = $AvailableCodecs | Where-Object { $_.Code -eq $candidateCode } | Select-Object -First 1
        if ($candidate -and $candidate.Code -ne $CurrentCodec.Code) {
            return $candidate
        }
    }

    return $AvailableCodecs | Where-Object { $_.Code -ne $CurrentCodec.Code } | Select-Object -First 1
}

function Show-ToggleCodecDialog {
    param(
        $CodecState,
        [int] $InitialIntervalMinutes
    )

    $currentCodec = $CodecState.Current
    $options = @(
        $CodecState.Available |
            Where-Object { $_.Code -ne $currentCodec.Code } |
            ForEach-Object {
                [pscustomobject]@{
                    Code = $_.Code
                    Display = $_.Display
                    Label = '{0} ({1})' -f $_.Display, $_.Code
                }
            }
    )

    if ($options.Count -lt 1) {
        throw "Virtual Desktop only exposed one codec option, so there is nothing different to toggle to."
    }

    $suggested = Get-SuggestedToggleCodec -CurrentCodec $currentCodec -AvailableCodecs $CodecState.Available

    [System.Windows.Forms.Application]::EnableVisualStyles()

    $form = [System.Windows.Forms.Form]::new()
    $form.Text = 'VirtualDesktopSwitcher'
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ShowInTaskbar = $true
    $form.TopMost = $true
    $form.ClientSize = [System.Drawing.Size]::new(420, 218)

    $label = [System.Windows.Forms.Label]::new()
    $label.AutoSize = $false
    $label.Location = [System.Drawing.Point]::new(16, 16)
    $label.Size = [System.Drawing.Size]::new(388, 48)
    $label.Text = "Current codec is $($currentCodec.Display). Choose the different codec to toggle with."
    $form.Controls.Add($label)

    $combo = [System.Windows.Forms.ComboBox]::new()
    $combo.DropDownStyle = 'DropDownList'
    $combo.DisplayMember = 'Label'
    $combo.Location = [System.Drawing.Point]::new(16, 72)
    $combo.Size = [System.Drawing.Size]::new(388, 24)
    foreach ($option in $options) {
        [void] $combo.Items.Add($option)
    }

    $suggestedIndex = 0
    for ($i = 0; $i -lt $combo.Items.Count; $i++) {
        if ($suggested -and $combo.Items[$i].Code -eq $suggested.Code) {
            $suggestedIndex = $i
            break
        }
    }
    $combo.SelectedIndex = $suggestedIndex
    $form.Controls.Add($combo)

    $intervalLabel = [System.Windows.Forms.Label]::new()
    $intervalLabel.AutoSize = $true
    $intervalLabel.Location = [System.Drawing.Point]::new(16, 118)
    $intervalLabel.Text = 'Switch every'
    $form.Controls.Add($intervalLabel)

    $intervalInput = [System.Windows.Forms.NumericUpDown]::new()
    $intervalInput.Location = [System.Drawing.Point]::new(100, 114)
    $intervalInput.Size = [System.Drawing.Size]::new(72, 24)
    $intervalInput.Minimum = 1
    $intervalInput.Maximum = 10080
    $intervalInput.Value = $InitialIntervalMinutes
    $form.Controls.Add($intervalInput)

    $minutesLabel = [System.Windows.Forms.Label]::new()
    $minutesLabel.AutoSize = $true
    $minutesLabel.Location = [System.Drawing.Point]::new(180, 118)
    $minutesLabel.Text = 'minute(s)'
    $form.Controls.Add($minutesLabel)

    $okButton = [System.Windows.Forms.Button]::new()
    $okButton.Text = 'OK'
    $okButton.Location = [System.Drawing.Point]::new(248, 168)
    $okButton.Size = [System.Drawing.Size]::new(75, 28)
    $okButton.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $okButton
    $form.Controls.Add($okButton)

    $cancelButton = [System.Windows.Forms.Button]::new()
    $cancelButton.Text = 'Cancel'
    $cancelButton.Location = [System.Drawing.Point]::new(329, 168)
    $cancelButton.Size = [System.Drawing.Size]::new(75, 28)
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancelButton
    $form.Controls.Add($cancelButton)

    $result = $form.ShowDialog()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
        throw "Codec selection cancelled."
    }

    return [pscustomobject]@{
        Codec = Resolve-Codec $combo.SelectedItem.Code
        IntervalMinutes = [int] $intervalInput.Value
    }
}

function Get-NextCodec {
    param(
        [string] $CurrentCodec,
        [object[]] $ResolvedCodecs
    )

    $codes = @($ResolvedCodecs | ForEach-Object { $_.Code })
    $index = [Array]::IndexOf($codes, $CurrentCodec)
    if ($index -lt 0) {
        Write-Warning "Current codec '$CurrentCodec' is not in the rotation list; starting from '$($ResolvedCodecs[0].Code)'."
        return $ResolvedCodecs[0]
    }

    return $ResolvedCodecs[($index + 1) % $ResolvedCodecs.Count]
}

function Switch-ToNextCodec {
    param([object[]] $ResolvedCodecs)

    $current = Get-VdPreferredCodec
    $next = Get-NextCodec -CurrentCodec $current -ResolvedCodecs $ResolvedCodecs
    $selected = Set-VdPreferredCodec $next.Code

    Write-Status "Preferred Codec: $current -> $selected"
}

if ($TargetCodec) {
    $selected = Set-VdPreferredCodec $TargetCodec
    Write-Status "Preferred Codec set to $selected"
    exit 0
}

if ($PSBoundParameters.ContainsKey('Codecs')) {
    $codecNames = @(
        $Codecs |
            ForEach-Object { $_ -split ',' } |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ }
    )
    $resolvedCodecs = @($codecNames | ForEach-Object { Resolve-Codec $_ })
    $distinctCodecCount = @($resolvedCodecs | Select-Object -ExpandProperty Code -Unique).Count
    if ($distinctCodecCount -lt 2) {
        throw "Specify at least two different codecs in -Codecs."
    }
}
else {
    $codecState = Get-VdCodecState
    $toggleSelection = Show-ToggleCodecDialog -CodecState $codecState -InitialIntervalMinutes $IntervalMinutes
    $toggleCodec = $toggleSelection.Codec
    $IntervalMinutes = $toggleSelection.IntervalMinutes
    $resolvedCodecs = @($codecState.Current, $toggleCodec)
    Write-Status "Toggle pair selected: $($codecState.Current.Code), $($toggleCodec.Code); interval: $IntervalMinutes minute(s)"
}

if ($Once) {
    Switch-ToNextCodec $resolvedCodecs
    exit 0
}

Write-Status "VirtualDesktopSwitcher started. Codecs: $(($resolvedCodecs | ForEach-Object { $_.Code }) -join ', '); interval: $IntervalMinutes minute(s)."

if ($SwitchImmediately) {
    Switch-ToNextCodec $resolvedCodecs
}

while ($true) {
    Start-Sleep -Seconds ($IntervalMinutes * 60)
    Switch-ToNextCodec $resolvedCodecs
}
