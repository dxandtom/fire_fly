# 数据安全与进程检测：统计文件的原子保存/备份恢复、claude.exe 快照缓存、前台类型判断、同步安装不倒退
# 用法：powershell -NoProfile -STA -File tests\t_safety.ps1 [-Exe plugins\firefly-pet\bin\FireflyPet.exe]
param([string]$Exe = "$PSScriptRoot\..\plugins\firefly-pet\bin\FireflyPet.exe", [string]$Dir = "$env:TEMP\ff-t-safety")
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
if (Test-Path $Dir) { Remove-Item -Recurse -Force $Dir }
New-Item -ItemType Directory $Dir | Out-Null
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$S = [Reflection.BindingFlags]'Public,NonPublic,Static'
$prog = $asm.GetType('FireflyPet.Program'); $prog.GetField('DataDir', $S).SetValue($null, $Dir)
$json = $asm.GetType('FireflyPet.Json'); $statsT = $asm.GetType('FireflyPet.Stats')
$load = $json.GetMethod('Load', $S).MakeGenericMethod($statsT); $save = $json.GetMethod('Save', $S)
$f = "$Dir\stats.json"
$fail = 0
function Check($name, $ok) { if ($ok) { "  ok   $name" } else { "  FAIL $name"; $script:fail++ } }

"1) 保存与备份"
$st = [Activator]::CreateInstance($statsT); $st.Xp = 111; $save.Invoke($null, @($f, $st))
Check '首次保存生成 stats.json' (Test-Path $f)
$st.Xp = 222; $save.Invoke($null, @($f, $st))
Check '再次保存留下 .bak（上一份）' ((Test-Path "$f.bak") -and ($load.Invoke($null, @("$f.bak")).Xp -eq 111))
Check '不残留 .tmp' (-not (Test-Path "$f.tmp"))
Check '读回最新值 222' ($load.Invoke($null, @($f)).Xp -eq 222)

"2) 文件损坏时从 .bak 恢复，坏文件留作 .broken"
[IO.File]::WriteAllText($f, '{"Xp":22')
Check '读到备份的 111' ($load.Invoke($null, @($f)).Xp -eq 111)
Check '生成 .broken' (Test-Path "$f.broken")

"3) 旧版保存在删除和改名之间被打断：只剩 .tmp"
Remove-Item $f, "$f.bak"; [IO.File]::WriteAllText("$f.tmp", '{"Xp":333}')
Check '从 .tmp 读到 333' ($load.Invoke($null, @($f)).Xp -eq 333)

"4) 什么都没有：从空白开始"
Remove-Item "$f.tmp"
Check 'Xp = 0' ($load.Invoke($null, @($f)).Xp -eq 0)

"5) claude.exe 快照 + 路径缓存，结果和系统进程列表一致"
$watch = $asm.GetType('FireflyPet.Watcher'); $dp = $watch.GetMethod('DesktopPids', $S)
$ref = @(Get-Process claude -EA SilentlyContinue | Where-Object { $_.Path -and $_.Path.ToLower() -notmatch '\\claude-code\\|\\\.local\\|\\npm\\' } | ForEach-Object { $_.Id } | Sort-Object)
$got = @($dp.Invoke($null, $null) | Sort-Object)
Check ("桌面端 pid 一致（" + $got.Count + " 个）") ((($ref -join ',')) -eq (($got -join ',')))
$run = $watch.GetMethod('ClaudeDesktopRunning', $S)
Check '桌面端在运行的判断一致' ([bool]$run.Invoke($null, $null) -eq ($ref.Count -gt 0))
$sw = [Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt 200; $i++) { $null = $run.Invoke($null, $null) }
$ms = $sw.Elapsed.TotalMilliseconds / 200
if ($ref.Count -gt 0) { Check ("每 3 秒一次的检查耗时 {0:N3}ms（原来约 10ms）" -f $ms) ($ms -lt 1) } else { "  桌面端没在运行，检查耗时 {0:N2}ms" -f $ms }

"6) 前台类型（只报告，不判对错）"
$desk = $asm.GetType('FireflyPet.Desk')
"  ForegroundKind = " + $desk.GetMethod('ForegroundKind', $S).Invoke($null, $null) + "（1 桌面端 / 2 终端或编辑器 / 0 其他）"

"7) 同步安装不倒退：安装目录里的 exe 更新时，旧会话的 hook 不覆盖它"
$sync = $prog.GetMethod('SyncInstall', $S)
$dst = "$Dir\app\FireflyPet.exe"; New-Item -ItemType Directory "$Dir\app" -Force | Out-Null
[IO.File]::WriteAllText($dst, 'newer'); [IO.File]::SetLastWriteTimeUtc($dst, [datetime]::UtcNow.AddDays(1))
$sync.Invoke($null, $null)
Check '更新的版本保留' ([IO.File]::ReadAllText($dst) -eq 'newer')
[IO.File]::SetLastWriteTimeUtc($dst, [datetime]::UtcNow.AddDays(-400))
$sync.Invoke($null, $null)
Check '更旧的版本被替换' ((Get-Item $dst).Length -eq (Get-Item $Exe).Length)

Remove-Item -Recurse -Force $Dir
if ($fail) { "失败 $fail 项"; exit 1 } else { "全部通过" }
