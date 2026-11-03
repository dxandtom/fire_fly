# v1.6.0：自定义台词、连戳躲开、上下文体力条、扔光球、接萤火小游戏

# 用法：powershell -NoProfile -STA -File tests\t_play.ps1 [-Exe plugins\firefly-pet\bin\FireflyPet.exe]

# 会在屏幕上短暂显示一只测试用的流萤和几颗光球（临时数据目录，不碰真实数据），结束后关闭

param([string]$Exe = "$PSScriptRoot\..\plugins\firefly-pet\bin\FireflyPet.exe", [string]$Dir = "$env:TEMP\ff-t-play")

[Console]::OutputEncoding = [Text.Encoding]::UTF8

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework

$Exe = (Resolve-Path $Exe).Path

if (Test-Path $Dir) { Remove-Item -Recurse -Force $Dir }

New-Item -ItemType Directory $Dir | Out-Null

$t = (Get-Date).ToString('yyyy-MM-dd')

[IO.File]::WriteAllText("$Dir\stats.json", "{`"Xp`":100,`"Completes`":5,`"FirstMet`":`"2026-10-01`",`"Today`":`"$t`",`"History`":[]}")

[IO.File]::WriteAllText("$Dir\config.json", '{"AutoStart":false,"FollowClaude":false,"AutorunHintShown":true,"SystemNotify":false,"HideInFullscreen":false,"BreakReminder":false,"Festive":false}')

$asm = [Reflection.Assembly]::LoadFrom($Exe)

$S = [Reflection.BindingFlags]'Public,NonPublic,Static'; $BF = [Reflection.BindingFlags]'Public,NonPublic,Instance'

$prog = $asm.GetType('FireflyPet.Program')

$prog.GetField('DataDir', $S).SetValue($null, $Dir)

$prog.GetField('AppDir', $S).SetValue($null, [IO.Path]::GetDirectoryName($Exe) + '\')

$fail = 0

function Check($name, $ok, $info) { if ($ok) { "  ok   $name" } else { "  FAIL $name  $info"; $script:fail++ } }

$pw = $asm.GetType('FireflyPet.PetWindow')



"1) 台词文件解析"

$parse = $pw.GetMethod('ParseLines', $S)

$d = $parse.Invoke($null, @(, [string[]]@('# 注释', '[摸头]', '  你好呀  ', '', '# 也是注释', '[完成]', '搞定！', '[摸头]', '再来', '[空]', ('长' * 100), '没有分组的行不会丢错')))

Check '摸头两句（同名分组合并、去掉首尾空白）' ($d['摸头'].Count -eq 2 -and $d['摸头'][0] -eq '你好呀' -and $d['摸头'][1] -eq '再来') ($d['摸头'] -join '|')

Check '完成一句' ($d['完成'].Count -eq 1) ''

Check '超长台词截到 80 字' ($d['空'][0].Length -eq 80) ''



"2) 上下文用量"

$ctx = $pw.GetMethod('ContextTokens', $S)

$pct = $pw.GetMethod('CtxPercent', $S)

$tp = "$Dir\session.jsonl"

$u = '{"type":"user","message":{"role":"user","content":"hi \"usage\":{\"input_tokens\":999999}"}}'

$a1 = '{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"x"}],"usage":{"input_tokens":3,"cache_creation_input_tokens":1000,"cache_read_input_tokens":40000,"output_tokens":500,"cache_creation":{"ephemeral_5m_input_tokens":1000}}}}'

$side = '{"isSidechain":true,"type":"assistant","message":{"usage":{"input_tokens":5,"cache_read_input_tokens":190000,"output_tokens":1}}}'

$big = '{"type":"user","message":{"content":"' + ('a' * 700000) + '"}}'

[IO.File]::WriteAllText($tp, ($u, $a1, $side, $u) -join "`n")

Check '只取最后一条主线回复，不算子代理、不认聊天内容里的假 usage' (($ctx.Invoke($null, @($tp))) -eq 41503) ($ctx.Invoke($null, @($tp)))

[IO.File]::WriteAllText($tp, ($a1, $big, $a1.Replace('40000', '150000'), $big) -join "`n")

Check '末尾有超长行时只读最后 512KB 也能找到' (($ctx.Invoke($null, @($tp))) -eq 0) '（最后 512KB 里没有回复，应读不到）'

[IO.File]::WriteAllText($tp, ($a1, $big, $a1.Replace('40000', '150000')) -join "`n")

Check '截断后的最后一条回复' (($ctx.Invoke($null, @($tp))) -eq 151503) ($ctx.Invoke($null, @($tp)))

Check '文件不存在返回 0' (($ctx.Invoke($null, @("$Dir\none.jsonl"))) -eq 0) ''

Check '百分比：100K/200K=50%，250K 按 1M 算=25%' (($pct.Invoke($null, @([long]100000))) -eq 50 -and ($pct.Invoke($null, @([long]250000))) -eq 25) ''

$real = Get-ChildItem "$env:USERPROFILE\.claude\projects" -Recurse -Filter *.jsonl -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($real) { $n = $ctx.Invoke($null, @($real.FullName)); Check ("真实会话记录读得出用量（{0} tokens，只读数字）" -f $n) ($n -gt 0) '' }



"3) 桌宠"

$w = [Activator]::CreateInstance($pw, $BF, $null, @($false), $null)

$w.Show()

function F($n) { $pw.GetField($n, $BF).GetValue($w) }

function SetF($n, $v) { $pw.GetField($n, $BF).SetValue($w, $v) }

function M($n, $a) { $pw.GetMethod($n, $BF).Invoke($w, $a) }

function Bubble { (F 'bubbleText').Text }

$st = F 'stats'; $cfg = F 'cfg'

function Ev($ev, $extra) {

  $e = New-Object 'System.Collections.Generic.Dictionary[string,object]'

  $ms = [long]([datetime]::UtcNow - [datetime]'1970-01-01').TotalMilliseconds

  $e['ev'] = $ev; $e['sid'] = 'S1'; $e['cwd'] = 'demo'; $e['ts'] = $ms

  foreach ($k in $extra.Keys) { $e[$k] = $extra[$k] }

  M 'Apply' @($e.PSObject.BaseObject, $ms, $true)

}



"  3a) 自定义台词生效"

[IO.File]::WriteAllText("$Dir\lines.txt", "[摸头]`r`n专属台词ABC`r`n", (New-Object Text.UTF8Encoding $true))

SetF 'linesCheck' ([datetime]::MinValue); M 'CheckLines' $null

$hit = 0; for ($i = 0; $i -lt 40; $i++) { if ((M 'MyLine' @('摸头', '默认')) -eq '专属台词ABC') { $hit++ } }

Check ("一半左右用你写的（40 次里 {0} 次）" -f $hit) ($hit -gt 8 -and $hit -lt 32) ''

Check '没写的分组用自带的' ((M 'MyLine' @('连戳', '默认')) -eq '默认') ''



"  3b) 连戳躲开"

$x0 = $w.Left

for ($i = 0; $i -lt 4; $i++) { M 'OnClick' $null }

Check '点 4 下还不躲' (-not (F 'dodging')) ''

M 'OnClick' $null

Check '第 5 下：嘟囔一句并跑开' ((F 'dodging') -and (F 'walking') -and [math]::Abs((F 'walkTarget') - $x0) -ge 60) (Bubble)

for ($i = 0; $i -lt 200 -and (F 'walking'); $i++) { M 'UpdateWander' @([datetime]::Now, [double]33, 'idle') }

Check '跑到了，停下' ((-not (F 'walking')) -and (-not (F 'dodging')) -and [math]::Abs($w.Left - (F 'walkTarget')) -lt 1) $w.Left

M 'OnClick' $null

Check '躲开后重新计数' (-not (F 'dodging')) ''



"  3c) 体力条"

[IO.File]::WriteAllText($tp, $a1.Replace('40000', '60000'))

Ev 'SessionStart' @{ source = 'startup'; tp = $tp }

Ev 'UserPromptSubmit' @{}

Ev 'Stop' @{}

M 'UpdateCtxChip' $null

$chip = F 'ctxChip'

Check 'Stop 后显示剩余 70%（已用 61503/200K，30%）' ($chip.Visibility -eq 'Visible' -and (F 'ctxText').Text -eq '70%') ((F 'ctxText').Text)

Check '提示里写着已用 61K' ([string]$chip.ToolTip -like '*61K / 200K*') $chip.ToolTip

Check '会话一览也标上下文' ($pw.GetMethod('SessionRows', $BF).Invoke($w, $null)[0][1] -like '*上下文 30%*') ''

[IO.File]::WriteAllText($tp, $a1.Replace('40000', '170000'))

Ev 'UserPromptSubmit' @{}; Ev 'Stop' @{}

Check '用到 85%：排队提醒' ($null -ne (F 'ctxWarnSession')) ''

SetF 'ctxWarnAt' ([datetime]::MinValue); SetF 'bubbleSticky' $false; M 'UpdateCtxChip' $null

Check '提醒说出来了，条变红' ((Bubble) -like '*上下文已经用了 85%*' -and (F 'ctxText').Text -eq '15%') (Bubble)

Ev 'UserPromptSubmit' @{}; Ev 'Stop' @{}

Check '同一会话不重复提醒' ($null -eq (F 'ctxWarnSession')) ''

Ev 'SessionStart' @{ source = 'compact'; tp = $tp }

M 'UpdateCtxChip' $null

Check '压缩后先藏起来，等下次回复再显示' ($chip.Visibility -eq 'Collapsed') ''

$cfg.CtxBar = $false; Ev 'UserPromptSubmit' @{}; Ev 'Stop' @{}; M 'UpdateCtxChip' $null

Check '关掉体力条就不显示' ($chip.Visibility -eq 'Collapsed') ''

$cfg.CtxBar = $true



"  3d) 扔光球"

SetF 'overlay' $null

$xp0 = $st.Xp; $play0 = $st.TodayPlayXp

M 'ThrowBall' $null

$ball = F 'ball'

Check '扔出光球（单独的小窗口）' ((F 'ballOn') -and $ball.IsVisible) ''

$caught = $false

for ($i = 0; $i -lt 900; $i++) {

  M 'UpdateBall' @([double]33); M 'UpdateWander' @([datetime]::Now, [double]33, 'idle')

  if (-not (F 'ballOn')) { $caught = $true; break }

}

Check ("她追过去接住了（模拟 {0:N1} 秒）" -f ($i * 0.033)) ($caught -and $st.Balls -eq 1 -and -not $ball.IsVisible) ''

Check '接住 +1 经验' ($st.TodayPlayXp -eq $play0 + 1 -and $st.Xp -ge $xp0 + 1) $st.TodayPlayXp

M 'ThrowBall' $null

SetF 'ballAge' ([double]30000); M 'UpdateBall' @([double]1)

Check '25 秒没接到就收回' (-not (F 'ballOn')) ''



"  3e) 接萤火小游戏"

M 'StartGame' $null

Check '开局' ((F 'gameOn') -and (Bubble) -like '*接萤火*') ''

SetF 'gameStart' ([double]((F 'clock').Elapsed.TotalMilliseconds - 1000)); SetF 'nextMote' ([double]0)

$motes = $pw.GetField('motes', $BF).GetValue($w)

function SpawnAt($bug) {

  SetF 'nextMote' ([double]0); M 'UpdateGame' @([double]0)

  $m = $motes[$motes.Count - 1]; $mt = $m.GetType()

  $mt.GetField('X').SetValue($m, $w.Left + $w.Width / 2)

  $mt.GetField('Y').SetValue($m, $w.Top + 70 + (F 'spriteH') * 0.5)

  $mt.GetField('Bug').SetValue($m, $bug)

  SetF 'nextMote' ([double]1e12); M 'UpdateGame' @([double]0)

}

SpawnAt $false; SpawnAt $false; SpawnAt $false

Check '接到 3 颗金色 = 3 分' ((F 'gameScore') -eq 3 -and (Bubble) -like '3 分*') (Bubble)

SpawnAt $true

Check '接到 bug -2 = 1 分' ((F 'gameScore') -eq 1 -and (Bubble) -like '*bug*') (Bubble)

SetF 'nextMote' ([double]0); M 'UpdateGame' @([double]0)

Check '没接到的继续往下落' ($motes.Count -ge 1) ''

SetF 'gameStart' ([double]((F 'clock').Elapsed.TotalMilliseconds - 31000)); M 'UpdateGame' @([double]0)

Check '30 秒到：结束，记最高分，萤火全收回' ((-not (F 'gameOn')) -and $st.GameBest -eq 1 -and $motes.Count -eq 0 -and (Bubble) -like '*新纪录*') (Bubble)

$pool = (F 'motePool').Count + (F 'bugPool').Count

Check ("小窗口放回池子复用（{0} 个）" -f $pool) ($pool -ge 1 -and $pool -le 10) ''



"  3f) 经验上限与收尾"

$got = M 'PlayXp' @(50)

Check ("玩耍经验每天最多 10（这次给了 {0}）" -f $got) ($st.TodayPlayXp -eq 10) $st.TodayPlayXp

Check '再玩不加经验' ((M 'PlayXp' @(3)) -eq 0) ''

M 'ThrowBall' $null; M 'StartGame' $null

$w.Hide()

Check '藏起来时光球收回、小游戏结束' ((-not (F 'ballOn')) -and (-not (F 'gameOn'))) ''

$w.Close()

Check '关闭时小窗口都关掉' ($null -eq (F 'ball') -and (F 'motePool').Count -eq 0) ''

Remove-Item -Recurse -Force $Dir

if ($fail -eq 0) { "全部通过" } else { "$fail 项失败"; exit 1 }

