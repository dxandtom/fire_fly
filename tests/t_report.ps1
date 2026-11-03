# 陪伴报告四个时段、窗口复用、悬停不消耗每日总结、会话一览关闭不报错
# 用法：powershell -NoProfile -STA -File tests\t_report.ps1（只用临时数据目录，不碰真实数据）
param([string]$Exe = "$PSScriptRoot\..\plugins\firefly-pet\bin\FireflyPet.exe", [string]$Dir = "$env:TEMP\ff-t-report")
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
Add-Type -AssemblyName PresentationFramework
if (Test-Path $Dir) { Remove-Item -Recurse -Force $Dir }
New-Item -ItemType Directory $Dir | Out-Null
$today = Get-Date
$fmt = 'yyyy-MM-dd'
$hist = @()
foreach ($i in 1, 2, 3, 8, 9, 20, 40, 100) { $d = $today.AddDays(-$i).ToString($fmt); $hist += "{`"Day`":`"$d`",`"Completes`":$($i % 7 + 1),`"Tools`":$($i * 3),`"Prompts`":$($i % 4 + 1)}" }
$t = $today.ToString($fmt)
[IO.File]::WriteAllText("$Dir\stats.json", "{`"Xp`":800,`"Completes`":50,`"Prompts`":60,`"Tools`":900,`"Pats`":30,`"FirstMet`":`"2026-10-01`",`"Today`":`"$t`",`"TodayCompletes`":4,`"TodayTools`":20,`"TodayPrompts`":5,`"TodayPats`":2,`"LastActiveDay`":`"$t`",`"Streak`":3,`"BestStreak`":5,`"History`":[" + ($hist -join ',') + "]}")
[IO.File]::WriteAllText("$Dir\config.json", '{"AutoStart":false,"FollowClaude":false,"AutorunHintShown":true,"SystemNotify":false,"HideInFullscreen":false,"BreakReminder":false}')
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$S = [Reflection.BindingFlags]'Public,NonPublic,Static'; $I = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$prog = $asm.GetType('FireflyPet.Program')
$prog.GetField('DataDir', $S).SetValue($null, $Dir)
$prog.GetField('AppDir', $S).SetValue($null, [IO.Path]::GetDirectoryName($Exe) + '\')
$pw = $asm.GetType('FireflyPet.PetWindow')
$w = [Activator]::CreateInstance($pw, $I, $null, @($false), $null)
$st = $pw.GetField('stats', $I).GetValue($w)

function Texts($el) {
  $out = New-Object System.Collections.Generic.List[string]
  $q = New-Object System.Collections.Queue; $q.Enqueue($el)
  while ($q.Count -gt 0) { $e = $q.Dequeue()
    if ($e -is [System.Windows.Controls.TextBlock]) { if ($e.Text) { $out.Add($e.Text) } }
    foreach ($c in [System.Windows.LogicalTreeHelper]::GetChildren($e)) { if ($c -is [System.Windows.DependencyObject]) { $q.Enqueue($c) } } }
  return $out
}
# 1) 报告窗口四个时段
$pw.GetMethod('OpenReport', $I).Invoke($w, @('day'))
$rw = $pw.GetField('report', $I).GetValue($w)
foreach ($p in 'day', 'week', 'month', 'year') {
  $rw.ShowPeriod($p)
  $tx = Texts $rw.Body | Where-Object { $_.Length -gt 2 }
  "[$p] " + (($tx | Select-Object -First 9) -join ' | ')
}
$pw.GetMethod('OpenReport', $I).Invoke($w, @('year')); "reuse same window: " + [object]::ReferenceEquals($rw, $pw.GetField('report', $I).GetValue($w))
$rw.Close(); "after close field null: " + ($null -eq $pw.GetField('report', $I).GetValue($w))
# 2) 悬停不再消耗每日总结，点击才说
$prog.GetField('TestHour', $S).SetValue($null, 19)
$pw.GetMethod('OnEnter', $I).Invoke($w, @($null, $null)); "after hover SummaryDay=[" + $st.SummaryDay + "]"
$pw.GetMethod('OnClick', $I).Invoke($w, $null); "after click SummaryDay=[" + $st.SummaryDay + "] bubble=" + $pw.GetField('bubbleText', $I).GetValue($w).Text
# 3) 会话一览：激活后关闭，不应抛异常
$pw.GetMethod('OpenSessions', $I).Invoke($w, $null)
$sw = $pw.GetField('sessionsWin', $I).GetValue($w)
$sw.Activate() | Out-Null
try { $sw.Close(); "sessions close ok" } catch { "sessions close error: " + $_.Exception.InnerException.Message }
$log = "$Dir\pet.log"; if (Test-Path $log) { "log: " + ((Get-Content $log -Encoding UTF8 | Select-Object -First 1)) } else { "log: none" }
$w.Close()
Remove-Item -Recurse -Force $Dir -EA SilentlyContinue
