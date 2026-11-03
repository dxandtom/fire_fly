// 内存测量宿主：在临时数据目录里加载一只测试桌宠，每 15 秒记一次私有内存，输出线性斜率
// 编译：%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe -out:%TEMP%\mem_host.exe -r:WPF\PresentationFramework.dll
//       -r:WPF\PresentationCore.dll -r:WPF\WindowsBase.dll -r:System.Xaml.dll tests\mem_host.cs（-r 路径都在同一个 Framework64 目录下）
// 用法：mem_host.exe <FireflyPet.exe> <临时数据目录> <秒数> <模式>
//   模式：base 正常显示 | hidden 隐藏 | frozen 所有帧换成同一张（排查帧切换） | control 不加载桌宠，只有空窗口和 33ms 空定时器（对照）
//   多组可以同时开多个进程并行测。数据目录会先清空，务必用临时目录。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

static class Host
{
    [STAThread]
    static int Main(string[] a)
    {
        string exe = a[0], dir = a[1], mode = a[3];
        int secs = int.Parse(a[2]);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), "{\"Scale\":0.7,\"HasPos\":true,\"Left\":300,\"Top\":200,\"AutoStart\":false,\"FollowClaude\":false,\"AutorunHintShown\":true,\"SystemNotify\":false,\"HideInFullscreen\":false,\"BreakReminder\":false,\"Bubbles\":\"off\"}");
        var S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Window w;
        if (mode == "control")
        {
            w = new Window { Width = 50, Height = 50 };
            var busy = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            busy.Tick += (s, e) => { };
            busy.Start();
        }
        else
        {
            var asm = Assembly.LoadFrom(exe);
            var prog = asm.GetType("FireflyPet.Program");
            prog.GetField("DataDir", S).SetValue(null, dir);
            prog.GetField("AppDir", S).SetValue(null, Path.GetDirectoryName(exe) + "\\");
            var pw = asm.GetType("FireflyPet.PetWindow");
            w = (Window)Activator.CreateInstance(pw, I, null, new object[] { false }, null);
        }
        w.Show();
        if (mode == "frozen")
        {
            var fr = (Array)w.GetType().GetField("frames", I).GetValue(w);
            object f0 = fr.GetValue(0, 0);
            for (int r = 0; r < fr.GetLength(0); r++) for (int c = 0; c < fr.GetLength(1); c++) fr.SetValue(f0, r, c);
        }
        if (mode == "hidden") w.Hide();
        var xs = new List<double>(); var ys = new List<double>();
        var sw = Stopwatch.StartNew();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        t.Tick += (s, e) =>
        {
            var p = Process.GetCurrentProcess();
            xs.Add(sw.Elapsed.TotalSeconds); ys.Add(p.PrivateMemorySize64 / 1048576.0);
            if (sw.Elapsed.TotalSeconds >= secs) { t.Stop(); w.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        };
        t.Start();
        Dispatcher.Run();
        // 去掉第一个样本（启动期）
        int n = xs.Count - 1; double mx = 0, my = 0;
        for (int i = 1; i <= n; i++) { mx += xs[i]; my += ys[i]; }
        mx /= n; my /= n;
        double num = 0, den = 0;
        for (int i = 1; i <= n; i++) { num += (xs[i] - mx) * (ys[i] - my); den += (xs[i] - mx) * (xs[i] - mx); }
        Console.WriteLine("{0}: start={1:F1}MB end={2:F1}MB slope={3:F2}MB/min n={4}", mode, ys[1], ys[n], num / den * 60, n);
        return 0;
    }
}
