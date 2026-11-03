# 动画帧：每帧是独立位图（不是 CroppedBitmap，后者会让透明窗口泄漏内存），像素与从精灵表直接裁剪的结果逐字节一致
# 用法：powershell -NoProfile -STA -File tests\t_frames.ps1（只用临时数据目录，只做数值比对）
param([string]$Exe = "$PSScriptRoot\..\plugins\firefly-pet\bin\FireflyPet.exe", [string]$Dir = "$env:TEMP\ff-t-frames")
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
Add-Type -AssemblyName PresentationFramework
if (Test-Path $Dir) { Remove-Item -Recurse -Force $Dir }
New-Item -ItemType Directory $Dir | Out-Null
[IO.File]::WriteAllText("$Dir\config.json", '{"AutoStart":false,"FollowClaude":false,"AutorunHintShown":true,"SystemNotify":false,"BreakReminder":false}')
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$S = [Reflection.BindingFlags]'Public,NonPublic,Static'; $I = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$prog = $asm.GetType('FireflyPet.Program')
$prog.GetField('DataDir', $S).SetValue($null, $Dir)
$prog.GetField('AppDir', $S).SetValue($null, [IO.Path]::GetDirectoryName($Exe) + '\')
$pw = $asm.GetType('FireflyPet.PetWindow')
$w = [Activator]::CreateInstance($pw, $I, $null, @($false), $null)
$fr = $pw.GetField('frames', $I).GetValue($w)
$cfg = $pw.GetField('cfg', $I).GetValue($w)
$sheet = $cfg.Spritesheet; if (-not [IO.Path]::IsPathRooted($sheet)) { $sheet = [IO.Path]::Combine([IO.Path]::GetDirectoryName($Exe), 'assets', $sheet) }
$bmp = New-Object System.Windows.Media.Imaging.BitmapImage
$bmp.BeginInit(); $bmp.CacheOption = 'OnLoad'; $bmp.UriSource = New-Object Uri($sheet); $bmp.EndInit(); $bmp.Freeze()
$cw = 192; $ch = 208; $stride = $cw * 4
$a = New-Object byte[] ($stride * $ch); $b = New-Object byte[] ($stride * $ch)
$bad = 0; $crop = 0; $n = 0
for ($r = 0; $r -lt $fr.GetLength(0); $r++) { for ($c = 0; $c -lt $fr.GetLength(1); $c++) {
  $f = $fr.GetValue($r, $c); $n++
  if ($f -is [System.Windows.Media.Imaging.CroppedBitmap]) { $crop++ }
  $ref = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap((New-Object System.Windows.Media.Imaging.CroppedBitmap($bmp, (New-Object System.Windows.Int32Rect(($c * $cw), ($r * $ch), $cw, $ch)))), [System.Windows.Media.PixelFormats]::Pbgra32, $null, 0)
  $ref.CopyPixels($a, $stride, 0)
  (New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($f, [System.Windows.Media.PixelFormats]::Pbgra32, $null, 0)).CopyPixels($b, $stride, 0)
  if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$a, [byte[]]$b) -or $f.DpiX -ne $bmp.DpiX -or $f.PixelWidth -ne $cw) { $bad++ }
} }
"帧数 $n，仍是 CroppedBitmap 的 $crop，像素/DPI 不一致的 $bad"
$w.Close(); Remove-Item -Recurse -Force $Dir -EA SilentlyContinue
if ($crop -or $bad -or $n -eq 0) { "失败"; exit 1 } else { "全部通过" }
