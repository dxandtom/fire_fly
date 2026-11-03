# v1.5.0 新功能：节日日期（含闰月年）、打节拍、测试/编译结果反应、离开打盹与回来迎接、节日祝福与特效、生日
# 用法：powershell -NoProfile -STA -File tests\t_fun.ps1 [-Exe plugins\firefly-pet\bin\FireflyPet.exe]
# 会在屏幕上短暂显示一只测试用的流萤（临时数据目录，不碰真实数据），结束后关闭
param([string]$Exe = "$PSScriptRoot\..\plugins\firefly-pet\bin\FireflyPet.exe", [string]$Dir = "$env:TEMP\ff-t-fun")
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$Exe = (Resolve-Path $Exe).Path
if (Test-Path $Dir) { Remove-Item -Recurse -Force $Dir }
New-Item -ItemType Directory $Dir | Out-Null
$t = (Get-Date).ToString('yyyy-MM-dd')
[IO.File]::WriteAllText("$Dir\stats.json", "{`"Xp`":100,`"Completes`":5,`"FirstMet`":`"2026-10-01`",`"Today`":`"$t`",`"History`":[]}")
[IO.File]::WriteAllText("$Dir\config.json", '{"AutoStart":false,"FollowClaude":false,"AutorunHintShown":true,"SystemNotify":false,"HideInFullscreen":false,"BreakReminder":false}')
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$S = [Reflection.BindingFlags]'Public,NonPublic,Static'; $BF = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$prog = $asm.GetType('FireflyPet.Program')
$prog.GetField('DataDir', $S).SetValue($null, $Dir)
$prog.GetField('AppDir', $S).SetValue($null, [IO.Path]::GetDirectoryName($Exe) + '\')
$fail = 0
function Check($name, $ok, $info) { if ($ok) { "  ok   $name" } else { "  FAIL $name  $info"; $script:fail++ } }

"1) 节日日期"
$find = $asm.GetType('FireflyPet.Festival').GetMethod('Find', $S)
function Fest($date, $bday) { $a = @([datetime]$date, $bday, $null, $null); $null = $find.Invoke($null, $a); return $a[2] }
$cases = @(
  @('2026-02-16', 'fireworks', '除夕'), @('2026-02-17', 'fireworks', '春节'), @('2026-02-19', 'fireworks', '初三'), @('2026-02-20', $null, '初四'),
  @('2026-03-03', 'lanterns', '元宵'), @('2026-06-19', 'leaves', '端午'), @('2026-08-19', 'hearts', '七夕'), @('2026-09-25', 'moon', '中秋'),
  @('2025-08-29', 'hearts', '2025 七夕（当年闰六月）'), @('2025-10-06', 'moon', '2025 中秋（闰六月之后）'), @('2028-10-03', 'moon', '2028 中秋（闰五月之后）'),
  @('2026-12-25', 'snow', '圣诞'), @('2026-10-31', 'spooky', '万圣节'), @('2027-01-01', 'fireworks', '元旦'), @('2026-11-03', $null, '普通日子'))
foreach ($c in $cases) { $k = Fest $c[0] $null; Check ("{0} {1} -> {2}" -f $c[0], $c[2], $c[1]) ($k -eq $c[1]) "实际 $k" }
Check '生日优先' ((Fest '2026-12-25' '12-25') -eq 'birthday') ''

"2) 桌宠"
$pw = $asm.GetType('FireflyPet.PetWindow')
$w = [Activator]::CreateInstance($pw, $BF, $null, @($false), $null)
$w.Show()
function F($n) { $pw.GetField($n, $BF).GetValue($w) }
function SetF($n, $v) { $pw.GetField($n, $BF).SetValue($w, $v) }
function M($n, $a) { $pw.GetMethod($n, $BF).Invoke($w, $a) }
$fx = F 'fx'; $st = F 'stats'; $cfg = F 'cfg'
function Bubble { (F 'bubbleText').Text }

$n0 = $fx.Children.Count; M 'Beat' $null
Check '打节拍：冒出 1 颗光点' ($fx.Children.Count -eq $n0 + 1) ($fx.Children.Count - $n0)
$n0 = $fx.Children.Count; M 'Beat' $null
Check '打节拍：150ms 内不重复' ($fx.Children.Count -eq $n0) ''

$n0 = $fx.Children.Count; M 'ToolResult' @('test', $false)
Check '测试通过：说话 + 撒 14 颗彩色萤火' ((Bubble) -like '*测试全过*' -and $fx.Children.Count -eq $n0 + 14) (Bubble)
$n0 = $fx.Children.Count; M 'ToolResult' @('test', $false)
Check '通过的庆祝 20 秒内不重复' ($fx.Children.Count -eq $n0) ''
M 'ToolResult' @('build', $true)
$q = @(F 'overlays') + @(F 'overlay')
Check '编译失败：着急（failed 动作排进队列）' ((Bubble) -like '*编译没过*' -and @($q | Where-Object { $_.Key -eq 'failed' }).Count -eq 1) (Bubble)

# 走一遍真实事件入口：PostToolUse 带 kind=test
SetF 'passCooldown' ([datetime]::MinValue)
$ev = New-Object 'System.Collections.Generic.Dictionary[string,object]'
$ms = [long]([datetime]::UtcNow - [datetime]'1970-01-01').TotalMilliseconds
$ev['ev'] = 'PostToolUse'; $ev['sid'] = 'S1'; $ev['tool'] = 'Bash'; $ev['kind'] = 'test'; $ev['ts'] = $ms
$av = New-Object object[] 3; $av[0] = $ev.PSObject.BaseObject; $av[1] = $ms; $av[2] = $true; $pw.GetMethod('Apply', $BF).Invoke($w, $av) | Out-Null
Check '事件入口 PostToolUse(kind=test) 触发庆祝' ((Bubble) -like '*测试全过*') (Bubble)

"3) 离开与回来"
M 'UpdateAway' @([double]400)
Check '离开 400 秒 -> 进入打盹' ((F 'away') -eq $true) ''
$n0 = $fx.Children.Count; SetF 'nextAmbient' ([double]0); M 'UpdateAmbient' @('idle')
$z = $fx.Children[$fx.Children.Count - 1]
Check '打盹冒 z' ($fx.Children.Count -eq $n0 + 1 -and $z -is [System.Windows.Controls.TextBlock] -and $z.Text -eq 'z') ''
$st.Completes = $st.Completes + 2
M 'UpdateAway' @([double]1)
Check '回来：说离开多久和完成了几个任务' ((F 'away') -eq $false -and (Bubble) -like '*回来啦*6 分钟*完成了 2 个任务*') (Bubble)
M 'UpdateAway' @([double]100); M 'UpdateAway' @([double]1)
Check '没离开够 5 分钟不打扰' ((F 'away') -eq $false -and (Bubble) -like '*完成了 2 个任务*') (Bubble)

"4) 节日"
$prog.GetField('TestDate', $S).SetValue($null, [Nullable[datetime]][datetime]'2026-12-25')
SetF 'bubbleUntil' ([datetime]::MinValue); SetF 'festDay' $null
$n0 = $fx.Children.Count; M 'CheckFestival' @([double]0)
Check '圣诞：祝福 + 烟花' ((Bubble) -like '*圣诞快乐*' -and $st.FestivalDay -eq '2026-12-25' -and $fx.Children.Count -ge $n0 + 36) (Bubble)
SetF 'bubbleUntil' ([datetime]::MinValue); $b0 = Bubble; M 'CheckFestival' @([double]0)
Check '当天只祝福一次' ((Bubble) -eq $b0) ''
$n0 = $fx.Children.Count; SetF 'nextAmbient' ([double]0); M 'UpdateAmbient' @('running')
Check '圣诞飘雪' ($fx.Children[$fx.Children.Count - 1].Text -eq '❄') ''
$cfg.Festive = $false; SetF 'festDay' $null; M 'CheckFestival' @([double]0)
Check '关掉节日特效后不再识别' ($null -eq (F 'festKind')) ''
$cfg.Festive = $true; $cfg.Birthday = '11-03'
$prog.GetField('TestDate', $S).SetValue($null, [Nullable[datetime]][datetime]'2026-11-03')
SetF 'festDay' $null; SetF 'bubbleUntil' ([datetime]::MinValue); M 'CheckFestival' @([double]0)
Check '生日：祝福' ((Bubble) -like '*生日快乐*' -and (F 'festKind') -eq 'birthday') (Bubble)
SetF 'festDay' $null; $st.FestivalDay = $null; SetF 'bubbleUntil' ([datetime]::MinValue); M 'CheckFestival' @([double]200)
Check '人不在（空闲 200 秒）时先不祝福' ([string]::IsNullOrEmpty($st.FestivalDay)) $st.FestivalDay

"5) 光点会按时清理"
$sp = F 'sparks'
$spT = $pw.GetNestedType('Spark', [Reflection.BindingFlags]'NonPublic'); foreach ($x in @($sp)) { $spT.GetField('Start').SetValue($x, [double]0); $spT.GetField('Dur').SetValue($x, [double]1) }
M 'UpdateSparks' $null
Check '到期的光点全部移除' ((F 'sparks').Count -eq 0) (F 'sparks').Count
$w.Close()
Remove-Item -Recurse -Force $Dir
if ($fail -eq 0) { "全部通过" } else { "$fail 项失败"; exit 1 }
