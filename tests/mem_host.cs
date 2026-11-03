// 内存测量宿主：在临时数据目录里加载一只测试桌宠，每 15 秒记一次私有内存，输出线性斜率
// 编译：%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe -out:%TEMP%\mem_host.exe -r:WPF\PresentationFramework.dll
//       -r:WPF\PresentationCore.dll -r:WPF\WindowsBase.dll -r:System.Xaml.dll tests\mem_host.cs（-r 路径都在同一个 Framework64 目录下）
// 用法：mem_host.exe <FireflyPet.exe> <临时数据目录> <秒数> <模式>
//   模式：base 正常显示 | hidden 隐藏 | frozen 所有帧换成同一张（排查帧切换） | talk 每 3 秒换一句气泡 | text 只换文字、气泡常亮 | fade 文字不变、反复淡入淡出 | bubble50 / glow50 气泡或夜光固定半透明 | orbit 三颗子代理萤火 | sparks 每 3 秒飞出 5 颗萤火 | glowon 夜光一直亮着（晚上 9 点、满级） | control 不加载桌宠，只有空窗口和 33ms 空定时器（对照）
//   模式后加 -sw（如 sparks-sw）用软件渲染。
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
        string label = mode;
        if (mode.EndsWith("-sw")) { mode = mode.Substring(0, mode.Length - 3); System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly; }
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
        if (mode == "bubble50" || mode == "glow50")
        {
            // 固定的半透明（不再变化），用来区分"透明度在变"和"有半透明元素、每帧重绘"
            if (mode == "bubble50") w.GetType().GetMethod("Say", I, null, new[] { typeof(string), typeof(int), typeof(int), typeof(bool) }, null).Invoke(w, new object[] { "固定半透明", 1, 0, true });
            // 字段换成一个不在界面上的替身，桌宠每帧的渐变只改替身，界面上的那个就一直停在 0.5
            var fld = w.GetType().GetField(mode == "bubble50" ? "bubbleWrap" : "nightGlow", I);
            var el = (UIElement)fld.GetValue(w);
            fld.SetValue(w, mode == "bubble50" ? (object)new System.Windows.Controls.StackPanel() : new System.Windows.Shapes.Ellipse());
            el.Opacity = 0.5;
        }
        if (mode == "orbit")
        {
            // 三个子代理在跑：身边一直绕着三颗小萤火
            var sess = w.GetType().GetField("sessions", I).GetValue(w);
            var so = Activator.CreateInstance(w.GetType().Assembly.GetType("FireflyPet.Session"), true);
            so.GetType().GetField("Subagents").SetValue(so, 3);
            so.GetType().GetField("Last").SetValue(so, DateTime.Now.AddDays(1));
            sess.GetType().GetProperty("Item").SetValue(sess, so, new object[] { "test" });
        }
        if (mode == "glowon")
        {
            // 走桌宠自己的夜光逻辑：按晚上 9 点、满级判断，夜光一直在呼吸
            w.GetType().Assembly.GetType("FireflyPet.Program").GetField("TestHour", S).SetValue(null, 21);
            var st = w.GetType().GetField("stats", I).GetValue(w);
            st.GetType().GetProperty("Xp").SetValue(st, 100000000L);
        }
        if (mode == "sparks")
        {
            var fire = w.GetType().GetMethod("Fireflies", I);
            var sp = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            sp.Tick += (s, e) => fire.Invoke(w, new object[] { 5 });
            sp.Start();
        }
        if (mode == "talk" || mode == "text" || mode == "fade")
        {
            var say = w.GetType().GetMethod("Say", I, null, new[] { typeof(string), typeof(int), typeof(int), typeof(bool) }, null);
            int k = 0;
            var talk = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            talk.Tick += (s, e) => say.Invoke(w, new object[] { mode == "fade" ? "正在测量内存" : "第 " + (++k) + " 句：正在测量内存", 1, mode == "text" ? 0 : 2500, true });
            talk.Start();
        }
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
        Console.WriteLine("{0}: start={1:F1}MB end={2:F1}MB slope={3:F2}MB/min n={4} cpu={5:F0}ms/10s", label, ys[1], ys[n], num / den * 60, n, Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds / sw.Elapsed.TotalSeconds * 10);
        return 0;
    }
}
