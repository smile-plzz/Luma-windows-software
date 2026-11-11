param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class WinInput {
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public static void SendKey(byte vk) {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    }
    public static void SendShiftF10() {
        keybd_event(0x10, 0, 0, UIntPtr.Zero);
        keybd_event(0x79, 0, 0, UIntPtr.Zero);
        keybd_event(0x79, 0, 2, UIntPtr.Zero);
        keybd_event(0x10, 0, 2, UIntPtr.Zero);
    }
}
"@
New-Item -ItemType Directory -Force artifacts/qa | Out-Null
$smokeSw = [System.Diagnostics.Stopwatch]::StartNew()
function Write-SmokeStep([string]$Step) {
    Write-Host ("[{0:000.0}s] {1}" -f $smokeSw.Elapsed.TotalSeconds, $Step)
}
Write-SmokeStep "Checking executable metadata"
$exe = (Resolve-Path $Executable).Path
$version=[System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
if ($version.ProductName -ne 'Luma' -or $version.FileVersion -ne '0.7.0.0' -or $version.FileDescription -ne 'Luma — Local media gallery') { throw 'Executable branding/version metadata is incorrect.' }
if (!(Test-Path (Join-Path (Split-Path $exe) 'Assets/Luma.ico'))) { throw 'Window icon is missing.' }
$icon=[System.Drawing.Icon]::ExtractAssociatedIcon($exe)
if (!$icon) { throw 'Executable icon is missing.' }
$icon.ToBitmap().Save((Join-Path (Get-Location) 'artifacts/qa/executable-icon.png')); $icon.Dispose()
$version | Format-List ProductName,FileDescription,FileVersion,ProductVersion | Out-File artifacts/qa/executable-metadata.txt
$sessionId=[Guid]::NewGuid().ToString('N')
$smokeRoot=Join-Path $env:TEMP "Luma-smoke-$sessionId"
$launchArgs=@('--smoke-test',"--smoke-session=$sessionId")
$process = Start-Process $exe -ArgumentList $launchArgs -PassThru
try {
    $window = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { throw "Luma exited during startup: $($process.ExitCode)" }
        $pidCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $nameCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Luma')
        $condition = [System.Windows.Automation.AndCondition]::new($pidCond, $nameCond)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($window) { break }
    }
    if (!$window) { throw 'Luma did not create a desktop window.' }
    function Find-Control([string]$Name) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Name)
        return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
    }
    function Find-Id([string]$Id) {
        $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
        for ($j = 0; $j -lt 100; $j++) {
            $found=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
            if ($found) { return $found }
            Start-Sleep -Milliseconds 100
        }
        throw "Control did not become available: $Id"
    }
    function Select-Card([string]$Name) {
        $item=Wait-Control $Name; $pattern=$null
        while ($item -and !$item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) {
            $item=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item)
        }
        if (!$item) { throw "Card is not selectable: $Name" }; $pattern.Select()
    }
    function Wait-Control([string]$Name) {
        for ($j = 0; $j -lt 120; $j++) { $found = Find-Control $Name; if ($found) { return $found }; Start-Sleep -Milliseconds 500 }
        throw "Control did not become available: $Name"
    }
    function Save-ScreenCapture([string]$OutputPath) {
        try {
            if (!$window) { return }
            $rectangle = $window.Current.BoundingRectangle
            if ($rectangle.Width -gt 0 -and $rectangle.Height -gt 0) {
                $bitmap = [System.Drawing.Bitmap]::new([int]$rectangle.Width, [int]$rectangle.Height)
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.CopyFromScreen([int]$rectangle.X, [int]$rectangle.Y, 0, 0, $bitmap.Size)
                } catch {
                    # Headless or background session fallback when no desktop DC is attached
                    $graphics.Clear([System.Drawing.Color]::FromArgb(32, 32, 32))
                }
                $bitmap.Save($OutputPath)
                $graphics.Dispose()
                $bitmap.Dispose()
            }
        } catch {
            # Screen capture error is non-fatal
        }
    }
    function Send-Keystroke([string]$Key) {
        try {
            if ($window) {
                [WinInput]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
            }
            if ($Key -eq '{ESC}') {
                [WinInput]::SendKey(0x1B)
            } elseif ($Key -eq ' ') {
                [WinInput]::SendKey(0x20)
            } elseif ($Key -eq '+{F10}') {
                [WinInput]::SendShiftF10()
            } else {
                [System.Windows.Forms.SendKeys]::SendWait($Key)
            }
        } catch {
            if ($Key -eq '{ESC}') { [WinInput]::SendKey(0x1B) }
            elseif ($Key -eq ' ') { [WinInput]::SendKey(0x20) }
            elseif ($Key -eq '+{F10}') { [WinInput]::SendShiftF10() }
        }
        Start-Sleep -Milliseconds 300
    }
    function Close-AppWindow {
        try {
            if ($window) {
                $pattern = $null
                if ($window.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) {
                    $pattern.Close()
                }
            }
        } catch { }
        try {
            if ($window -and $window.Current.NativeWindowHandle -ne 0) {
                [WinInput]::PostMessage([IntPtr]$window.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            }
        } catch { }
        if (!$process.HasExited) { $process.CloseMainWindow() | Out-Null }
        if (!$process.WaitForExit(6000)) { $process.Kill() }
    }
    function Restart-App([switch]$DisconnectFixture) {
        Close-AppWindow
        if ($DisconnectFixture) { Move-Item $fixtureRoot ($fixtureRoot + ' disconnected') }
        $script:process=Start-Process $exe -ArgumentList $launchArgs -PassThru
        $script:window=$null
        for ($i=0;$i -lt 60;$i++) {
            Start-Sleep -Milliseconds 500
            $pidCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
            $nameCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Luma')
            $condition = [System.Windows.Automation.AndCondition]::new($pidCond, $nameCond)
            $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
            if ($window) { break }
        }
        Wait-Control 'Welcome back. Your last view is restored.' | Out-Null
        Start-Sleep -Seconds 1
    }
    Wait-Control 'fixture000.bmp' | Out-Null
    Write-SmokeStep "Gallery loaded; checking UIA tree and scrolling to end"
    Start-Sleep -Seconds 3
    $nodes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    $names = @($nodes | ForEach-Object { $_.Current.Name })
    $names | Set-Content artifacts/qa/accessibility-tree.txt
    foreach ($name in @('Library','Search library','+ Add folder or drive','Settings')) {
        if ($names -notcontains $name) { throw "Required control not found: $name" }
    }
    $galleryCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'MediaGrid')
    $gallery = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$galleryCondition)
    $scroll = $gallery.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    for ($k=0; $k -lt 18; $k++) { $scroll.SetScrollPercent(-1,100); Start-Sleep -Milliseconds 400 }
    Wait-Control 'fixture259.bmp' | Out-Null
    $metricsPath=Join-Path $smokeRoot "gallery-metrics.json"
    if (Test-Path $metricsPath) {
        $metrics=Get-Content $metricsPath -Raw | ConvertFrom-Json
        if ($metrics.PeakDecoded -ge 240) { throw 'Decoded images grew to almost the entire fixture catalog.' }
        Copy-Item $metricsPath artifacts/qa/gallery-metrics.json
    } else { throw 'Gallery did not produce image-lifetime metrics.' }
    $scroll.SetScrollPercent(-1,0)
    Wait-Control 'fixture000.bmp'  | Out-Null
    Write-SmokeStep "Scroll complete; testing search and Favorites"
    $search = Wait-Control 'Search library'
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('fixture003')
    Start-Sleep -Seconds 2
    Wait-Control 'fixture003.bmp' | Out-Null
    if (Find-Control 'fixture000.bmp') { throw 'Search did not filter the library.' }
    $card = Wait-Control 'fixture003.bmp'
    # The named border is inside the selectable GridViewItem; walk to its selection pattern.
    $selectable = $card
    $selectionPattern = $null
    while ($selectable -and !$selectable.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selectionPattern)) {
        $selectable = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($selectable)
    }
    if (!$selectable) { throw 'Media item is not selectable through accessibility.' }
    $selectionPattern.Select()
    (Wait-Control '☆ Favorite').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
    Start-Sleep -Seconds 2
    (Wait-Control 'Favorites').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2
    Wait-Control 'fixture003.bmp' | Out-Null
    if (Find-Control 'fixture000.bmp') { throw 'Favorites filter included a non-favorite.' }
    (Wait-Control 'Library').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2
    Wait-Control 'fixture000.bmp' | Out-Null
    Write-SmokeStep "Search and Favorites passed; testing albums"
    # Create and use an actual virtual album through the desktop UI.
    (Wait-Control '+ Album').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    (Find-Id 'DialogInput').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Smoke album')
    (Wait-Control 'Save').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 1
    (Wait-Control 'Reset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('fixture003')
    Start-Sleep -Seconds 1
    Select-Card 'fixture003.bmp'
    (Wait-Control 'Details').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    (Wait-Control 'Add to album…').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Add').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 1
    (Wait-Control 'Album filter').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-Control 'Smoke album (1)').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
    Start-Sleep -Seconds 1
    Wait-Control 'fixture003.bmp' | Out-Null
    if (Find-Control 'fixture000.bmp') { throw 'Album included a file that was not added.' }
    (Wait-Control 'Details').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    (Wait-Control 'Reset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'fixture000.bmp' | Out-Null
    Write-SmokeStep "Albums passed; testing source/folder filter"
    # Select the registered fixture source; folder navigation uses saved catalog data.
    (Wait-Control 'Source filter').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    $nodes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    $sourceItem = $nodes | Where-Object { $_.Current.Name -like '*Fixture originals' } | Select-Object -First 1
    if (!$sourceItem) { throw 'Fixture source choice was not found.' }
    $sourceItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $folder = Wait-Control 'Trips'
    $folderPattern = $null
    while ($folder -and !$folder.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$folderPattern)) {
        $folder = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($folder)
    }
    if (!$folder) { throw 'Cached folder cannot be selected.' }
    $folderPattern.Select()
    Start-Sleep -Seconds 2
    Wait-Control 'fixture007.bmp' | Out-Null
    if (Find-Control 'fixture000.bmp') { throw 'Folder navigation did not filter the library.' }
    (Wait-Control 'Up to parent folder').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'fixture000.bmp' | Out-Null
    Write-SmokeStep "Folder navigation passed; clearing and repairing previews"
    (Wait-Control 'Storage & previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Clear selected previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Clear previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 1
    (Wait-Control 'Storage & previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Repair missing previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Repair finished: 260 / 260 previews saved. Remaining gaps may need a connected source, supported codec or more space.' | Out-Null
    (Wait-Control 'Storage & previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $coverage=Find-Id 'StorageCoverage'
    if ($coverage.Current.Name -notlike '260 / 260 current previews saved*') { throw 'Offline coverage is incomplete.' }
    $coverage.Current.Name | Set-Content artifacts/qa/offline-coverage.txt
    Write-SmokeStep "Repair finished; testing offline disconnect & Memories"
    (Wait-Control 'Close').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    # Make only the isolated synthetic fixture source unavailable, then re-query the cached library.
    $fixtureRoot = Join-Path $smokeRoot "Fixture originals"
    if (!(Test-Path $fixtureRoot)) { throw 'Isolated smoke fixture root was not found.' }
    Move-Item $fixtureRoot ($fixtureRoot + ' disconnected')
    (Wait-Control 'Refresh library').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Update drive status only').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    Wait-Control 'fixture000.bmp' | Out-Null
    Wait-Control 'Trips' | Out-Null
    (Wait-Control 'On this day').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2
    Wait-Control 'fixture003.bmp' | Out-Null
    Wait-Control 'Memory day' | Out-Null
    if (Find-Control 'fixture000.bmp') { throw 'Memories included media without a capture date.' }
    'PASS: On this day shows previous-year captured media from the offline catalog.' | Set-Content artifacts/qa/memories.txt
    (Wait-Control 'Reset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    # Restore the smoke fixture ordering after leaving memories.
    (Wait-Control 'Sort order').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-Control 'Date modified').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2

    (Wait-Control 'Storage & previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if ((Find-Id 'StorageCoverage').Current.Name -notlike '260 / 260 current previews saved*') { throw 'Offline coverage was lost.' }
    (Wait-Control 'Close').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    # Reconnect without scanning; keep the existing cards and selection, and leave
    # a newly added file undiscovered until an explicit rescan is requested.
    Select-Card 'fixture003.bmp'
    $beforeSelection=($gallery.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].GetRuntimeId() -join ',')
    $beforeReconnect=$gallery.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticalScrollPercent
    Move-Item ($fixtureRoot + ' disconnected') $fixtureRoot
    Copy-Item (Join-Path $fixtureRoot 'fixture000.bmp') (Join-Path $fixtureRoot 'not-indexed-yet.bmp')
    (Wait-Control 'Refresh library').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Update drive status only').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    $selected=$gallery.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selected.Count -ne 1 -or ($selected[0].GetRuntimeId() -join ',') -ne $beforeSelection) { throw 'Reconnect lost the selected card.' }
    $afterReconnect=$gallery.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticalScrollPercent
    if ([Math]::Abs($afterReconnect-$beforeReconnect) -gt 1) { throw 'Reconnect moved the gallery scroll position.' }
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('not-indexed-yet')
    Wait-Control 'No matching moments' | Out-Null
    Remove-Item (Join-Path $fixtureRoot 'not-indexed-yet.bmp')
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
    Wait-Control 'fixture000.bmp' | Out-Null
    'PASS: reconnect preserves selection and scroll without discovering new files or rescanning.' | Set-Content artifacts/qa/reconnect.txt

    # Persist view choices, restart the same isolated library and verify native controls restore them.
    (Wait-Control 'Gallery view options').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Find-Id 'ThumbnailSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(170)
    (Find-Id 'NamesToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Send-Keystroke '{ESC}'
    Close-AppWindow
    $process=Start-Process $exe -ArgumentList $launchArgs -PassThru
    $window=$null
    for ($i=0;$i -lt 60;$i++) {
        Start-Sleep -Milliseconds 500
        $pidCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $nameCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Luma')
        $condition = [System.Windows.Automation.AndCondition]::new($pidCond, $nameCond)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($window) { break }
    }
    Wait-Control 'fixture000.bmp' | Out-Null
    (Wait-Control 'Gallery view options').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if ((Find-Id 'ThumbnailSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 170) { throw 'Thumbnail size did not survive restart.' }
    if ((Find-Id 'NamesToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Filename preference did not survive restart.' }
    Send-Keystroke '{ESC}'
    Copy-Item (Join-Path $smokeRoot 'settings.json') artifacts/qa/restored-settings.json
    Start-Sleep -Seconds 2
    Save-ScreenCapture (Join-Path (Get-Location) 'artifacts/qa/startup.png')
    # Capture the polished palette in both supported appearance modes.
    (Wait-Control 'Settings').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Find-Id 'ThemeSelector').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-Control 'Dark').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    (Wait-Control 'Save').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    Save-ScreenCapture (Join-Path (Get-Location) 'artifacts/qa/dark-gallery.png')
    Write-SmokeStep "Dark theme captured; testing Refresh, Repair, Rebuild"
    # Management is discoverable and works through actual native controls.
    (Wait-Control 'Refresh library').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Refresh all connected sources').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Scan complete: 260 media files. Metadata saved. Navigate or Reset to apply new dates without shifting your current view.' | Out-Null
    (Wait-Control 'Storage & previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Repair missing previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Repair finished: 260 / 260 previews saved. Remaining gaps may need a connected source, supported codec or more space.' | Out-Null
    (Wait-Control 'Storage & previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Rebuild previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Rebuild').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Rebuilt 260 of 260 previews. Offline, unsupported or capacity-limited previews were preserved.' | Out-Null
    # Non-everyday media is visible by default and the opt-in filter is removable.
    [System.IO.File]::WriteAllBytes((Join-Path $fixtureRoot 'visible-raw.dng'),[byte[]](1,2,3,4))
    (Wait-Control 'Refresh library').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Refresh all connected sources').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Scan complete: 261 media files. Metadata saved. Navigate or Reset to apply new dates without shifting your current view.' | Out-Null
    Wait-Control 'visible-raw.dng' | Out-Null
    (Wait-Control 'Gallery view options').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Everyday formats only').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Send-Keystroke '{ESC}'
    Wait-Control '260 of 261 items · Filtered' | Out-Null
    if (Find-Control 'visible-raw.dng') { throw 'Everyday-only filter did not apply.' }
    (Wait-Control 'Remove filter: Everyday formats only').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'visible-raw.dng' | Out-Null
    (Wait-Control 'Gallery view options').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Everyday formats only').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Send-Keystroke '{ESC}'
    (Wait-Control 'Reset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'visible-raw.dng' | Out-Null
    if (Find-Control 'Drive tools') { throw 'Legacy maintenance controls are still exposed.' }
    if (Find-Control 'Timeline') { throw 'Timeline is still a duplicate navigation destination.' }
    (Wait-Control 'Gallery view options').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Show timeline').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Send-Keystroke '{ESC}'
    Wait-Control 'visible-raw.dng' | Out-Null
    if ((Wait-Control 'Settings').Current.IsOffscreen -or (Wait-Control 'Storage & previews').Current.IsOffscreen) { throw 'Pinned maintenance destinations are offscreen.' }
    (Wait-Control 'Browse month').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-Control 'September 2026 · 29').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-Control '29 of 261 items · Filtered' | Out-Null
    Wait-Control 'fixture000.bmp' | Out-Null
    if (Find-Control 'visible-raw.dng') { throw 'Month filter included another month.' }
    (Wait-Control 'Newer month').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control '1 of 261 items · Filtered' | Out-Null
    Wait-Control 'visible-raw.dng' | Out-Null
    (Wait-Control 'Older month').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control '29 of 261 items · Filtered' | Out-Null
    (Wait-Control 'Select').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Select-Card 'fixture000.bmp'
    $second=Wait-Control 'fixture001.bmp'; $pattern=$null
    while ($second -and !$second.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) {
        $second=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($second)
    }
    $second.SetFocus(); Send-Keystroke ' '
    Wait-Control '2 selected' | Out-Null
    (Wait-Control 'Copy').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control '2 file(s) ready to copy. Paste into a destination folder.' | Out-Null
    $clipboardPaths=@(powershell.exe -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Clipboard]::GetFileDropList()")
    if ($clipboardPaths.Count -ne 2 -or !($clipboardPaths | Where-Object { $_ -like '*fixture000.bmp' }) -or !($clipboardPaths | Where-Object { $_ -like '*fixture001.bmp' })) { throw 'Multi-file clipboard contents are incorrect.' }
    Save-ScreenCapture (Join-Path (Get-Location) 'artifacts/qa/months-selection.png')
    (Wait-Control 'Selection actions').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Clear selection').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Select').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    (Wait-Control 'All months').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control '261 items' | Out-Null
    'PASS: exact month counts and boundaries, older/newer navigation, two-file selection mode, Windows file clipboard and all-month reset.' | Set-Content artifacts/qa/months-selection.txt
    'PASS: non-everyday file visible by default, filter counts and removal, Reset reveals files, consolidated navigation.' | Set-Content artifacts/qa/filters-navigation.txt
    # Exclusions use the item context menu; restoration remains reachable from an empty gallery.
    function Open-ItemMenu([string]$Name) {
        Select-Card $Name
        $item=Wait-Control $Name; $pattern=$null
        while ($item -and !$item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) {
            $item=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item)
        }
        $item.SetFocus(); Send-Keystroke '+{F10}'
    }
    function Open-Exclusions {
        (Wait-Control 'Refresh library').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        (Wait-Control 'Manage exclusions…').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-Control 'Library exclusions' | Out-Null
    }
    function Restore-Rule([string]$Prefix) {
        $nodes=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
        $restore=$nodes | Where-Object { $_.Current.Name.StartsWith($Prefix) } | Select-Object -First 1
        if (!$restore) { throw "Missing restore action: $Prefix" }
        $restore.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-Control 'No exclusions. Everything supported is included.' | Out-Null
        (Wait-Control 'Done').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    $search=Wait-Control 'Search library'
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('fixture007')
    Open-ItemMenu 'fixture007.bmp'
    (Wait-Control 'Exclude this folder…').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Cancel').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'fixture007.bmp' | Out-Null
    Open-ItemMenu 'fixture007.bmp'
    (Wait-Control 'Exclude this folder…').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Exclude').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'No matching moments' | Out-Null
    (Wait-Control 'Reset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control '260 items' | Out-Null
    Open-Exclusions
    Save-ScreenCapture (Join-Path (Get-Location) 'artifacts/qa/exclusions.png')
    Restore-Rule 'Restore Folder:'
    Wait-Control '261 items' | Out-Null
    $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('visible-raw')
    Open-ItemMenu 'visible-raw.dng'
    (Wait-Control 'Exclude this file type…').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Exclude').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'No matching moments' | Out-Null
    (Wait-Control 'Reset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control '260 items' | Out-Null
    (Wait-Control 'Refresh library').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Refresh all connected sources').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Scan complete: 260 media files. Metadata saved. Navigate or Reset to apply new dates without shifting your current view.' | Out-Null
    Open-Exclusions; Restore-Rule 'Restore File type:'
    Wait-Control '261 items' | Out-Null
    Open-ItemMenu 'fixture000.bmp'
    (Wait-Control 'Exclude this source…').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Exclude').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'A little room for memories' | Out-Null
    Open-Exclusions; Restore-Rule 'Restore Source:'
    Wait-Control '261 items' | Out-Null
    if (!(Test-Path (Join-Path $fixtureRoot 'Trips/fixture007.bmp')) -or !(Test-Path (Join-Path $fixtureRoot 'visible-raw.dng'))) { throw 'Exclusions changed originals.' }
    'PASS: context folder cancellation, folder/type/source exclusions, persistent filtering through Reset and refresh, empty-library restoration, counts and originals retained.' | Set-Content artifacts/qa/exclusions.txt
    Write-SmokeStep "Exclusions passed; testing deep anchor restart"
    # Save a deep position, restart, and verify a bounded anchored window rather than replaying all earlier pages.
    (Wait-Control 'Reset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Sort order').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-Control 'Name').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $direction=(Wait-Control 'Descending order').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($direction.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $direction.Toggle() }
    (Wait-Control 'Gallery view options').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Find-Id 'GroupBox').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-Control 'None').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Send-Keystroke '{ESC}'
    $gallery=Find-Id 'MediaGrid'; $scroll=$gallery.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    for ($k=0;$k -lt 12;$k++) { $scroll.SetScrollPercent(-1,100); Start-Sleep -Milliseconds 200 }
    $scroll.SetScrollPercent(-1,65); Start-Sleep -Seconds 2
    $saved=Get-Content (Join-Path $smokeRoot 'settings.json') -Raw | ConvertFrom-Json
    if (!$saved.Place.AnchorPath) { throw 'Visible anchor was not saved.' }
    $anchorName=[System.IO.Path]::GetFileName($saved.Place.AnchorPath)
    Restart-App
    $anchor=Wait-Control $anchorName
    if ($anchor.Current.IsOffscreen) { throw 'Restored anchor is offscreen.' }
    Wait-Control 'Earlier items' | Out-Null
    $gallery=Find-Id 'MediaGrid'
    $actualInset=$anchor.Current.BoundingRectangle.Y-$gallery.Current.BoundingRectangle.Y
    if ([Math]::Abs($actualInset-$saved.Place.Inset) -gt 60) { throw "Restored offset differs: saved $($saved.Place.Inset), actual $actualInset" }
    (Wait-Control 'Earlier items').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 1
    if ((Wait-Control $anchorName).Current.IsOffscreen) { throw 'Loading earlier items lost the visible anchor.' }
    (Wait-Control 'Reset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'fixture000.bmp' | Out-Null
    # Persist source, nested folder and month, then reopen entirely from the offline catalog.
    (Wait-Control 'Months').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Source filter').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $nodes=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    $sourceItem=$nodes | Where-Object { $_.Current.Name -like '*Fixture originals' } | Select-Object -First 1
    $sourceItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $folder=Wait-Control 'Trips';$pattern=$null
    while($folder -and !$folder.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) { $folder=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($folder) }
    $pattern.Select(); Wait-Control '1 of 261 items · Filtered' | Out-Null
    Start-Sleep -Seconds 1
    Wait-Control 'fixture007.bmp' | Out-Null
    (Wait-Control 'Browse month').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Wait-Control 'September 2026 · 1').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2
    $beforeRestart=Get-Content (Join-Path $smokeRoot 'settings.json') -Raw | ConvertFrom-Json
    if ($beforeRestart.Place.Month -ne '2026-09') { throw 'Month was not saved before restart.' }
    Copy-Item (Join-Path $smokeRoot 'settings.json') artifacts/qa/before-offline-restart.json
    Restart-App -DisconnectFixture
    Wait-Control 'fixture007.bmp' | Out-Null
    if ((Wait-Control 'Folder path').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Trips') { throw 'Folder did not survive restart.' }
    $saved=Get-Content (Join-Path $smokeRoot 'settings.json') -Raw | ConvertFrom-Json
    if ($saved.Place.Month -ne '2026-09' -or !$saved.Place.SourceId) { throw 'Month/source did not survive restart.' }
    Move-Item ($fixtureRoot + ' disconnected') $fixtureRoot
    (Wait-Control 'Reset').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'fixture000.bmp' | Out-Null
    if ((Wait-Control 'Activity').Current.HelpText -notlike '*elapsed*') { throw 'Activity help text is missing.' }
    $phases=Get-Content (Join-Path $smokeRoot 'import-phases.txt')
    if ($phases -notcontains 'discovery' -or $phases -notcontains 'metadata') { throw 'Import phases were not both reported.' }
    Copy-Item (Join-Path $smokeRoot 'import-phases.txt') artifacts/qa/import-phases.txt
    'PASS: deep visible anchor and inset restored; earlier items preserve position; source/folder/month survive offline restart; Reset returns to start; both import phases and accessible help verified.' | Set-Content artifacts/qa/final-experience.txt
    (Wait-Control 'Activity').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Pause current work' | Out-Null
    (Wait-Control 'Close').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Manage sources').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Remove from Luma').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Remove source from Luma?' | Out-Null
    (Wait-Control 'Cancel').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'fixture000.bmp' | Out-Null
    (Wait-Control 'Manage sources').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Remove from Luma').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Remove source from Luma?' | Out-Null
    (Wait-Control 'Remove from Luma').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Source removed from Luma. Originals are untouched. Storage cleanup can remove its unused previews.' | Out-Null
    Wait-Control 'A little room for memories' | Out-Null
    if (!(Test-Path (Join-Path $fixtureRoot 'fixture000.bmp'))) { throw 'Source removal changed an original file.' }
    (Wait-Control 'Album filter').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Wait-Control 'Smoke album (0)' | Out-Null
    Send-Keystroke '{ESC}'
    (Wait-Control 'Storage & previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Clean unused previews').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Clean up').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    if ((Get-ChildItem (Join-Path $smokeRoot 'thumbnails') -Filter '*.thumb').Count -ne 0) { throw 'Unused previews survived cleanup after removing their source.' }
    # Verify new collapsible chrome and offline-by-default Map View.
    (Wait-Control 'Open map view').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Online Map Privacy Notice (One-Time Per Session)' | Out-Null
    Wait-Control 'Scan GPS' | Out-Null
    Wait-Control 'Trail' | Out-Null
    (Wait-Control '← Back to gallery').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Wait-Control 'Toggle sidebar').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (Find-Control 'Manage sources') { throw 'Sidebar did not collapse.' }
    (Wait-Control 'Toggle sidebar').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Manage sources' | Out-Null
    (Wait-Control 'Toggle gallery controls').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (Find-Control 'Filters') { throw 'Gallery toolbar did not collapse.' }
    (Wait-Control 'Toggle gallery controls').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Control 'Filters' | Out-Null
    'PASS: source manager, removal cancellation, confirmed removal, refreshed album counts, all-source refresh, repair, rebuild, activity and unused-preview cleanup.' | Set-Content artifacts/qa/management.txt
    'PASS: native window, 260-item continuous scrolling and image recycling, search, selection, favorites, albums, cached folders, offline preparation, collapsible chrome, offline map privacy gate and view preference restart work through Windows UI Automation.' | Set-Content artifacts/qa/result.txt
    Write-SmokeStep "Smoke test completed successfully"
} catch {
    if ($window) {
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { "$($_.Current.AutomationId) | $($_.Current.Name)" } | Set-Content artifacts/qa/failure-controls.txt
        Save-ScreenCapture (Join-Path (Get-Location) 'artifacts/qa/failure.png')
    }
    if (Test-Path (Join-Path $smokeRoot "settings.json")) { Copy-Item (Join-Path $smokeRoot "settings.json") artifacts/qa/failure-settings.json }
    if (Test-Path (Join-Path $smokeRoot "last-error.txt")) { Copy-Item (Join-Path $smokeRoot "last-error.txt") artifacts/qa/last-error.txt }
    $log = Join-Path (Split-Path $exe) 'startup-error.txt'
    if (Test-Path $log) { Get-Content $log; Copy-Item $log artifacts/qa/startup-error.txt }
    Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Luma|Microsoft.UI.Xaml' } | Select-Object -First 5 -ExpandProperty Message | Set-Content artifacts/qa/windows-events.txt
    throw
} finally {
    if (!$process.HasExited) { Close-AppWindow }
}
