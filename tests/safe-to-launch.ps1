# Is it OK to launch a test copy right now? Prints the reason and exits 1 when not:
#   - WoW is running (GPU tests wait until it's closed)
#   - the window in front is fullscreen on its monitor (likely a game or a video: a test window must not disturb it)
#   - the window in front is your own Haku Player (not a test copy): you're watching something
# Dot-source it (. tests\safe-to-launch.ps1) or run it; test scripts call it before every launch.
Add-Type -Namespace SafeLaunch -Name Win -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
[StructLayout(LayoutKind.Sequential)] public struct MI { public int cbSize; public R rcMonitor, rcWork; public uint dwFlags; }
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
[DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MI mi);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
'@ -ErrorAction SilentlyContinue
function Test-SafeToLaunch {
    [SafeLaunch.Win]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
    if (Get-Process Wow -ErrorAction SilentlyContinue) { return "WoW is running" }
    $h = [SafeLaunch.Win]::GetForegroundWindow()
    if ($h -eq [IntPtr]::Zero) { return $null }
    $fp = 0; [SafeLaunch.Win]::GetWindowThreadProcessId($h, [ref]$fp) | Out-Null
    $proc = Get-Process -Id $fp -ErrorAction SilentlyContinue
    $cls = New-Object Text.StringBuilder 128; [SafeLaunch.Win]::GetClassName($h, $cls, 128) | Out-Null
    if ($cls.ToString() -in 'Progman', 'WorkerW', 'Shell_TrayWnd') { return $null } # the desktop or taskbar
    if ($proc -and $proc.ProcessName -eq 'HakuPlayer' -and $proc.Path -notlike "$env:TEMP*" -and $proc.Path -notlike '*\HakuWinUI\bin\*') { return "your Haku Player is in front ($($proc.Path))" }
    $r = New-Object SafeLaunch.Win+R; [SafeLaunch.Win]::GetWindowRect($h, [ref]$r) | Out-Null
    $mi = New-Object SafeLaunch.Win+MI; $mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($mi)
    [SafeLaunch.Win]::GetMonitorInfo([SafeLaunch.Win]::MonitorFromWindow($h, 2), [ref]$mi) | Out-Null
    $m = $mi.rcMonitor
    if ($r.L -le $m.L -and $r.T -le $m.T -and $r.Rt -ge $m.Rt -and $r.B -ge $m.B) { return "a fullscreen window is in front ($(if ($proc) { $proc.ProcessName } else { "pid $fp" }))" }
    return $null
}
if ($MyInvocation.InvocationName -ne '.') {
    $why = Test-SafeToLaunch
    if ($why) { "NOT SAFE: $why"; exit 1 } else { "safe to launch" }
}
