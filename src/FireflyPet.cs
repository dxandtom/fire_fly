// 流萤 · Claude Code 桌宠
// 透明置顶 WPF 窗口；读取 hook 写入的 ~/.claude/firefly-pet/events.jsonl，
// 按 Codex 宠物同款精灵表（8 列 × 11 行，192×208）播放状态动画，并带经验/等级成长。
// 运行模式：默认为桌宠；--hook 由 Claude Code 插件调用；--watch 为开机自启的守护进程。
// 用 Windows 自带的 .NET Framework csc.exe 编译（C# 5 语法），见 build.js。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace FireflyPet
{
    static class Program
    {
        public static string DataDir;
        public static string AppDir;
        public static string SnapDir;   // 调试用：定时把窗口内容渲染成 PNG
        public static bool HandbookOnStart; // 调试用：启动即打开成长手册（配合 --snap 截图）

        public const string PetMutex = "Local\\FireflyClaudePet";

        [STAThread]
        static int Main(string[] args)
        {
            AppDir = AppDomain.CurrentDomain.BaseDirectory;
            DataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "firefly-pet");
            bool demo = false, watch = false, hook = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--data" && i + 1 < args.Length) DataDir = args[++i];
                else if (args[i] == "--snap" && i + 1 < args.Length) SnapDir = args[++i];
                else if (args[i] == "--demo") demo = true;
                else if (args[i] == "--watch") watch = true;
                else if (args[i] == "--hook") hook = true;
                else if (args[i] == "--handbook") HandbookOnStart = true;
                else if (args[i] == "--focus-claude") return Desk.FocusClaude();
            }
            Directory.CreateDirectory(DataDir);
            if (hook) return Hook.Run();
            if (watch) return Watcher.Run();
            return RunPet(demo);
        }

        // 桌宠和守护进程固定从这里运行（插件目录会随版本变化，开机自启需要一个稳定路径）
        public static string InstalledExe { get { return System.IO.Path.Combine(DataDir, "app", "FireflyPet.exe"); } }

        // 把插件里的 exe 和素材同步到 ~/.claude/firefly-pet/app。
        // 正在运行的 exe 不能覆盖但可以改名，所以先挪成 .old 再复制，旧进程不受影响，下次启动就是新版。
        public static void SyncInstall()
        {
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string dst = InstalledExe, dstDir = System.IO.Path.GetDirectoryName(dst);
            if (string.Equals(System.IO.Path.GetFullPath(self), System.IO.Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(dst) && File.ReadAllBytes(dst).SequenceEqual(File.ReadAllBytes(self))) return;
            Directory.CreateDirectory(System.IO.Path.Combine(dstDir, "assets"));
            foreach (var o in Directory.GetFiles(dstDir, "FireflyPet.exe*.old")) { try { File.Delete(o); } catch { } }
            if (File.Exists(dst)) File.Move(dst, dst + "." + DateTime.Now.Ticks + ".old");
            File.Copy(self, dst);
            string srcAssets = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
            if (Directory.Exists(srcAssets))
                foreach (var f in Directory.GetFiles(srcAssets))
                    File.Copy(f, System.IO.Path.Combine(dstDir, "assets", System.IO.Path.GetFileName(f)), true);
            Log("已同步安装到 " + dstDir);
        }

        public static void LaunchPet()
        {
            if (Watcher.PetRunning()) return;
            string exe = File.Exists(InstalledExe) ? InstalledExe : System.Reflection.Assembly.GetExecutingAssembly().Location;
            Process.Start(new ProcessStartInfo(exe, "--data \"" + DataDir + "\"") { UseShellExecute = false });
        }

        // 单独成方法：守护模式不会因 JIT 编译 Main 而加载 WPF
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int RunPet(bool demo)
        {
            bool created;
            using (var mutex = new Mutex(true, PetMutex, out created))
            {
                if (!created) return 0;
                string pidFile = System.IO.Path.Combine(DataDir, "pet.pid");
                try { File.WriteAllText(pidFile, Process.GetCurrentProcess().Id.ToString()); } catch { }

                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += (s, e) => { Log("未处理异常: " + e.Exception); e.Handled = true; };
                var win = new PetWindow(demo);
                app.Run(win);
                try { File.Delete(pidFile); } catch { }
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        public static void Log(string msg)
        {
            try
            {
                string f = System.IO.Path.Combine(DataDir, "pet.log");
                if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- 跟随 Claude 桌面端

    // 开机自启的守护进程（--watch）：不加载 WPF，每 3 秒看一眼 Claude 桌面端是否在运行，
    // 桌面端从无到有时拉起桌宠；桌宠自己负责在桌面端退出后跟着退出。
    static class Watcher
    {
        public static int Run()
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\FireflyClaudePetWatch", out created))
            {
                if (!created) return 0;
                string cfgFile = System.IO.Path.Combine(Program.DataDir, "config.json");
                bool was = false;
                while (true)
                {
                    bool up = ClaudeDesktopRunning();
                    // 只在"桌面端刚启动"时拉起：用户手动退出桌宠后，不会被立刻拉回来
                    if (up && !was && Json.Load<Config>(cfgFile).FollowClaude)
                    {
                        try { Program.LaunchPet(); }
                        catch (Exception e) { Program.Log("守护进程启动桌宠失败: " + e.Message); }
                    }
                    was = up;
                    Thread.Sleep(3000);
                }
            }
        }

        public static bool PetRunning()
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(Program.PetMutex, out m)) return false;
            m.Dispose();
            return true;
        }

        // Claude 桌面端的主程序也叫 claude.exe；Claude Code CLI（含桌面端内置的那份）同名，按路径排除
        public static bool ClaudeDesktopRunning() { return DesktopPids().Count > 0; }

        public static HashSet<int> DesktopPids()
        {
            var pids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                try
                {
                    string path = ImagePath(p.Id).ToLowerInvariant();
                    if (path.Length > 0 && !path.Contains("\\claude-code\\") && !path.Contains("\\.local\\") && !path.Contains("\\npm\\"))
                        pids.Add(p.Id);
                }
                finally { p.Dispose(); }
            }
            return pids;
        }

        static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }

    // ---------------------------------------------------------------- 窗口工具：跳回 Claude、全屏检测

    static class Desk
    {
        public const int FocusOk = 0, FocusNotFound = 1, FocusFlashed = 2;

        // 把 Claude 桌面端的主窗口切到最前面
        public static int FocusClaude()
        {
            var pids = Watcher.DesktopPids();
            if (pids.Count == 0) return FocusNotFound;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid) || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return FocusNotFound;
            if (IsIconic(found)) ShowWindow(found, SW_RESTORE);
            SetForegroundWindow(found);
            if (GetForegroundWindow() != found)
            {
                // 前台锁：模拟按一下 Alt，再借前台窗口的输入队列切换（常见做法）
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                int dummy;
                int fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out dummy), me = GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
                BringWindowToTop(found);
                SetForegroundWindow(found);
                if (attached) AttachThreadInput(me, fgThread, false);
            }
            if (GetForegroundWindow() == found) return FocusOk;
            var fi = new FLASHWINFO { cbSize = Marshal.SizeOf(typeof(FLASHWINFO)), hwnd = found, dwFlags = 3 | 12 /* 标题+任务栏，直到切到前台 */, uCount = 5 };
            FlashWindowEx(ref fi);
            Program.Log("切换到 Claude 被系统拦下，改为闪烁任务栏");
            return FocusFlashed;
        }

        // 用户多久没碰键盘鼠标了
        public static double IdleSeconds()
        {
            var li = new LASTINPUTINFO { cbSize = Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return 0;
            return unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
        }

        // 前台窗口是否在 self 所在的显示器上全屏（桌面、任务栏本身不算）
        public static bool ForegroundFullscreen(IntPtr self)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == self) return false;
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
            IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            if (mon != MonitorFromWindow(self, MONITOR_DEFAULTTONEAREST)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            RECT r;
            if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(fg, out r)) return false;
            return r.Left <= mi.Monitor.Left && r.Top <= mi.Monitor.Top && r.Right >= mi.Monitor.Right && r.Bottom >= mi.Monitor.Bottom;
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT Monitor, Work; public int Flags; }
        [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public int cbSize; public IntPtr hwnd; public int dwFlags; public int uCount; public int dwTimeout; }
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public int cbSize; public uint dwTime; }
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO fi);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO li);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(int from, int to, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
        const int GW_OWNER = 4, SW_RESTORE = 9, MONITOR_DEFAULTTONEAREST = 2, KEYEVENTF_KEYUP = 2;
        const byte VK_MENU = 0x12;
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, int flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    }

    // ---------------------------------------------------------------- Claude Code hook

    // 插件的 hooks.json 以 `FireflyPet.exe --hook` 调用：Claude Code 把事件 JSON 写到 stdin，
    // 这里精简后追加到 events.jsonl，桌宠自己去读。必须快、必须安静：不向 stdout 输出任何内容
    // （UserPromptSubmit 的 stdout 会进上下文），永远返回 0。
    static class Hook
    {
        public static int Run()
        {
            try { Handle(ReadStdin(1500)); }
            catch (Exception e) { Program.Log("hook 失败: " + e.Message); }
            return 0;
        }

        static string ReadStdin(int timeoutMs)
        {
            var box = new string[1];
            var t = new Thread(() =>
            {
                try { using (var r = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))) box[0] = r.ReadToEnd(); }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
            t.Join(timeoutMs); // stdin 迟迟不关时也别卡住 Claude
            return box[0] ?? "";
        }

        static string Short(object v, int n)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(Convert.ToString(v ?? "", CultureInfo.InvariantCulture), @"\s+", " ").Trim();
            return s.Length > n ? s.Substring(0, n - 1) + "…" : s;
        }

        static string Base(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            p = p.TrimEnd('/', '\\');
            int i = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        // 从工具输入里挑一个最能说明"在干什么"的短字符串
        static string ToolDetail(string tool, Dictionary<string, object> input)
        {
            if (input == null) return "";
            Func<string, string> s = k => Json.Str(input, k);
            switch (tool)
            {
                case "Bash": case "PowerShell": return Short(s("description") != "" ? s("description") : s("command"), 40);
                case "Read": case "Write": case "Edit": case "MultiEdit": case "NotebookEdit":
                    return Base(s("file_path") != "" ? s("file_path") : s("notebook_path"));
                case "Grep": case "Glob": return Short(s("pattern"), 30);
                case "WebFetch": try { return new Uri(s("url")).Host; } catch { return ""; }
                case "WebSearch": return Short(s("query"), 30);
                case "Task": case "Agent": return Short(s("description") != "" ? s("description") : s("subagent_type"), 30);
                default: return Short(s("description") != "" ? s("description") : s("file_path"), 30);
            }
        }

        // 桌宠没在运行时没人取走事件，文件会一直变大；超过上限就只留最近的一段
        const long MaxEventsBytes = 1024 * 1024, KeepEventsBytes = 128 * 1024;

        static void TrimEvents(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (fs.Length <= MaxEventsBytes) return;
                var tail = new byte[KeepEventsBytes];
                fs.Seek(-KeepEventsBytes, SeekOrigin.End);
                int n = 0, r;
                while (n < tail.Length && (r = fs.Read(tail, n, tail.Length - n)) > 0) n += r;
                int start = Array.IndexOf(tail, (byte)'\n', 0, n) + 1; // 从完整的一行开始
                fs.SetLength(0);
                fs.Write(tail, start, n - start);
            }
        }

        static void Handle(string raw)
        {
            var d = Json.Parse(string.IsNullOrEmpty(raw) ? "{}" : raw) ?? new Dictionary<string, object>();
            string ev = Json.Str(d, "hook_event_name");
            if (ev == "") ev = "Unknown";
            var rec = new Dictionary<string, object>();
            rec["ts"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            rec["ev"] = ev;
            string sid = Json.Str(d, "session_id");
            rec["sid"] = sid.Length > 36 ? sid.Substring(0, 36) : sid;
            if (Json.Str(d, "cwd") != "") rec["cwd"] = Base(Json.Str(d, "cwd"));
            string tool = Json.Str(d, "tool_name");
            if (tool != "")
            {
                rec["tool"] = tool;
                object input;
                d.TryGetValue("tool_input", out input);
                string det = ToolDetail(tool, input as Dictionary<string, object>);
                if (det != "") rec["detail"] = det;
            }
            if (Json.Str(d, "notification_type") != "") rec["ntype"] = Json.Str(d, "notification_type");
            if (Json.Str(d, "message") != "") rec["msg"] = Short(Json.Str(d, "message"), 60);
            if (Json.Str(d, "source") != "") rec["source"] = Json.Str(d, "source");
            if (Json.Str(d, "reason") != "") rec["reason"] = Json.Str(d, "reason");
            object err;
            if (d.TryGetValue("error", out err) && err != null)
                rec["error"] = Short(err is string ? (string)err : Json.S.Serialize(err), 60);
            if (Json.Str(d, "agent_type") != "") rec["agent"] = Json.Str(d, "agent_type");

            string line = Json.S.Serialize(rec) + "\n";
            string file = System.IO.Path.Combine(Program.DataDir, "events.jsonl");
            try { if (new FileInfo(file).Length > MaxEventsBytes) TrimEvents(file); }
            catch (Exception e) { if (!(e is FileNotFoundException)) Program.Log("裁剪事件文件失败: " + e.Message); }
            for (int i = 0; i < 10; i++) // 桌宠正把文件改名取走时会短暂占用
            {
                try { File.AppendAllText(file, line, new UTF8Encoding(false)); break; }
                catch (IOException) { Thread.Sleep(30); }
            }

            if (ev == "SessionStart" || ev == "UserPromptSubmit")
            {
                try { Program.SyncInstall(); } catch (Exception e) { Program.Log("同步安装失败: " + e.Message); }
                var cfg = Json.Load<Config>(System.IO.Path.Combine(Program.DataDir, "config.json"));
                if (cfg.AutoStart) { try { Program.LaunchPet(); } catch (Exception e) { Program.Log("启动桌宠失败: " + e.Message); } }
            }
        }
    }

    // ---------------------------------------------------------------- 开机自启

    static class Autorun
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "FireflyClaudePet";

        public static bool Enabled
        {
            get
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key))
                    return k != null && k.GetValue(Name) != null;
            }
        }

        // 由用户在右键菜单里手动开关
        public static void Set(bool on)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
            {
                if (on)
                {
                    string exe = File.Exists(Program.InstalledExe) ? Program.InstalledExe : System.Reflection.Assembly.GetExecutingAssembly().Location;
                    k.SetValue(Name, "\"" + exe + "\" --watch");
                    Process.Start(new ProcessStartInfo(exe, "--watch") { UseShellExecute = false }); // 已在运行的话会自己退出
                }
                else k.DeleteValue(Name, false);
            }
        }
    }

    // ---------------------------------------------------------------- 持久化

    static class Json
    {
        public static readonly JavaScriptSerializer S = new JavaScriptSerializer();

        public static Dictionary<string, object> Parse(string s)
        {
            try { return S.DeserializeObject(s) as Dictionary<string, object>; } catch { return null; }
        }

        public static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return "";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Num(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return 0;
            try { return (long)Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        public static T Load<T>(string file) where T : new()
        {
            try { if (File.Exists(file)) return S.Deserialize<T>(File.ReadAllText(file, Encoding.UTF8)); }
            catch (Exception e) { Program.Log("读取失败 " + file + ": " + e.Message); }
            return new T();
        }

        public static void Save(string file, object o)
        {
            try
            {
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, S.Serialize(o), new UTF8Encoding(false));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch (Exception e) { Program.Log("保存失败 " + file + ": " + e.Message); }
        }
    }

    public class Config
    {
        public double Scale { get; set; }
        public bool HasPos { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public bool AutoStart { get; set; }
        public bool FollowClaude { get; set; }   // 随 Claude 桌面端出现 / 退出
        public bool AutorunHintShown { get; set; }
        public string Bubbles { get; set; }      // all / brief / off
        public bool LookAtCursor { get; set; }
        public bool Wander { get; set; }
        public bool HideInFullscreen { get; set; } // 前台全屏（看视频、演示、游戏）时自动隐藏
        public bool PixelSharp { get; set; }       // 最近邻缩放，像素边缘更锐利
        public bool SystemNotify { get; set; }     // 看不到她（隐藏、全屏、人走开）时弹系统通知
        public string Spritesheet { get; set; }
        public string PetName { get; set; }

        public Config()
        {
            Scale = 0.7; AutoStart = true; FollowClaude = true; Bubbles = "all"; LookAtCursor = true; HideInFullscreen = true; SystemNotify = true;
            Spritesheet = "firefly-spritesheet.png"; PetName = "流萤";
        }
    }

    public class Stats
    {
        public long Xp { get; set; }
        public long Prompts { get; set; }
        public long Tools { get; set; }
        public long Completes { get; set; }
        public long Failures { get; set; }
        public long Pats { get; set; }
        public long Sessions { get; set; }
        public string FirstMet { get; set; }
        public string Today { get; set; }
        public int TodayCompletes { get; set; }
        public int TodayPats { get; set; }
        public int TodayPrompts { get; set; }
        public int TodayTools { get; set; }
        public string LastActiveDay { get; set; }  // 最近一次一起工作的日期
        public int Streak { get; set; }            // 连续一起工作的天数
        public int BestStreak { get; set; }
        public string SummaryDay { get; set; }     // 哪天已经做过每日总结
        public List<string> Achievements { get; set; }

        public Stats() { FirstMet = DateTime.Now.ToString("yyyy-MM-dd"); Today = FirstMet; LastActiveDay = ""; SummaryDay = ""; Achievements = new List<string>(); }

        // 断了一天以上就不算连续了
        public int CurrentStreak()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd"), yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
            return LastActiveDay == today || LastActiveDay == yesterday ? Streak : 0;
        }
    }

    // ---------------------------------------------------------------- 成就

    static class Feats
    {
        public class Def
        {
            public string Id, Name, Desc;
            public Func<Stats, bool> Done; // null 表示由事件直接触发
            public Def(string id, string name, string desc, Func<Stats, bool> done) { Id = id; Name = name; Desc = desc; Done = done; }
        }

        public static readonly List<Def> All = new List<Def>
        {
            new Def("first_task", "初次合作", "一起完成第 1 个任务", x => x.Completes >= 1),
            new Def("tasks_100", "百事通", "累计完成 100 个任务", x => x.Completes >= 100),
            new Def("tasks_1000", "千锤百炼", "累计完成 1000 个任务", x => x.Completes >= 1000),
            new Def("tools_1000", "工具达人", "累计调用工具 1000 次", x => x.Tools >= 1000),
            new Def("day_20", "高效的一天", "一天之内完成 20 个任务", x => x.TodayCompletes >= 20),
            new Def("streak_3", "三日之约", "连续 3 天一起工作", x => x.BestStreak >= 3),
            new Def("streak_7", "一周相伴", "连续 7 天一起工作", x => x.BestStreak >= 7),
            new Def("streak_30", "月光常在", "连续 30 天一起工作", x => x.BestStreak >= 30),
            new Def("pats_100", "摸摸头", "摸头 100 次", x => x.Pats >= 100),
            new Def("night_owl", "夜猫子", "凌晨 0–5 点完成一个任务", null),
            new Def("early_bird", "早起的萤火", "早上 5–8 点完成一个任务", null),
            new Def("marathon", "马拉松", "一个任务连续做了 30 分钟以上", null),
        };
    }

    // ---------------------------------------------------------------- 成长

    static class Growth
    {
        // 第 n 级升到 n+1 级需要的经验
        public static long Need(int level) { return 40 + 20L * (level - 1); }

        public static int Level(long xp, out long into, out long need)
        {
            int lv = 1;
            while (xp >= Need(lv)) { xp -= Need(lv); lv++; }
            into = xp; need = Need(lv);
            return lv;
        }

        public static int Level(long xp) { long a, b; return Level(xp, out a, out b); }

        // 到达第 n 级需要的累计经验
        public static long TotalXp(int level) { return 40L * (level - 1) + 10L * (level - 1) * (level - 2); }

        // 各行为给的经验（事件处理和成长手册共用）
        public const int XpComplete = 10, XpPrompt = 3, XpSubagent = 2, XpTool = 1, XpPat = 1, PatsPerDay = 20;

        static readonly int[] TitleAt = { 1, 3, 6, 10, 15, 20, 30 };
        static readonly string[] Titles = { "初遇的萤火", "小小同行者", "默契搭档", "可靠的伙伴", "萤火守望者", "燃烧的萤火", "永不熄灭的萤火" };

        public static string Title(int lv)
        {
            string t = Titles[0];
            for (int i = 0; i < TitleAt.Length; i++) if (lv >= TitleAt[i]) t = Titles[i];
            return t;
        }

        public const int WanderLevel = 5;
        public const int FirefliesLevel = 8;

        public class Perk
        {
            public int Level; public string Name, Desc; public bool IsTitle;
            public Perk(int lv, string name, string desc, bool title) { Level = lv; Name = name; Desc = desc; IsTitle = title; }
        }

        // 随等级到来的功能与外观；称号另见 Titles。升级气泡、右键菜单、成长手册都从这里取
        static readonly Perk[] Features =
        {
            new Perk(3, "更多悄悄话", "摸头时她会说的话变多。", false),
            new Perk(WanderLevel, "散步模式", "空闲时在屏幕底部左右溜达，有任务时自动停下。需在右键菜单勾选「散步模式」开启。", false),
            new Perk(WanderLevel, "青绿色徽章", "脚下的等级徽章变成青绿色。", false),
            new Perk(6, "更多悄悄话", "又多了一组台词。", false),
            new Perk(FirefliesLevel, "萤火特效", "每次 Claude 完成任务时，身边飘起萤火。自动生效。", false),
            new Perk(10, "更多悄悄话", "最后一组台词。", false),
            new Perk(10, "金色徽章", "等级徽章变成金色。", false),
            new Perk(20, "粉色徽章", "等级徽章变成粉色。", false),
        };

        public static List<Perk> AllPerks()
        {
            var all = new List<Perk>();
            for (int i = 0; i < TitleAt.Length; i++) all.Add(new Perk(TitleAt[i], "称号「" + Titles[i] + "」", "", true));
            all.AddRange(Features);
            return all.OrderBy(p => p.Level).ThenBy(p => p.IsTitle ? 0 : 1).ToList();
        }

        public static List<Perk> PerksAt(int lv) { return AllPerks().Where(p => p.Level == lv).ToList(); }

        // 当前等级之后、最近一个有新东西的等级；没有了返回 0
        public static int NextPerkLevel(int lv)
        {
            var next = AllPerks().Where(p => p.Level > lv).ToList();
            return next.Count == 0 ? 0 : next[0].Level;
        }

        public static string PerkNames(int lv) { return string.Join("、", PerksAt(lv).Select(p => p.Name).ToArray()); }

        // 升级气泡里的解锁提示（称号已在气泡第一行显示，这里只列功能）
        public static string Unlock(int lv)
        {
            var f = Features.Where(p => p.Level == lv).Select(p => p.Level == WanderLevel && p.Name == "散步模式" ? "散步模式（右键开启）" : p.Name).ToArray();
            return f.Length == 0 ? null : "解锁：" + string.Join("、", f);
        }

        public static Color TierColor(int lv)
        {
            if (lv >= 20) return Color.FromRgb(0xFF, 0x9E, 0xD8);
            if (lv >= 10) return Color.FromRgb(0xFF, 0xD5, 0x6A);
            if (lv >= 5) return Color.FromRgb(0x7F, 0xE6, 0xC8);
            return Color.FromRgb(0xDD, 0xE6, 0xE4);
        }

        static readonly string[][] Lines =
        {
            new[] { "嗯？找我有事吗？", "我会一直在这里陪着你的。", "今天也要好好加油哦。", "写代码累了，就休息一下吧。" },
            new[] { "和你一起工作，时间过得好快。", "要不要喝口水？我等你。", "刚才那个改动，很漂亮呢。" },
            new[] { "我们已经是很默契的搭档了吧？", "只要有你在，bug 也没那么可怕。", "嘿嘿…被摸头了。" },
            new[] { "再难的问题，我们也能一起解决。", "我想把今天也好好记住。", "萤火虽小，也能照亮前路。" },
        };

        public static string RandomLine(int lv, Random rnd)
        {
            int tiers = lv >= 10 ? 4 : lv >= 6 ? 3 : lv >= 3 ? 2 : 1;
            var pool = new List<string>();
            for (int i = 0; i < tiers; i++) pool.AddRange(Lines[i]);
            return pool[rnd.Next(pool.Count)];
        }
    }

    // ---------------------------------------------------------------- 成长手册

    // 等级、解锁内容、经验来源的说明窗口。右键菜单 / 托盘菜单 / 点击脚下徽章打开
    public class HandbookWindow : Window
    {
        static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x2B, 0x33, 0x3A));
        static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x7A, 0x86, 0x8C));
        static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x3F, 0xB8, 0x9C));
        static readonly Brush Card = Brushes.White;
        static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xE3, 0xEA, 0xE8));

        readonly Stats stats;
        readonly string petName;
        public readonly StackPanel Body;

        public HandbookWindow(Stats st, string name)
        {
            stats = st; petName = name;
            Title = name + "成长手册";
            Width = 500; Height = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF8, 0xF7));
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Foreground = Ink;
            Body = new StackPanel { Margin = new Thickness(22, 18, 22, 22) };
            Content = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Refresh();
        }

        public void Refresh()
        {
            Body.Children.Clear();
            long into, need;
            int lv = Growth.Level(stats.Xp, out into, out need);

            // 当前进度
            Body.Children.Add(Text(petName + " · Lv." + lv + "「" + Growth.Title(lv) + "」", 18, FontWeights.Bold, Ink));
            Body.Children.Add(Text("本级 " + into + " / " + need + " 经验 · 累计 " + stats.Xp + " 经验", 12, FontWeights.Normal, Muted, 2));
            Body.Children.Add(Bar(into, need));
            int nx = Growth.NextPerkLevel(lv);
            Body.Children.Add(nx == 0
                ? Text("所有内容都已解锁，谢谢你一直陪着她～", 13, FontWeights.Normal, Accent, 8)
                : Text("下一个解锁：Lv." + nx + " —— " + Growth.PerkNames(nx) + "（还差 " + (Growth.TotalXp(nx) - stats.Xp) + " 经验）", 13, FontWeights.SemiBold, Accent, 8));

            // 陪伴与成就
            Body.Children.Add(Heading("陪伴与成就"));
            var ach = new StackPanel();
            var got = stats.Achievements ?? new List<string>();
            ach.Children.Add(Text("连续一起工作 " + stats.CurrentStreak() + " 天 · 最长 " + stats.BestStreak + " 天 · 今天完成 " + stats.TodayCompletes + " 个任务、调用 " + stats.TodayTools + " 次工具", 12, FontWeights.Normal, Ink));
            ach.Children.Add(Text("已解锁 " + Feats.All.Count(x => got.Contains(x.Id)) + " / " + Feats.All.Count + " 个成就", 12, FontWeights.SemiBold, Accent, 4));
            foreach (var def in Feats.All)
                ach.Children.Add(XpRow((got.Contains(def.Id) ? "★ " : "☆ ") + def.Name + " —— " + def.Desc, got.Contains(def.Id) ? "已达成" : "未达成"));
            ach.Children.Add(Text("晚上 6 点后第一次摸头或把鼠标放到她身上，她会说说今天一起做了什么。", 11.5, FontWeights.Normal, Muted, 6));
            Body.Children.Add(CardOf(ach));

            // 经验来源
            Body.Children.Add(Heading("怎么获得经验"));
            var xp = new StackPanel();
            xp.Children.Add(XpRow("Claude 完成一次回复", "+" + Growth.XpComplete));
            xp.Children.Add(XpRow("你发一条消息", "+" + Growth.XpPrompt));
            xp.Children.Add(XpRow("子代理（subagent）完成一次", "+" + Growth.XpSubagent));
            xp.Children.Add(XpRow("工具调用成功一次", "+" + Growth.XpTool));
            xp.Children.Add(XpRow("摸头（点她一下）", "+" + Growth.XpPat + "，每天最多 " + Growth.PatsPerDay + " 次"));
            xp.Children.Add(Text("出错、等你授权、开会话不加经验。桌宠没开时发生的事件会在下次启动时补算。", 11.5, FontWeights.Normal, Muted, 6));
            Body.Children.Add(CardOf(xp));

            // 等级与解锁
            Body.Children.Add(Heading("等级与解锁"));
            foreach (int level in Growth.AllPerks().Select(p => p.Level).Distinct())
                Body.Children.Add(LevelCard(level, lv));

            // 徽章
            Body.Children.Add(Heading("脚下的等级徽章"));
            var bp = new StackPanel();
            bp.Children.Add(Text("就是她脚下写着「Lv.x」的小标签，下面的细条是本级经验进度；点它也能打开这本手册。颜色随等级变化：", 12, FontWeights.Normal, Ink));
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var t in new[] { new KeyValuePair<int, string>(1, "Lv.1–4"), new KeyValuePair<int, string>(5, "Lv.5–9"), new KeyValuePair<int, string>(10, "Lv.10–19"), new KeyValuePair<int, string>(20, "Lv.20+") })
                row.Children.Add(Badge(t.Key, t.Value));
            bp.Children.Add(row);
            Body.Children.Add(CardOf(bp));
        }

        UIElement LevelCard(int level, int cur)
        {
            bool open = cur >= level;
            var sp = new StackPanel();
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
            var lvText = Text("Lv." + level, 15, FontWeights.Bold, open ? Ink : Muted);
            var total = Text("  累计 " + Growth.TotalXp(level) + " 经验", 11.5, FontWeights.Normal, Muted);
            total.VerticalAlignment = VerticalAlignment.Bottom;
            total.Margin = new Thickness(0, 0, 0, 2);
            DockPanel.SetDock(lvText, Dock.Left); DockPanel.SetDock(total, Dock.Left);
            head.Children.Add(lvText); head.Children.Add(total);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2),
                Background = open ? new SolidColorBrush(Color.FromRgb(0xDD, 0xF5, 0xEE)) : new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF2)),
                Child = Text(open ? "✓ 已解锁" : "还差 " + (Growth.TotalXp(level) - stats.Xp) + " 经验", 11.5, FontWeights.SemiBold, open ? Accent : Muted),
            };
            DockPanel.SetDock(chip, Dock.Right);
            head.Children.Add(chip);
            sp.Children.Add(head);
            foreach (var p in Growth.PerksAt(level))
            {
                var name = Text("· " + p.Name, 13, p.IsTitle ? FontWeights.Normal : FontWeights.SemiBold, open ? Ink : Muted, 2);
                sp.Children.Add(name);
                if (p.Desc != "") sp.Children.Add(Text(p.Desc, 11.5, FontWeights.Normal, Muted, 0, 12));
            }
            var c = CardOf(sp);
            if (!open) c.Opacity = 0.85;
            return c;
        }

        static TextBlock Text(string s, double size, FontWeight w, Brush fg, double top = 0, double left = 0)
        {
            return new TextBlock { Text = s, FontSize = size, FontWeight = w, Foreground = fg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left, top, 0, 0) };
        }

        static TextBlock Heading(string s) { return Text(s, 14, FontWeights.Bold, Ink, 20); }

        static Border CardOf(UIElement child)
        {
            return new Border { Child = child, Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 8, 0, 0) };
        }

        static UIElement XpRow(string what, string amount)
        {
            var g = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var a = Text(amount, 13, FontWeights.Bold, Accent);
            DockPanel.SetDock(a, Dock.Right);
            g.Children.Add(a);
            g.Children.Add(Text(what, 13, FontWeights.Normal, Ink));
            return g;
        }

        static UIElement Bar(long into, long need)
        {
            var g = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(into, 0.001), GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(need - into, 0.001), GridUnitType.Star) });
            var bg = new Border { Background = Line, CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(bg, 2);
            g.Children.Add(bg);
            g.Children.Add(new Border { Background = Accent, CornerRadius = new CornerRadius(3) });
            return g;
        }

        // 和桌宠脚下一样的徽章样式
        static UIElement Badge(int lv, string label)
        {
            var col = new SolidColorBrush(Growth.TierColor(lv));
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = col, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new Border { Height = 2.5, Width = 44, Background = col, Margin = new Thickness(0, 1, 0, 1) });
            return new Border { Child = sp, Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1E, 0x26, 0x2C)), CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 0, 8, 0) };
        }
    }

    // ---------------------------------------------------------------- 动画

    class Anim
    {
        public int Row;
        public int[] Dur;
        public Anim(int row, params int[] dur) { Row = row; Dur = dur; }
    }

    class Session
    {
        public string State = "idle";
        public DateTime Last;
        public long StateTs;            // 最近一次改动状态的事件时间（毫秒）；异步 hook 可能晚到，更早的事件不再改状态
        public DateTime? PromptAt;      // 这一轮任务开始的时间，用来算耗时
        public string Project = "";     // 项目文件夹名，多个会话同时进行时标在气泡前
        public bool StuckWarned;        // 这次长时间没动静已经提醒过
    }

    // ---------------------------------------------------------------- 窗口

    public class PetWindow : Window
    {
        const int CellW = 192, CellH = 208, BubbleArea = 70, BadgeArea = 22;

        static readonly Dictionary<string, Anim> Anims = new Dictionary<string, Anim>
        {
            { "idle",          new Anim(0, 900, 320, 140, 160, 320, 900) },
            { "running-right", new Anim(1, 90, 90, 90, 90, 90, 90, 90, 90) },
            { "running-left",  new Anim(2, 90, 90, 90, 90, 90, 90, 90, 90) },
            { "waving",        new Anim(3, 170, 170, 170, 220) },
            { "jumping",       new Anim(4, 90, 110, 160, 110, 130) },
            { "failed",        new Anim(5, 160, 160, 200, 260, 900, 220, 180, 300) },
            { "waiting",       new Anim(6, 220, 220, 220, 220, 220, 220) },
            { "running",       new Anim(7, 130, 130, 130, 130, 130, 130) },
            { "review",        new Anim(8, 260, 260, 260, 260, 260, 260) },
        };

        // 优先级：需要你处理的 > 出错 > 等你验收 > 工作中 > 空闲
        static readonly Dictionary<string, int> Priority = new Dictionary<string, int>
        {
            { "waiting", 5 }, { "failed", 4 }, { "review", 3 }, { "running", 2 }, { "idle", 1 },
        };

        readonly string eventsFile, processingFile, configFile, statsFile;
        readonly Config cfg;
        readonly Stats stats;
        readonly Random rnd = new Random();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly HashSet<string> endedSessions = new HashSet<string>(); // 已结束的会话，晚到的异步事件不再让它复活
        BitmapSource[,] frames;

        // 视图
        Grid root;
        Image sprite;
        Canvas fx;
        Border bubble;
        StackPanel bubbleWrap;
        TextBlock bubbleText;
        Border badge;
        TextBlock badgeText;
        Rectangle badgeBar;
        WF.NotifyIcon tray;
        double scale, spriteW, spriteH;

        // 动画状态
        string curAnim = "";
        int frameIdx;
        double frameElapsed;
        readonly Queue<KeyValuePair<string, int>> overlays = new Queue<KeyValuePair<string, int>>();
        KeyValuePair<string, int>? overlay;
        int lookIdx = -1;
        bool looking;
        Point lastCursor;
        DateTime lastCursorMove = DateTime.MinValue;

        // 交互
        bool pressed, dragging;
        Point pressCursor, pressWin;
        double dragPrevX;
        int dragDir;
        DateTime dragDirAt;
        DateTime hoverCooldown = DateTime.MinValue;
        DateTime failOverlayCooldown = DateTime.MinValue;

        // 散步
        bool walking;
        double walkTarget;
        DateTime nextWander;

        // 气泡
        DateTime bubbleUntil = DateTime.MinValue;
        bool bubbleSticky;

        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastTickMs;
        bool statsDirty;
        DateTime lastStaleCheck = DateTime.MinValue;
        bool demoMode;

        public PetWindow(bool demo)
        {
            demoMode = demo;
            eventsFile = System.IO.Path.Combine(Program.DataDir, "events.jsonl");
            processingFile = System.IO.Path.Combine(Program.DataDir, "events.processing.jsonl");
            configFile = System.IO.Path.Combine(Program.DataDir, "config.json");
            statsFile = System.IO.Path.Combine(Program.DataDir, "stats.json");
            cfg = Json.Load<Config>(configFile);
            stats = Json.Load<Stats>(statsFile);
            if (!File.Exists(configFile)) Json.Save(configFile, cfg);

            Title = cfg.PetName;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            UseLayoutRounding = true;

            LoadSprites();
            BuildView();
            ApplyScale(cfg.Scale, false);
            PlaceInitially();
            BuildTray();

            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };
            Loaded += (s, e) =>
            {
                PollEvents(); // 先吃掉积压事件（只记经验，不演出）
                Overlay("waving", 2);
                if (!cfg.AutorunHintShown)
                {
                    cfg.AutorunHintShown = true; SaveConfig();
                    Say("初次见面～右键我可以开启开机自启哦", 1, 8000, true);
                }
                else Say(Greeting(), 1, 3500);
                nextWander = DateTime.Now.AddSeconds(20 + rnd.Next(30));
                if (demoMode) RunDemo();
                if (Program.SnapDir != null) StartSnapshots();
                if (Program.HandbookOnStart)
                {
                    OpenHandbook();
                    if (Program.SnapDir != null)
                    {
                        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                        t.Tick += (s2, e2) => { t.Stop(); SnapElement(handbook.Body, "handbook.png"); };
                        t.Start();
                    }
                }
            };
            Closed += (s, e) => { SaveStats(); if (tray != null) { tray.Visible = false; tray.Dispose(); } };

            var tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            tick.Tick += (s, e) => Tick();
            tick.Start();
            var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            poll.Tick += (s, e) => PollEvents();
            poll.Start();

            // 见过 Claude 桌面端、之后它连续两次检查都不在了，就跟着退出（只用 CLI 时从没见过桌面端，不受影响）
            var follow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            follow.Tick += (s, e) =>
            {
                if (Watcher.ClaudeDesktopRunning()) { sawClaude = true; claudeGone = 0; }
                else if (sawClaude && cfg.FollowClaude && ++claudeGone >= 2) Close();
            };
            follow.Start();

            var fullscreen = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            fullscreen.Tick += (s, e) => CheckFullscreen();
            fullscreen.Start();
        }

        bool sawClaude;
        int claudeGone;
        bool autoHidden; // 因为全屏被自动藏起来（用户手动隐藏的不算）

        void CheckFullscreen()
        {
            bool full = false;
            try { full = cfg.HideInFullscreen && Desk.ForegroundFullscreen(new WindowInteropHelper(this).Handle); }
            catch (Exception e) { Program.Log("全屏检测失败: " + e.Message); }
            if (full && IsVisible && !dragging) { autoHidden = true; Hide(); }
            else if (!full && autoHidden) { autoHidden = false; Show(); Topmost = false; Topmost = true; }
        }

        // ------------------------------------------------------------ 构建

        void LoadSprites()
        {
            string sheet = cfg.Spritesheet;
            if (!System.IO.Path.IsPathRooted(sheet)) sheet = System.IO.Path.Combine(Program.AppDir, "assets", sheet);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(sheet);
            bmp.EndInit();
            bmp.Freeze();
            int rows = bmp.PixelHeight / CellH, cols = bmp.PixelWidth / CellW;
            frames = new BitmapSource[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var cb = new CroppedBitmap(bmp, new Int32Rect(c * CellW, r * CellH, CellW, CellH));
                    cb.Freeze();
                    frames[r, c] = cb;
                }
        }

        void BuildView()
        {
            root = new Grid();
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition());

            // 气泡
            bubbleText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 36,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x33, 0x3A)),
                TextAlignment = TextAlignment.Center,
            };
            bubble = new Border
            {
                Child = bubbleText,
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0xCF, 0xB8)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 4, 9, 5),
                MaxWidth = 220,
            };
            var tail = new Polygon
            {
                Points = new PointCollection { new Point(0, 0), new Point(12, 0), new Point(6, 7) },
                Fill = bubble.Background,
                Stroke = bubble.BorderBrush,
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -1.5, 0, 0),
            };
            bubbleWrap = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0 };
            bubbleWrap.Children.Add(bubble);
            bubbleWrap.Children.Add(tail);
            bubbleWrap.IsHitTestVisible = false;
            Grid.SetRow(bubbleWrap, 0);
            Panel.SetZIndex(bubbleWrap, 10);
            root.Children.Add(bubbleWrap);

            // 精灵
            var spriteHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            sprite = new Image { Stretch = Stretch.Fill, Cursor = Cursors.Hand };
            ApplyScaling();
            fx = new Canvas { IsHitTestVisible = false };
            spriteHost.Children.Add(sprite);
            spriteHost.Children.Add(fx);
            Grid.SetRow(spriteHost, 1);
            root.Children.Add(spriteHost);

            sprite.MouseLeftButtonDown += OnDown;
            sprite.MouseMove += OnMove;
            sprite.MouseLeftButtonUp += OnUp;
            sprite.MouseEnter += OnEnter;
            sprite.ContextMenuOpening += (s, e) => { sprite.ContextMenu = BuildMenu(); };
            sprite.ContextMenu = BuildMenu();

            // 等级徽章
            badgeText = new TextBlock { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            var barBg = new Grid { Width = 40, Height = 2.5, Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 1, 0, 1) };
            badgeBar = new Rectangle { HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            barBg.Children.Add(badgeBar);
            var badgeStack = new StackPanel();
            badgeStack.Children.Add(badgeText);
            badgeStack.Children.Add(barBg);
            badge = new Border
            {
                Child = badgeStack,
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x1E, 0x26, 0x2C)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(6, 0, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            Grid.SetRow(badge, 2);
            root.Children.Add(badge);
            UpdateBadge();
            badge.IsHitTestVisible = true;
            badge.Cursor = Cursors.Hand;
            badge.ToolTip = "点我打开成长手册";
            badge.MouseLeftButtonUp += (s, e) => { OpenHandbook(); e.Handled = true; };

            Content = root;
        }

        HandbookWindow handbook;

        void ApplyScaling()
        {
            RenderOptions.SetBitmapScalingMode(sprite, cfg.PixelSharp ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        }

        void OpenHandbook()
        {
            if (handbook != null) { handbook.Refresh(); handbook.Activate(); return; }
            handbook = new HandbookWindow(stats, cfg.PetName);
            handbook.Closed += (s, e) => handbook = null;
            handbook.Show();
            handbook.Activate();
        }

        void ApplyScale(double s, bool keepFeet)
        {
            double oldW = Width, oldH = Height;
            scale = s;
            spriteW = Math.Round(CellW * s);
            spriteH = Math.Round(CellH * s);
            double w = Math.Max(spriteW + 40, 236), h = BubbleArea + spriteH + BadgeArea;
            root.RowDefinitions[0].Height = new GridLength(BubbleArea);
            root.RowDefinitions[1].Height = new GridLength(spriteH);
            root.RowDefinitions[2].Height = new GridLength(BadgeArea);
            sprite.Width = spriteW; sprite.Height = spriteH;
            fx.Width = spriteW; fx.Height = spriteH;
            bubbleWrap.Margin = new Thickness(0, 0, 0, -spriteH * 0.06);
            Width = w; Height = h;
            if (keepFeet && !double.IsNaN(oldW))
            {
                Left += (oldW - w) / 2;
                Top += oldH - h;
                SavePos();
            }
        }

        void PlaceInitially()
        {
            bool ok = cfg.HasPos
                && cfg.Left > SystemParameters.VirtualScreenLeft - 50
                && cfg.Top > SystemParameters.VirtualScreenTop - 50
                && cfg.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60
                && cfg.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;
            if (ok) { Left = cfg.Left; Top = cfg.Top; }
            else ResetPosition();
        }

        void ResetPosition()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 24;
            Top = wa.Bottom - Height + 4;
            SavePos();
        }

        void SavePos()
        {
            cfg.HasPos = true; cfg.Left = Left; cfg.Top = Top;
            Json.Save(configFile, cfg);
        }

        void SaveConfig() { Json.Save(configFile, cfg); }

        void BuildTray()
        {
            try
            {
                string sheet = cfg.Spritesheet;
                if (!System.IO.Path.IsPathRooted(sheet)) sheet = System.IO.Path.Combine(Program.AppDir, "assets", sheet);
                SD.Icon icon;
                using (var src = new SD.Bitmap(sheet))
                using (var face = new SD.Bitmap(32, 32))
                {
                    using (var g = SD.Graphics.FromImage(face))
                    {
                        g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new SD.Rectangle(0, 0, 32, 32), new SD.Rectangle(40, 12, 112, 112), SD.GraphicsUnit.Pixel);
                    }
                    icon = SD.Icon.FromHandle(face.GetHicon());
                }
                tray = new WF.NotifyIcon { Icon = icon, Text = cfg.PetName + " · Claude Code", Visible = true };
                var menu = new WF.ContextMenuStrip();
                menu.Items.Add("显示 / 隐藏", null, (s, e) => ToggleVisible());
                menu.Items.Add("回到右下角", null, (s, e) => { Show(); ResetPosition(); });
                menu.Items.Add("成长手册…", null, (s, e) => OpenHandbook());
                menu.Items.Add("演示全部动作", null, (s, e) => { Show(); RunDemo(); });
                menu.Items.Add("-");
                menu.Items.Add("退出", null, (s, e) => Close());
                tray.ContextMenuStrip = menu;
                tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ToggleVisible(); };
                tray.BalloonTipClicked += (s, e) => { try { Desk.FocusClaude(); } catch { } };
            }
            catch (Exception e) { Program.Log("托盘图标失败: " + e.Message); }
        }

        void ToggleVisible()
        {
            autoHidden = false;
            if (IsVisible) Hide(); else { Show(); Topmost = false; Topmost = true; }
        }

        ContextMenu BuildMenu()
        {
            long into, need;
            int lv = Growth.Level(stats.Xp, out into, out need);
            var m = new ContextMenu();
            m.Items.Add(new MenuItem { Header = cfg.PetName + " · Lv." + lv + "「" + Growth.Title(lv) + "」", IsEnabled = false });
            m.Items.Add(new MenuItem { Header = "经验 " + into + " / " + need + "   今日完成 " + stats.TodayCompletes + " 次", IsEnabled = false });
            m.Items.Add(new MenuItem { Header = "累计：对话 " + stats.Prompts + " · 工具 " + stats.Tools + " · 完成 " + stats.Completes, IsEnabled = false });
            int nx = Growth.NextPerkLevel(lv);
            m.Items.Add(new MenuItem { Header = nx == 0 ? "所有内容都已解锁" : "下一个解锁：Lv." + nx + " " + Growth.PerkNames(nx) + "（还差 " + (Growth.TotalXp(nx) - stats.Xp) + " 经验）", IsEnabled = false });
            var book = new MenuItem { Header = "成长手册（等级、解锁、经验怎么来）…", FontWeight = FontWeights.Bold };
            book.Click += (s, e) => OpenHandbook();
            m.Items.Add(book);
            m.Items.Add(new Separator());

            var size = new MenuItem { Header = "大小" };
            foreach (var p in new[] { new KeyValuePair<string, double>("小", 0.5), new KeyValuePair<string, double>("中", 0.7), new KeyValuePair<string, double>("大", 1.0) })
            {
                double v = p.Value;
                var it = new MenuItem { Header = p.Key, IsCheckable = true, IsChecked = Math.Abs(cfg.Scale - v) < 0.01 };
                it.Click += (s, e) => { cfg.Scale = v; ApplyScale(v, true); SaveConfig(); };
                size.Items.Add(it);
            }
            m.Items.Add(size);

            var bub = new MenuItem { Header = "气泡" };
            foreach (var p in new[] { new KeyValuePair<string, string>("全部（含工具动态）", "all"), new KeyValuePair<string, string>("精简（只说重要的）", "brief"), new KeyValuePair<string, string>("关闭", "off") })
            {
                string v = p.Value;
                var it = new MenuItem { Header = p.Key, IsCheckable = true, IsChecked = cfg.Bubbles == v };
                it.Click += (s, e) => { cfg.Bubbles = v; SaveConfig(); };
                bub.Items.Add(it);
            }
            m.Items.Add(bub);

            var look = new MenuItem { Header = "视线跟随鼠标", IsCheckable = true, IsChecked = cfg.LookAtCursor };
            look.Click += (s, e) => { cfg.LookAtCursor = !cfg.LookAtCursor; SaveConfig(); };
            m.Items.Add(look);

            bool canWander = lv >= Growth.WanderLevel;
            var wander = new MenuItem { Header = canWander ? "散步模式" : "散步模式（Lv." + Growth.WanderLevel + " 解锁）", IsCheckable = true, IsChecked = cfg.Wander && canWander, IsEnabled = canWander };
            wander.Click += (s, e) => { cfg.Wander = !cfg.Wander; SaveConfig(); };
            m.Items.Add(wander);

            var full = new MenuItem { Header = "全屏时自动隐藏", IsCheckable = true, IsChecked = cfg.HideInFullscreen };
            full.Click += (s, e) => { cfg.HideInFullscreen = !cfg.HideInFullscreen; SaveConfig(); };
            m.Items.Add(full);

            var sharp = new MenuItem { Header = "像素清晰（边缘更锐利）", IsCheckable = true, IsChecked = cfg.PixelSharp };
            sharp.Click += (s, e) => { cfg.PixelSharp = !cfg.PixelSharp; ApplyScaling(); SaveConfig(); };
            m.Items.Add(sharp);

            var notify = new MenuItem { Header = "系统通知（看不到我时提醒你）", IsCheckable = true, IsChecked = cfg.SystemNotify };
            notify.Click += (s, e) => { cfg.SystemNotify = !cfg.SystemNotify; SaveConfig(); };
            m.Items.Add(notify);

            var auto = new MenuItem { Header = "随 Claude Code 自动出现", IsCheckable = true, IsChecked = cfg.AutoStart };
            auto.Click += (s, e) => { cfg.AutoStart = !cfg.AutoStart; SaveConfig(); };
            m.Items.Add(auto);

            var followItem = new MenuItem { Header = "跟随 Claude 桌面端出现 / 退出", IsCheckable = true, IsChecked = cfg.FollowClaude };
            followItem.Click += (s, e) => { cfg.FollowClaude = !cfg.FollowClaude; SaveConfig(); };
            m.Items.Add(followItem);

            bool autorunOn = false;
            try { autorunOn = Autorun.Enabled; } catch { }
            var autorun = new MenuItem { Header = "开机自启（Claude 桌面端一打开我就出现）", IsCheckable = true, IsChecked = autorunOn };
            autorun.Click += (s, e) =>
            {
                try { Autorun.Set(!autorunOn); Say(!autorunOn ? "好，以后开机我就在后台等你～" : "已关闭开机自启。", 1, 3000, true); }
                catch (Exception ex) { Program.Log("设置开机自启失败: " + ex.Message); Say("设置开机自启失败了…", 1, 3000, true); }
            };
            m.Items.Add(autorun);

            m.Items.Add(new Separator());
            var demo = new MenuItem { Header = "演示全部动作" };
            demo.Click += (s, e) => RunDemo();
            m.Items.Add(demo);
            var reset = new MenuItem { Header = "回到右下角" };
            reset.Click += (s, e) => ResetPosition();
            m.Items.Add(reset);
            var hide = new MenuItem { Header = "隐藏（托盘图标可唤回）" };
            hide.Click += (s, e) => Hide();
            m.Items.Add(hide);
            var quit = new MenuItem { Header = "退出" };
            quit.Click += (s, e) => Close();
            m.Items.Add(quit);
            return m;
        }

        // ------------------------------------------------------------ 主循环

        void Tick()
        {
            double now = clock.Elapsed.TotalMilliseconds;
            double dt = Math.Min(now - lastTickMs, 200);
            lastTickMs = now;
            DateTime t = DateTime.Now;

            if ((t - lastStaleCheck).TotalSeconds >= 1) { lastStaleCheck = t; CheckStale(t); }

            string baseState = BaseState();
            UpdateLook(t, baseState);
            UpdateWander(t, dt, baseState);

            string want;
            if (dragging) want = dragDir > 0 ? "running-right" : dragDir < 0 ? "running-left" : "idle";
            else if (walking) want = walkTarget > Left ? "running-right" : "running-left";
            else if (overlay.HasValue) want = overlay.Value.Key;
            else if (baseState == "idle" && looking) want = "look";
            else want = baseState;

            if (dragging && (t - dragDirAt).TotalMilliseconds > 250) dragDir = 0;

            if (want != curAnim) { curAnim = want; frameIdx = 0; frameElapsed = 0; }
            Advance(dt);

            // 气泡
            if (bubbleSticky && baseState != "waiting") { bubbleSticky = false; bubbleUntil = t; }
            bool showBubble = bubbleSticky || t < bubbleUntil;
            double target = showBubble ? 1 : 0;
            if (Math.Abs(bubbleWrap.Opacity - target) > 0.01)
                bubbleWrap.Opacity += Math.Sign(target - bubbleWrap.Opacity) * Math.Min(Math.Abs(target - bubbleWrap.Opacity), dt / 160);
        }

        void Advance(double dt)
        {
            if (curAnim == "look")
            {
                int r = lookIdx < 8 ? 9 : 10, c = lookIdx % 8;
                SetFrame(r, c);
                return;
            }
            Anim a = Anims[curAnim];
            frameElapsed += dt;
            while (frameElapsed >= a.Dur[frameIdx])
            {
                frameElapsed -= a.Dur[frameIdx];
                frameIdx++;
                if (frameIdx >= a.Dur.Length)
                {
                    if (overlay.HasValue && overlay.Value.Key == curAnim && !dragging && !walking)
                    {
                        int left = overlay.Value.Value - 1;
                        if (left > 0) overlay = new KeyValuePair<string, int>(curAnim, left);
                        else
                        {
                            overlay = overlays.Count > 0 ? overlays.Dequeue() : (KeyValuePair<string, int>?)null;
                            frameIdx = a.Dur.Length - 1; frameElapsed = 0;
                            break;
                        }
                    }
                    frameIdx = 0;
                }
            }
            SetFrame(a.Row, frameIdx);
        }

        BitmapSource shown;
        void SetFrame(int r, int c)
        {
            var f = frames[r, c];
            if (f != shown) { shown = f; sprite.Source = f; }
        }

        void Overlay(string name, int loops)
        {
            var o = new KeyValuePair<string, int>(name, loops);
            if (!overlay.HasValue) overlay = o; else overlays.Enqueue(o);
        }

        string BaseState()
        {
            string best = "idle";
            foreach (var s in sessions.Values)
                if (Priority[s.State] > Priority[best]) best = s.State;
            return best;
        }

        const int StuckMinutes = 10;

        void RollDay()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (stats.Today == today) return;
            stats.Today = today; stats.TodayCompletes = 0; stats.TodayPats = 0; stats.TodayPrompts = 0; stats.TodayTools = 0;
            statsDirty = true;
        }

        // 同时有多个会话在忙时，在气泡前标出项目名
        string ProjectTag(Session s)
        {
            if (string.IsNullOrEmpty(s.Project)) return "";
            int active = sessions.Values.Count(x => (DateTime.Now - x.Last).TotalMinutes < 30);
            return active > 1 ? "[" + s.Project + "] " : "";
        }

        // 看不到她（隐藏、被全屏挡住、人走开了）时，弹一个系统通知
        DateTime lastNotify = DateTime.MinValue;
        void Notify(string title, string text)
        {
            if (!cfg.SystemNotify || tray == null) return;
            if (IsVisible && Desk.IdleSeconds() < 60) return;
            if ((DateTime.Now - lastNotify).TotalSeconds < 5) return; // 授权请求和对应的通知会接连到达，只弹一次
            lastNotify = DateTime.Now;
            try { tray.ShowBalloonTip(6000, cfg.PetName + " · " + title, text, WF.ToolTipIcon.None); }
            catch (Exception e) { Program.Log("系统通知失败: " + e.Message); }
        }

        // 连续一起工作的天数
        void MarkActive(DateTime when)
        {
            string day = when.ToString("yyyy-MM-dd");
            if (string.Compare(day, stats.LastActiveDay ?? "", StringComparison.Ordinal) <= 0) return;
            stats.Streak = stats.LastActiveDay == when.AddDays(-1).ToString("yyyy-MM-dd") ? stats.Streak + 1 : 1;
            stats.BestStreak = Math.Max(stats.BestStreak, stats.Streak);
            stats.LastActiveDay = day;
        }

        void Unlock(string id, bool live)
        {
            if (stats.Achievements == null) stats.Achievements = new List<string>();
            if (stats.Achievements.Contains(id)) return;
            var def = Feats.All.FirstOrDefault(x => x.Id == id);
            if (def == null) return;
            stats.Achievements.Add(id);
            statsDirty = true;
            if (!live) return;
            featQueue.Add(def); // 等当前气泡（比如"完成啦"）说完再宣布
            if (handbook != null && handbook.IsVisible) handbook.Refresh();
        }

        readonly List<Feats.Def> featQueue = new List<Feats.Def>();

        void AnnounceFeats()
        {
            if (featQueue.Count == 0 || bubbleSticky || DateTime.Now < bubbleUntil) return;
            Overlay("jumping", 1);
            Say(featQueue.Count == 1
                ? "解锁成就「" + featQueue[0].Name + "」！" + featQueue[0].Desc + "～"
                : "解锁成就" + string.Concat(featQueue.Select(x => "「" + x.Name + "」").ToArray()) + "！去成长手册看看吧～", 1, 5000, true);
            featQueue.Clear();
        }

        void CheckFeats(bool live)
        {
            foreach (var def in Feats.All)
                if (def.Done != null && def.Done(stats)) Unlock(def.Id, live);
        }

        // 晚上第一次互动时，说说今天一起做了什么
        bool MaybeSummary()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (DateTime.Now.Hour < 18 || stats.SummaryDay == today || stats.TodayCompletes < 1) return false;
            stats.SummaryDay = today; statsDirty = true;
            int streak = stats.CurrentStreak();
            Say("今天一起完成了 " + stats.TodayCompletes + " 个任务、调用了 " + stats.TodayTools + " 次工具" +
                (streak >= 2 ? "，已经连续陪你 " + streak + " 天啦" : "") + "，辛苦啦～", 1, 7000, true);
            return true;
        }

        // Claude Code 的通知原文是英文，常见的几句翻成中文
        static string Zh(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return msg;
            var m = System.Text.RegularExpressions.Regex.Match(msg, @"^Claude needs your permission to use (.+?)\.?$");
            if (m.Success) return "要用 " + m.Groups[1].Value + "，需要你点一下允许～";
            if (msg.StartsWith("Claude is waiting for your input")) return "Claude 在等你回复～";
            if (msg.StartsWith("Claude Code needs your attention") || msg.StartsWith("Claude needs your attention")) return "Claude 需要你看一下～";
            if (msg.StartsWith("Claude Code login successful")) return "登录成功～";
            return msg;
        }

        void CheckStale(DateTime t)
        {
            foreach (var s in sessions.Values)
            {
                double min = (t - s.Last).TotalMinutes;
                if ((s.State == "failed" && min > 0.33) || (s.State == "review" && min > 20) ||
                    ((s.State == "running" || s.State == "waiting") && min > 60))
                    s.State = "idle";
            }
            RollDay();
            AnnounceFeats();
            foreach (var kv in sessions)
            {
                var s = kv.Value;
                if (s.State != "running" || s.StuckWarned || (t - s.Last).TotalMinutes < StuckMinutes) continue;
                s.StuckWarned = true;
                string msg = ProjectTag(s) + "已经 " + StuckMinutes + " 分钟没动静了，是在跑很久的命令，还是卡住了？去看看吧～";
                Overlay("waiting", 1);
                Say(msg, 1, 10000, true);
                Notify("是不是卡住了？", msg);
            }
            if (statsDirty) SaveStats();
        }

        Point CursorDip()
        {
            POINT p;
            GetCursorPos(out p);
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformFromDevice.Transform(new Point(p.X, p.Y));
            return new Point(p.X, p.Y);
        }

        void UpdateLook(DateTime t, string baseState)
        {
            looking = false;
            if (!cfg.LookAtCursor || dragging || walking || baseState != "idle") return;
            Point c = CursorDip();
            if ((c - lastCursor).Length > 2) { lastCursor = c; lastCursorMove = t; }
            double fx0 = Left + Width / 2, fy0 = Top + BubbleArea + spriteH * 0.32;
            double dx = c.X - fx0, dy = c.Y - fy0, dist = Math.Sqrt(dx * dx + dy * dy);
            if ((t - lastCursorMove).TotalSeconds > 3 || dist < spriteW * 0.4 || dist > 1100 || sprite.IsMouseOver) return;
            double ang = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;   // 0=上，90=右，顺时针
            int idx = (int)Math.Round(ang / 22.5) % 16;
            // 轻微滞回，避免在两个方向之间抖动
            if (lookIdx >= 0 && lookIdx != idx)
            {
                double center = lookIdx * 22.5, diff = Math.Abs(((ang - center) + 540) % 360 - 180);
                if (diff < 14) idx = lookIdx;
            }
            lookIdx = idx;
            looking = true;
        }

        void UpdateWander(DateTime t, double dt, string baseState)
        {
            bool allowed = cfg.Wander && Growth.Level(stats.Xp) >= Growth.WanderLevel && !dragging && baseState == "idle" && !overlay.HasValue;
            if (walking)
            {
                if (!allowed) { walking = false; SavePos(); return; }
                double step = 70 * dt / 1000 * Math.Max(scale, 0.6) / 0.7;
                double d = walkTarget - Left;
                if (Math.Abs(d) <= step) { Left = walkTarget; walking = false; SavePos(); nextWander = t.AddSeconds(25 + rnd.Next(40)); }
                else Left += Math.Sign(d) * step;
                return;
            }
            if (!allowed || looking || t < nextWander) return;
            var scr = WF.Screen.FromPoint(new SD.Point((int)(Left + Width / 2), (int)(Top + Height / 2))).WorkingArea;
            var src = PresentationSource.FromVisual(this);
            double k = (src != null && src.CompositionTarget != null) ? src.CompositionTarget.TransformFromDevice.M11 : 1;
            double minX = scr.Left * k - (Width - spriteW) / 2, maxX = scr.Right * k - Width + (Width - spriteW) / 2;
            double dist = (120 + rnd.Next(220)) * (rnd.Next(2) == 0 ? -1 : 1);
            walkTarget = Math.Max(minX, Math.Min(maxX, Left + dist));
            if (Math.Abs(walkTarget - Left) < 30) { nextWander = t.AddSeconds(20); return; }
            walking = true;
        }

        // ------------------------------------------------------------ 鼠标

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            pressed = true; dragging = false;
            pressCursor = CursorDip();
            pressWin = new Point(Left, Top);
            dragPrevX = pressCursor.X;
            sprite.CaptureMouse();
            e.Handled = true;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (!pressed) return;
            Point c = CursorDip();
            if (!dragging && (c - pressCursor).Length > 4) { dragging = true; walking = false; }
            if (!dragging) return;
            Left = pressWin.X + c.X - pressCursor.X;
            Top = pressWin.Y + c.Y - pressCursor.Y;
            if (c.X - dragPrevX > 1.5) { dragDir = 1; dragDirAt = DateTime.Now; }
            else if (c.X - dragPrevX < -1.5) { dragDir = -1; dragDirAt = DateTime.Now; }
            dragPrevX = c.X;
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            pressed = false;
            sprite.ReleaseMouseCapture();
            if (dragging) { dragging = false; dragDir = 0; SavePos(); }
            else OnClick();
            e.Handled = true;
        }

        void OnEnter(object sender, MouseEventArgs e)
        {
            if (pressed || DateTime.Now < hoverCooldown) return;
            hoverCooldown = DateTime.Now.AddSeconds(6);
            string b = BaseState();
            if ((b == "idle" || b == "review") && !overlay.HasValue) Overlay("jumping", 1);
            if (!bubbleSticky && DateTime.Now >= bubbleUntil && !MaybeSummary()) Say(StatusLine(), 0, 2500, true);
        }

        void OnClick()
        {
            string b = BaseState();
            if (b == "review" || b == "waiting")
            {
                int r = Desk.FocusNotFound;
                try { r = Desk.FocusClaude(); } catch (Exception e) { Program.Log("切换到 Claude 失败: " + e.Message); }
                bool jumped = r == Desk.FocusOk;
                if (b == "review") foreach (var s in sessions.Values) if (s.State == "review") s.State = "idle";
                Overlay("waving", 1);
                bubbleSticky = false; // 你已经点过来了，"需要你确认"的常驻气泡可以收起
                if (r == Desk.FocusFlashed) Say("任务栏里闪的那个就是 Claude，点它过去吧～", 1, 4000, true);
                else if (b == "review") Say(jumped ? "带你回去看看～" : "嗯，交给你检查啦～", 1, 2500, true);
                else Say(jumped ? "就在这里，等你确认～" : "Claude 那边在等你确认哦～", 1, 2500, true);
                return;
            }
            stats.Pats++;
            if (stats.TodayPats < Growth.PatsPerDay) { stats.TodayPats++; GainXp(Growth.XpPat, true); }
            statsDirty = true;
            Overlay("jumping", 1);
            if (!MaybeSummary()) Say(Growth.RandomLine(Growth.Level(stats.Xp), rnd), 1, 3000, true);
            CheckFeats(true);
        }

        // ------------------------------------------------------------ 气泡

        void Say(string text, int importance, int ms) { Say(text, importance, ms, false); }

        // importance：0 工具动态，1 一般，2 需要注意；force 为用户主动交互
        void Say(string text, int importance, int ms, bool force)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!force)
            {
                if (cfg.Bubbles == "off") return;
                if (cfg.Bubbles == "brief" && importance < 1) return;
            }
            if (bubbleSticky && ms > 0 && importance < 2) return; // 等待确认的提示不被普通消息覆盖
            bubbleText.Text = text;
            bubbleSticky = ms <= 0;
            bubbleUntil = DateTime.Now.AddMilliseconds(Math.Max(ms, 0));
        }

        string StatusLine()
        {
            long into, need;
            int lv = Growth.Level(stats.Xp, out into, out need);
            return "Lv." + lv + "「" + Growth.Title(lv) + "」 " + into + "/" + need + " XP";
        }

        string Greeting()
        {
            int h = DateTime.Now.Hour;
            if (h < 6) return "这么晚还在努力呀…我陪着你。";
            if (h < 11) return "早上好！今天也一起加油吧～";
            if (h < 14) return "中午好～记得吃饭哦。";
            if (h < 18) return "下午好，我们继续吧！";
            return "晚上好～今天辛苦啦。";
        }

        static string ToolText(string tool, string detail)
        {
            string d = string.IsNullOrEmpty(detail) ? "" : detail;
            switch (tool)
            {
                case "Bash": case "PowerShell": return d == "" ? "执行命令中…" : "执行：" + d;
                case "Read": return "阅读 " + d;
                case "Edit": case "MultiEdit": return "修改 " + d;
                case "Write": return "写入 " + d;
                case "NotebookEdit": return "编辑笔记本 " + d;
                case "Grep": case "Glob": return "搜索 " + d;
                case "WebFetch": return "浏览 " + d;
                case "WebSearch": return "上网查查：" + d;
                case "Task": case "Agent": return "派出小帮手：" + d;
                case "TodoWrite": return "整理一下待办清单～";
            }
            if (tool.StartsWith("mcp__")) { var parts = tool.Split(new[] { "__" }, StringSplitOptions.None); return "调用 " + parts[parts.Length - 1]; }
            return "使用 " + tool + (d == "" ? "" : "：" + d);
        }

        // ------------------------------------------------------------ 事件

        void PollEvents()
        {
            try
            {
                if (!File.Exists(processingFile))
                {
                    if (!File.Exists(eventsFile) || new FileInfo(eventsFile).Length == 0) return;
                    File.Move(eventsFile, processingFile); // hook 之后会自动新建 events.jsonl
                }
                string text = File.ReadAllText(processingFile, Encoding.UTF8);
                File.Delete(processingFile);
                long nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                var batch = new List<Dictionary<string, object>>();
                foreach (var line in text.Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    var d = Json.Parse(line); // 半行（裁剪时留下的）解析失败会得到 null
                    if (d != null) batch.Add(d);
                }
                // 异步 hook 写入顺序不一定等于发生顺序，按事件时间排一下（OrderBy 是稳定排序）
                foreach (var d in batch.OrderBy(x => Json.Num(x, "ts")))
                {
                    long ts = Json.Num(d, "ts");
                    Apply(d, ts, nowMs - ts < 4000);
                }
                SaveStats();
            }
            catch (IOException) { /* 文件正被 hook 占用，下次再读 */ }
            catch (Exception e) { Program.Log("读取事件失败: " + e); }
        }

        void Apply(Dictionary<string, object> d, long ts, bool live)
        {
            string ev = Json.Str(d, "ev"), sid = Json.Str(d, "sid"), tool = Json.Str(d, "tool"), detail = Json.Str(d, "detail");
            DateTime when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ts).ToLocalTime();
            if (ev == "SessionStart") endedSessions.Remove(sid);
            bool ended = endedSessions.Contains(sid);
            Session s;
            if (!sessions.TryGetValue(sid, out s)) { s = new Session { Last = when }; if (!ended) sessions[sid] = s; }
            if (when > s.Last) s.Last = when;
            // 异步 hook 可能晚到：比这个会话已处理过的事件更早的，只记统计和经验，不改状态、不说话
            bool fresh = !ended && ts >= s.StateTs;
            if (fresh) s.StateTs = ts;
            bool recent = fresh && (DateTime.Now - when).TotalMinutes < 60;
            bool talk = live && fresh;
            RollDay();
            bool today = when.ToString("yyyy-MM-dd") == stats.Today;
            if (Json.Str(d, "cwd") != "") s.Project = Json.Str(d, "cwd");
            if (fresh) s.StuckWarned = false;

            switch (ev)
            {
                case "SessionStart":
                {
                    string src = Json.Str(d, "source");
                    if (src == "startup") stats.Sessions++;
                    if (recent) s.State = "idle";
                    if (talk && src != "compact")
                    {
                        Overlay("waving", 2);
                        Say(src == "resume" ? "欢迎回来，我们继续吧。" : src == "clear" ? "好，从头开始～" : Greeting(), 1, 3500);
                    }
                    break;
                }
                case "UserPromptSubmit":
                    stats.Prompts++;
                    if (today) stats.TodayPrompts++;
                    MarkActive(when);
                    if (recent) s.State = "running";
                    if (fresh) s.PromptAt = when;
                    GainXp(Growth.XpPrompt, live);
                    if (talk) Say("收到～让我想想…", 1, 2500);
                    break;
                case "PreToolUse":
                    if (recent) s.State = "running";
                    if (talk) Say(ToolText(tool, detail), 0, 3000);
                    break;
                case "PostToolUse":
                    stats.Tools++;
                    if (today) stats.TodayTools++;
                    if (recent) s.State = "running";
                    GainXp(Growth.XpTool, live);
                    break;
                case "PostToolUseFailure":
                    stats.Tools++;
                    if (today) stats.TodayTools++;
                    if (recent) s.State = "running";
                    if (talk && DateTime.Now > failOverlayCooldown)
                    {
                        failOverlayCooldown = DateTime.Now.AddSeconds(10);
                        Overlay("failed", 1);
                        Say(tool + " 好像出错了…我再想想。", 1, 3000);
                    }
                    break;
                case "PermissionRequest":
                    if (recent) s.State = "waiting";
                    if (talk)
                    {
                        Overlay("waving", 1);
                        Say(ProjectTag(s) + "需要你确认：" + ToolText(tool, detail), 2, 0);
                        Notify("需要你确认", ProjectTag(s) + ToolText(tool, detail));
                    }
                    break;
                case "Notification":
                {
                    string nt = Json.Str(d, "ntype");
                    if (nt == "permission_prompt" || nt == "elicitation_dialog")
                    {
                        if (recent) s.State = "waiting";
                        string text = ProjectTag(s) + (Json.Str(d, "msg") == "" ? "需要你确认一下～" : Zh(Json.Str(d, "msg")));
                        if (talk && !bubbleSticky) Say(text, 2, 0);
                        if (talk) Notify("需要你确认", text);
                    }
                    else if (nt == "idle_prompt")
                    {
                        if (recent && s.State == "idle") s.State = "review";
                    }
                    else if (talk) Say(ProjectTag(s) + Zh(Json.Str(d, "msg")), 1, 3500);
                    break;
                }
                case "SubagentStart":
                    if (recent) s.State = "running";
                    break;
                case "SubagentStop":
                    GainXp(Growth.XpSubagent, live);
                    break;
                case "PreCompact":
                    if (recent) s.State = "running";
                    if (talk) Say("整理一下记忆…", 1, 3000);
                    break;
                case "Stop":
                {
                    stats.Completes++;
                    if (today) stats.TodayCompletes++;
                    MarkActive(when);
                    if (when.Hour < 5) Unlock("night_owl", live);
                    else if (when.Hour >= 5 && when.Hour < 8) Unlock("early_bird", live);
                    if (recent) s.State = "review";
                    string took = "";
                    if (fresh && s.PromptAt.HasValue && when > s.PromptAt.Value)
                    {
                        var span = when - s.PromptAt.Value;
                        if (span.TotalSeconds >= 3) took = "（用了 " + Elapsed(span) + "）";
                        if (span.TotalMinutes >= 30) Unlock("marathon", live);
                    }
                    if (fresh) s.PromptAt = null;
                    if (talk)
                    {
                        Overlay("jumping", 1);
                        string[] done = { "完成啦！来看看吧～", "做好了，要检查一下吗？", "搞定～等你验收哦。" };
                        string doneText = ProjectTag(s) + done[rnd.Next(done.Length)] + took;
                        Say(doneText + "  +" + Growth.XpComplete + " XP", 1, 5000);
                        Notify("任务完成", doneText);
                        if (Growth.Level(stats.Xp) >= Growth.FirefliesLevel) Fireflies(7);
                    }
                    GainXp(Growth.XpComplete, live);
                    break;
                }
                case "StopFailure":
                    stats.Failures++;
                    if (recent) s.State = "failed";
                    if (talk)
                    {
                        string err = Json.Str(d, "error");
                        Say(ProjectTag(s) + "呜…请求失败了" + (err == "" ? "。" : "：" + err), 2, 6000);
                        Notify("请求失败", ProjectTag(s) + (err == "" ? "请求失败了" : err));
                    }
                    break;
                case "SessionEnd":
                    sessions.Remove(sid);
                    endedSessions.Add(sid);
                    if (talk) { Overlay("waving", 1); Say("下次见～", 1, 2500); }
                    break;
            }
            statsDirty = true;
            CheckFeats(live);
        }

        static string Elapsed(TimeSpan t)
        {
            if (t.TotalHours >= 1) return (int)t.TotalHours + " 小时 " + t.Minutes + " 分";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + " 分 " + t.Seconds + " 秒";
            return (int)t.TotalSeconds + " 秒";
        }

        void GainXp(int amount, bool live)
        {
            int before = Growth.Level(stats.Xp);
            stats.Xp += amount;
            int after = Growth.Level(stats.Xp);
            statsDirty = true;
            UpdateBadge();
            if (after > before && live)
            {
                Overlay("jumping", 2);
                Overlay("waving", 2);
                string unlock = Growth.Unlock(after);
                Say("升级啦！Lv." + after + "「" + Growth.Title(after) + "」" + (unlock != null ? "\n" + unlock : ""), 2, 6000);
                Fireflies(12);
            }
        }

        void UpdateBadge()
        {
            long into, need;
            int lv = Growth.Level(stats.Xp, out into, out need);
            var col = Growth.TierColor(lv);
            badgeText.Text = "Lv." + lv;
            badgeText.Foreground = new SolidColorBrush(col);
            badgeBar.Fill = new SolidColorBrush(col);
            badgeBar.Width = 40.0 * into / need;
        }

        void SaveStats()
        {
            if (!statsDirty) return;
            statsDirty = false;
            Json.Save(statsFile, stats);
        }

        // ------------------------------------------------------------ 特效

        static readonly Brush Glow = MakeGlow();
        static Brush MakeGlow()
        {
            var b = new RadialGradientBrush();
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xD8), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, 0xD8, 0xFF, 0x7A), 0.35));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x9A, 0xF5, 0x6A), 1));
            b.Freeze();
            return b;
        }

        void Fireflies(int n)
        {
            for (int i = 0; i < n; i++)
            {
                double size = 9 * Math.Max(scale, 0.6);
                var e = new Ellipse { Width = size, Height = size, Fill = Glow, Opacity = 0 };
                double x0 = spriteW * (0.15 + 0.7 * rnd.NextDouble()), y0 = spriteH * (0.45 + 0.45 * rnd.NextDouble());
                Canvas.SetLeft(e, x0); Canvas.SetTop(e, y0);
                fx.Children.Add(e);
                var dur = TimeSpan.FromMilliseconds(1500 + rnd.Next(900));
                var begin = TimeSpan.FromMilliseconds(i * 110);
                var ay = new DoubleAnimation(y0, y0 - spriteH * (0.45 + 0.35 * rnd.NextDouble()), dur) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                var ax = new DoubleAnimation(x0, x0 + (rnd.NextDouble() - 0.5) * 50 * scale, dur) { BeginTime = begin };
                var ao = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = dur };
                ao.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                ao.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.25)));
                ao.KeyFrames.Add(new LinearDoubleKeyFrame(0.7, KeyTime.FromPercent(0.55)));
                ao.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                var el = e;
                ao.Completed += (s, a) => fx.Children.Remove(el);
                e.BeginAnimation(Canvas.TopProperty, ay);
                e.BeginAnimation(Canvas.LeftProperty, ax);
                e.BeginAnimation(OpacityProperty, ao);
            }
        }

        // ------------------------------------------------------------ 演示

        void RunDemo()
        {
            const string sid = "__demo__";
            var steps = new List<KeyValuePair<int, Action>>();
            Action<string, string, int> st = (state, text, imp) =>
            {
                Session s;
                if (!sessions.TryGetValue(sid, out s)) { s = new Session(); sessions[sid] = s; }
                s.State = state; s.Last = DateTime.Now;
                Say(text, Math.Max(imp, 1), 2600, true);
            };
            steps.Add(new KeyValuePair<int, Action>(0, () => { Overlay("waving", 2); Say("演示开始～这是打招呼", 1, 2600, true); }));
            steps.Add(new KeyValuePair<int, Action>(2800, () => st("running", "running：思考 / 使用工具中", 1)));
            steps.Add(new KeyValuePair<int, Action>(5800, () => st("waiting", "waiting：需要你确认权限", 2)));
            steps.Add(new KeyValuePair<int, Action>(8800, () => { st("running", "工具出错时会这样…", 1); Overlay("failed", 1); }));
            steps.Add(new KeyValuePair<int, Action>(11300, () => { st("review", "review：完成啦，等你验收", 1); Overlay("jumping", 1); Fireflies(8); }));
            steps.Add(new KeyValuePair<int, Action>(14500, () => st("failed", "failed：请求失败", 2)));
            steps.Add(new KeyValuePair<int, Action>(17500, () => { sessions.Remove(sid); Overlay("running-right", 2); Overlay("running-left", 2); Say("拖动时会朝拖动方向跑", 1, 2600, true); }));
            steps.Add(new KeyValuePair<int, Action>(20500, () => { Say("移动鼠标，我会看向你～演示结束", 1, 3000, true); }));
            foreach (var step in steps)
            {
                var a = step.Value;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(step.Key, 1)) };
                timer.Tick += (s, e) => { timer.Stop(); a(); };
                timer.Start();
            }
        }

        void SnapElement(FrameworkElement el, string file)
        {
            Directory.CreateDirectory(Program.SnapDir);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth), (int)Math.Ceiling(el.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var host = new DrawingVisual();
            using (var dc = host.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            }
            rtb.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(System.IO.Path.Combine(Program.SnapDir, file))) enc.Save(f);
        }

        void StartSnapshots()
        {
            Directory.CreateDirectory(Program.SnapDir);
            int n = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            timer.Tick += (s, e) =>
            {
                if (++n > 30) { timer.Stop(); return; }
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var f = File.Create(System.IO.Path.Combine(Program.SnapDir, "snap-" + n.ToString("00") + "-" + curAnim + ".png"))) enc.Save(f);
            };
            timer.Start();
        }

        // ------------------------------------------------------------ Win32

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    }
}
