#!/usr/bin/env node
// 流萤桌宠构建脚本（只有改源码的人需要；使用插件的人不需要 Node）
//   node build.js   把 src/FireflyPet.cs 编译到 plugins/firefly-pet/bin/FireflyPet.exe
// 用 Windows 自带的 .NET Framework 4 编译器，无需额外依赖。改完源码记得同时提交新的 exe，并升级 plugin.json 的 version。
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const HERE = __dirname;
const OUT = path.join(HERE, 'plugins', 'firefly-pet', 'bin', 'FireflyPet.exe');

const fw = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319');
const csc = path.join(fw, 'csc.exe');
if (!fs.existsSync(csc)) { console.error('✗ 找不到 .NET Framework 4 编译器：' + csc); process.exit(1); }

const wpf = (n) => '-r:' + path.join(fw, 'WPF', n);
const args = ['-nologo', '-target:winexe', '-optimize+', '-codepage:65001', '-out:' + OUT,
  wpf('PresentationFramework.dll'), wpf('PresentationCore.dll'), wpf('WindowsBase.dll'),
  '-r:System.Xaml.dll', '-r:System.Web.Extensions.dll', '-r:System.Drawing.dll', '-r:System.Windows.Forms.dll', '-r:System.Core.dll',
  path.join(HERE, 'src', 'FireflyPet.cs')];
try {
  execFileSync(csc, args, { stdio: 'pipe' });
} catch (e) {
  const msg = Buffer.concat([e.stdout || Buffer.alloc(0), e.stderr || Buffer.alloc(0)]);
  let text;
  try { text = new TextDecoder('gbk').decode(msg); } catch (x) { text = msg.toString(); }
  console.error('✗ 编译失败：\n' + text);
  process.exit(1);
}
console.log('✓ 编译完成：' + path.relative(HERE, OUT));
