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
        public static bool ClaudeDesktopRunning()
        {
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                try
                {
                    string path = ImagePath(p.Id).ToLowerInvariant();
                    if (path.Length > 0 && !path.Contains("\\claude-code\\") && !path.Contains("\\.local\\") && !path.Contains("\\npm\\"))
                        return true;
                }
                finally { p.Dispose(); }
            }
            return false;
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
        public string Spritesheet { get; set; }
        public string PetName { get; set; }

        public Config()
        {
            Scale = 0.7; AutoStart = true; FollowClaude = true; Bubbles = "all"; LookAtCursor = true;
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

        public Stats() { FirstMet = DateTime.Now.ToString("yyyy-MM-dd"); Today = FirstMet; }
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

        public static string Unlock(int lv)
        {
            if (lv == 3) return "解锁：更多悄悄话";
            if (lv == WanderLevel) return "解锁：散步模式（右键开启）";
            if (lv == 6) return "解锁：更多悄悄话";
            if (lv == FirefliesLevel) return "解锁：完成任务时的萤火特效";
            if (lv == 10) return "解锁：更多悄悄话";
            return null;
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
        }

        bool sawClaude;
        int claudeGone;

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
            RenderOptions.SetBitmapScalingMode(sprite, BitmapScalingMode.HighQuality);
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

            Content = root;
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
                menu.Items.Add("演示全部动作", null, (s, e) => { Show(); RunDemo(); });
                menu.Items.Add("-");
                menu.Items.Add("退出", null, (s, e) => Close());
                tray.ContextMenuStrip = menu;
                tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ToggleVisible(); };
            }
            catch (Exception e) { Program.Log("托盘图标失败: " + e.Message); }
        }

        void ToggleVisible()
        {
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

        void CheckStale(DateTime t)
        {
            foreach (var s in sessions.Values)
            {
                double min = (t - s.Last).TotalMinutes;
                if ((s.State == "failed" && min > 0.33) || (s.State == "review" && min > 20) ||
                    ((s.State == "running" || s.State == "waiting") && min > 60))
                    s.State = "idle";
            }
            string today = t.ToString("yyyy-MM-dd");
            if (stats.Today != today) { stats.Today = today; stats.TodayCompletes = 0; stats.TodayPats = 0; statsDirty = true; }
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
            if (!bubbleSticky && DateTime.Now >= bubbleUntil) Say(StatusLine(), 0, 2500, true);
        }

        void OnClick()
        {
            if (BaseState() == "review")
            {
                foreach (var s in sessions.Values) if (s.State == "review") s.State = "idle";
                Overlay("waving", 1);
                Say("嗯，交给你检查啦～", 1, 2500, true);
                return;
            }
            stats.Pats++;
            if (stats.TodayPats < 20) { stats.TodayPats++; GainXp(1, true); }
            statsDirty = true;
            Overlay("jumping", 1);
            Say(Growth.RandomLine(Growth.Level(stats.Xp), rnd), 1, 3000, true);
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
                foreach (var line in text.Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    var d = Json.Parse(line);
                    if (d == null) continue;
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
            Session s;
            if (!sessions.TryGetValue(sid, out s)) { s = new Session { Last = when }; sessions[sid] = s; }
            if (when > s.Last) s.Last = when;
            bool recent = (DateTime.Now - when).TotalMinutes < 60;

            switch (ev)
            {
                case "SessionStart":
                {
                    string src = Json.Str(d, "source");
                    if (src == "startup") stats.Sessions++;
                    if (recent) s.State = "idle";
                    if (live && src != "compact")
                    {
                        Overlay("waving", 2);
                        Say(src == "resume" ? "欢迎回来，我们继续吧。" : src == "clear" ? "好，从头开始～" : Greeting(), 1, 3500);
                    }
                    break;
                }
                case "UserPromptSubmit":
                    stats.Prompts++;
                    if (recent) s.State = "running";
                    GainXp(3, live);
                    if (live) Say("收到～让我想想…", 1, 2500);
                    break;
                case "PreToolUse":
                    if (recent) s.State = "running";
                    if (live) Say(ToolText(tool, detail), 0, 3000);
                    break;
                case "PostToolUse":
                    stats.Tools++;
                    if (recent) s.State = "running";
                    GainXp(1, live);
                    break;
                case "PostToolUseFailure":
                    stats.Tools++;
                    if (recent) s.State = "running";
                    if (live && DateTime.Now > failOverlayCooldown)
                    {
                        failOverlayCooldown = DateTime.Now.AddSeconds(10);
                        Overlay("failed", 1);
                        Say(tool + " 好像出错了…我再想想。", 1, 3000);
                    }
                    break;
                case "PermissionRequest":
                    if (recent) s.State = "waiting";
                    if (live) { Overlay("waving", 1); Say("需要你确认：" + ToolText(tool, detail), 2, 0); }
                    break;
                case "Notification":
                {
                    string nt = Json.Str(d, "ntype");
                    if (nt == "permission_prompt" || nt == "elicitation_dialog")
                    {
                        if (recent) s.State = "waiting";
                        if (live && !bubbleSticky) Say(Json.Str(d, "msg") == "" ? "需要你确认一下～" : Json.Str(d, "msg"), 2, 0);
                    }
                    else if (nt == "idle_prompt")
                    {
                        if (recent && s.State == "idle") s.State = "review";
                    }
                    else if (live) Say(Json.Str(d, "msg"), 1, 3500);
                    break;
                }
                case "SubagentStart":
                    if (recent) s.State = "running";
                    break;
                case "SubagentStop":
                    GainXp(2, live);
                    break;
                case "PreCompact":
                    if (recent) s.State = "running";
                    if (live) Say("整理一下记忆…", 1, 3000);
                    break;
                case "Stop":
                {
                    stats.Completes++;
                    if (when.ToString("yyyy-MM-dd") == DateTime.Now.ToString("yyyy-MM-dd")) stats.TodayCompletes++;
                    if (recent) s.State = "review";
                    if (live)
                    {
                        Overlay("jumping", 1);
                        string[] done = { "完成啦！来看看吧～", "做好了，要检查一下吗？", "搞定～等你验收哦。" };
                        Say(done[rnd.Next(done.Length)] + "  +10 XP", 1, 5000);
                        if (Growth.Level(stats.Xp) >= Growth.FirefliesLevel) Fireflies(7);
                    }
                    GainXp(10, live);
                    break;
                }
                case "StopFailure":
                    stats.Failures++;
                    if (recent) s.State = "failed";
                    if (live)
                    {
                        string err = Json.Str(d, "error");
                        Say("呜…请求失败了" + (err == "" ? "。" : "：" + err), 2, 6000);
                    }
                    break;
                case "SessionEnd":
                    sessions.Remove(sid);
                    if (live) { Overlay("waving", 1); Say("下次见～", 1, 2500); }
                    break;
            }
            statsDirty = true;
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
