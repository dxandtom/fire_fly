# 和 Claude 一起的时长：计时、间隔过滤、每日经验上限、只聊天的每日总结、报告文字、换日
# 用法：powershell -NoProfile -STA -File tests\t_time.ps1（只用临时数据目录，不碰真实数据）
param([string]$Exe = "$PSScriptRoot\..\plugins\firefly-pet\bin\FireflyPet.exe", [string]$Dir = "$env:TEMP\ff-t-time")
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
Add-Type -AssemblyName PresentationFramework
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class J {
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int dx, int dy, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  public static void Jiggle() { mouse_event(1, 1, 0, 0, IntPtr.Zero); mouse_event(1, -1, 0, 0, IntPtr.Zero); }
  public static int FgPid() { int p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
}
"@
if (Test-Path $Dir) { Remove-Item -Recurse -Force $Dir }
New-Item -ItemType Directory $Dir | Out-Null
$t = (Get-Date).ToString('yyyy-MM-dd')
[IO.File]::WriteAllText("$Dir\stats.json", "{`"Xp`":100,`"FirstMet`":`"2026-10-01`",`"Today`":`"$t`",`"TodayCompletes`":0,`"TodayClaudeSeconds`":590,`"ClaudeSeconds`":590,`"History`":[]}")
[IO.File]::WriteAllText("$Dir\config.json", '{"AutoStart":false,"FollowClaude":false,"AutorunHintShown":true,"SystemNotify":false,"HideInFullscreen":false,"BreakReminder":false}')
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$S = [Reflection.BindingFlags]'Public,NonPublic,Static'; $I = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$prog = $asm.GetType('FireflyPet.Program')
$prog.GetField('DataDir', $S).SetValue($null, $Dir)
$prog.GetField('AppDir', $S).SetValue($null, [IO.Path]::GetDirectoryName($Exe) + '\')
$desk = $asm.GetType('FireflyPet.Desk')

# 0) 真实前台检测
$fp = [int][J]::FgPid(); "fgpid=$fp"; $desk.GetField('fgPid', $S).SetValue($null, -1)
"real foreground: " + $(if ($fp -gt 0) { (Get-Process -Id $fp -EA SilentlyContinue).ProcessName } else { "<none/locked>" }) + " -> ClaudeInForeground=" + $desk.GetMethod('ClaudeInForeground', $S).Invoke($null, $null)

$pw = $asm.GetType('FireflyPet.PetWindow')
$w = [Activator]::CreateInstance($pw, $I, $null, @($false), $null)
$st = $pw.GetField('stats', $I).GetValue($w)
$track = $pw.GetMethod('TrackClaudeTime', $I); $ltt = $pw.GetField('lastTimeTick', $I)
function Tick($gapSec) {
  $desk.GetField('fgPid', $S).SetValue($null, [J]::FgPid()); $desk.GetField('fgKind', $S).SetValue($null, 1)
  [J]::Jiggle(); Start-Sleep -Milliseconds 50
  $now = Get-Date; $ltt.SetValue($w, $now.AddSeconds(-$gapSec)); $track.Invoke($w, @($now))
  "  gap=$gapSec -> today=" + $st.TodayClaudeSeconds + " total=" + $st.ClaudeSeconds + " xp=" + $st.Xp
}
"1) normal ticks (590s start)"; Tick 6; Tick 6
"2) gap < 5 ignored"; Tick 3
"3) gap > 15 (sleep) not counted"; Tick 40
"4) cap: crossing 12th chunk gives xp, 13th does not"
$st.TodayClaudeSeconds = 12 * 600 - 3; Tick 6
$st.TodayClaudeSeconds = 13 * 600 - 3; Tick 6
"5) not foreground"
$desk.GetField('fgPid', $S).SetValue($null, [J]::FgPid()); $desk.GetField('fgKind', $S).SetValue($null, 0)
$before = $st.TodayClaudeSeconds; $nw = [datetime]::Now; $ltt.SetValue($w, $nw.AddSeconds(-6)); $track.Invoke($w, @($nw)); "  added=" + ($st.TodayClaudeSeconds - $before)
"5b) terminal in front: counts only while a Claude Code session was active in the last 3 min"
function TermTick() { $desk.GetField('fgPid', $S).SetValue($null, [J]::FgPid()); $desk.GetField('fgKind', $S).SetValue($null, 2)
  [J]::Jiggle(); Start-Sleep -Milliseconds 50
  $b0 = $st.TodayClaudeSeconds; $nw = [datetime]::Now; $ltt.SetValue($w, $nw.AddSeconds(-6)); $track.Invoke($w, @($nw)); return ($st.TodayClaudeSeconds - $b0) }
$sess = $pw.GetField('sessions', $I).GetValue($w); $sess.Clear()
"  no session: added=" + (TermTick)
$so = [Activator]::CreateInstance($asm.GetType('FireflyPet.Session'), $true); $so.GetType().GetField('Last').SetValue($so, [datetime]::Now.AddMinutes(-10)); $sess['old'] = $so
"  session idle 10 min: added=" + (TermTick)
$so.GetType().GetField('Last').SetValue($so, [datetime]::Now.AddSeconds(-30))
"  session active 30s ago: added=" + (TermTick)
$sess.Clear()
"6) chat-only day summary"
$st.TodayClaudeSeconds = 2 * 3600 + 15 * 60; $st.TodayCompletes = 0
$prog.GetField('TestHour', $S).SetValue($null, 19)
"  MaybeSummary=" + $pw.GetMethod('MaybeSummary', $I).Invoke($w, $null) + " bubble=" + $pw.GetField('bubbleText', $I).GetValue($w).Text
"7) report text"
$pw.GetMethod('OpenReport', $I).Invoke($w, @('day')); $rw = $pw.GetField('report', $I).GetValue($w)
foreach ($p in 'day', 'week', 'year') { $rw.ShowPeriod($p)
  $q = New-Object System.Collections.Queue; $q.Enqueue($rw.Body); $hits = @()
  while ($q.Count) { $e = $q.Dequeue(); if ($e -is [System.Windows.Controls.TextBlock] -and $e.Text -like '*Claude*') { $hits += $e.Text }
    foreach ($c in [System.Windows.LogicalTreeHelper]::GetChildren($e)) { if ($c -is [System.Windows.DependencyObject]) { $q.Enqueue($c) } } }
  "  [$p] " + (($hits | Where-Object { $_ -like '*Claude*' -and $_ -notlike '*v1.4.0*' } | ForEach-Object { $_.Substring(0, [Math]::Min(90, $_.Length)) }) -join ' | ') }
$rw.Close()
"8) roll day"
$st.Today = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd'); $pw.GetMethod('RollDay', $I).Invoke($w, $null)
$h = $st.History[$st.History.Count - 1]; "  history: " + $h.Day + " claude=" + $h.ClaudeSeconds + "; today reset=" + $st.TodayClaudeSeconds
$w.Close()
Remove-Item -Recurse -Force $Dir -EA SilentlyContinue
