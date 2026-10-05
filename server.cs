// ระบบสมาชิก ร้านยามุมยาเภสัช — เชื่อมต่อฐานข้อมูล CW Pharma (Pmc)
// อ่านข้อมูลลูกค้า/การขายจาก CW แบบอ่านอย่างเดียว เก็บแต้มที่แลก/ปรับ ไว้ในฐานข้อมูลแยก (YaMumMember)
// คอมไพล์ด้วย build.bat (ใช้ csc ของ .NET Framework 4 ที่มีในเครื่อง)
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// เปลี่ยนเลขเวอร์ชันตรงนี้ทุกครั้งก่อนปล่อยอัปเดต (publish.ps1 อ่านจากบรรทัดนี้)
[assembly: System.Reflection.AssemblyVersion("1.13.1")]
[assembly: System.Reflection.AssemblyTitle("ระบบสมาชิก ร้านยามุมยาเภสัช")]
[assembly: System.Reflection.AssemblyProduct("YaMumMember")]

class ApiError : Exception
{
    public int Status;
    public ApiError(int status, string msg) : base(msg) { Status = status; }
}

static class App
{
    static string Root, WebRoot, MemberCs, MasterCs, MemberDb, Cw;
    static readonly object WriteLock = new object();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    static readonly string[][] DefaultSettings = {
        new[] { "ShopName", "ร้านยามุมยาเภสัช" },
        new[] { "BahtPerPoint", "25" },
        new[] { "PointStartDate", "2026-10-01" },
        new[] { "TierSilver", "3000" },
        new[] { "TierGold", "10000" },
        new[] { "TierPlatinum", "30000" },
        new[] { "BenefitMember", "สะสมแต้มทุกการซื้อ" },
        new[] { "BenefitSilver", "ส่วนลดเวชสำอาง 3%" },
        new[] { "BenefitGold", "ส่วนลดเวชสำอาง 5% + ของขวัญวันเกิด" },
        new[] { "BenefitPlatinum", "ส่วนลด 7% + ของขวัญวันเกิด + ปรึกษาเภสัชกรส่วนตัว" },
        new[] { "IncludeWholesale", "0" },
        new[] { "StaffPin", "" },
        new[] { "AutoUpdate", "1" },
        new[] { "PointSource", "cw" },
        new[] { "CloudUrl", "" },
        new[] { "CloudKey", "" },
        new[] { "LineNotifyPoints", "1" },
        new[] { "LineNotifyEdits", "1" },
        new[] { "LineNotifyBirthday", "1" },
    };

    // โปรแกรมทำงานเบื้องหลัง มีไอคอนที่ถาดระบบ (มุมขวาล่าง) แทนหน้าต่างดำ — ปิดผิดไม่ได้ ปิดจากเมนูไอคอนเท่านั้น
    [STAThread]
    static void Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        System.Windows.Forms.Application.EnableVisualStyles();

        Root = AppDomain.CurrentDomain.BaseDirectory;
        WebRoot = Path.GetFullPath(Path.Combine(Root, "wwwroot"));
        var cfg = LoadConfig(Path.Combine(Root, "config.ini"));
        string server = Cfg(cfg, "SqlServer", @".\SQLEXPRESS");
        string cwDb = Ident(Cfg(cfg, "CwDatabase", "Pmc"));
        MemberDb = Ident(Cfg(cfg, "MemberDatabase", "YaMumMember"));
        Cw = "[" + cwDb + "].dbo.";
        MasterCs = "Data Source=" + server + ";Initial Catalog=master;Integrated Security=True";
        MemberCs = "Data Source=" + server + ";Initial Catalog=" + MemberDb + ";Integrated Security=True";

        string prefix = Cfg(cfg, "Listen", "http://localhost:8088/");
        string url = prefix.Replace("+", "localhost").Replace("*", "localhost");
        var args = Environment.GetCommandLineArgs();
        bool afterUpdate = args.Contains("--after-update"), autostart = args.Contains("--autostart");

        // เปิดเองตอนเปิดเครื่อง: SQL Server อาจยังไม่พร้อม — รอได้สูงสุด 3 นาที
        for (int attempt = 0; ; attempt++)
        {
            try { InitDb(); break; }
            catch (Exception e)
            {
                if (autostart && attempt < 18) { Thread.Sleep(10000); continue; }
                Log("เชื่อมต่อฐานข้อมูลไม่ได้: " + e.Message);
                Fatal("เชื่อมต่อฐานข้อมูลไม่ได้\n\n" + e.Message + "\n\nตรวจสอบว่า SQL Server (" + server + ") ทำงานอยู่ และมีฐานข้อมูล " + cwDb +
                      " ของ CW Pharma\nถ้า SQL Server ชื่ออื่น ให้สร้างไฟล์ config.ini ไว้ข้าง exe (ดูตัวอย่างใน README)");
                return;
            }
        }
        BaseUrl = url;
        UpdateUrl = Cfg(cfg, "UpdateUrl", DefaultUpdateUrl);

        Listener = new HttpListener();
        Listener.Prefixes.Add(prefix);
        // หลังอัปเดต เวอร์ชันเก่าอาจยังปล่อยพอร์ตไม่ทัน — รอได้สูงสุด 20 วินาที
        for (int attempt = 0; ; attempt++)
        {
            try { Listener.Start(); break; }
            catch (HttpListenerException e)
            {
                bool inUse = e.ErrorCode == 183 || e.ErrorCode == 32;
                if (inUse && afterUpdate && attempt < 40)
                {
                    Thread.Sleep(500);
                    Listener = new HttpListener();
                    Listener.Prefixes.Add(prefix);
                    continue;
                }
                if (inUse)
                {
                    // เปิดโปรแกรมไว้อยู่แล้ว (ดูไอคอนมุมขวาล่าง) — แค่เปิดหน้าเว็บให้
                    if (!autostart) OpenBrowser(url);
                    return;
                }
                Log("เปิดพอร์ตไม่ได้ (" + prefix + "): " + e.Message);
                Fatal("เปิดพอร์ตไม่ได้ (" + prefix + ")\n\n" + e.Message + "\n\nถ้าใช้ http://+:port/ ต้องรันแบบ Administrator หรือเพิ่ม urlacl ก่อน");
                return;
            }
        }
        Log("เริ่มทำงาน เวอร์ชัน " + AppVersion + (afterUpdate ? " (อัปเดตแล้ว)" : "") + " · CW: " + server + "/" + cwDb + " · สมาชิก: " + MemberDb + " · " + url);
        if (!afterUpdate && !autostart && !args.Contains("--no-browser")) OpenBrowser(url);

        string publicPrefix = Cfg(cfg, "PublicListen", "http://localhost:8090/");
        StartPublic(publicPrefix);
        // หน้าพนักงานสำหรับใช้นอกร้าน (ผ่าน Cloudflare Tunnel) — ใช้ได้เมื่อตั้งรหัสแอดมินและ PIN พนักงานแล้ว
        StartRelayed(Cfg(cfg, "AdminListen", "http://localhost:8092/"), true);

        CleanupOldExe();
        new Thread(UpdateLoop) { IsBackground = true }.Start();
        new Thread(CloudLoop) { IsBackground = true }.Start();
        new Thread(() =>
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = Listener.GetContext(); }
                catch { if (Restarting) return; Thread.Sleep(1000); continue; }
                ThreadPool.QueueUserWorkItem(o => Handle((HttpListenerContext)o, false), ctx);
            }
        }) { IsBackground = true }.Start();

        RunTray(url, publicPrefix, afterUpdate);
    }

    // ---------- ไอคอนถาดระบบ / เปิดเองตอนเปิดเครื่อง / บันทึกการทำงาน ----------

    static System.Windows.Forms.NotifyIcon Tray;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "YaMumMember";

    static void RunTray(string url, string publicPrefix, bool afterUpdate)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var open = menu.Items.Add("เปิดหน้าพนักงาน", null, (s, e) => OpenBrowser(url));
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add("เปิดหน้าลูกค้า (แท็บเล็ตหน้าร้าน)", null, (s, e) => OpenBrowser(url + "check"));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("เวอร์ชัน " + AppVersion) { Enabled = false });
        if (PublicListener != null)
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("เว็บไซต์ลูกค้า: " + publicPrefix) { Enabled = false });
        var auto = new System.Windows.Forms.ToolStripMenuItem("เปิดเองเมื่อเปิดเครื่อง") { Checked = AutoStartEnabled(), CheckOnClick = true };
        auto.CheckedChanged += (s, e) => SetAutoStart(auto.Checked);
        menu.Items.Add(auto);
        menu.Items.Add("ดูบันทึกการทำงาน (log)", null, (s, e) => { try { Process.Start(LogFile); } catch { } });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("ปิดระบบสมาชิก", null, (s, e) =>
        {
            var ok = System.Windows.Forms.MessageBox.Show(
                "ปิดระบบสมาชิก?\n\nหน้าพนักงาน หน้าลูกค้า และเว็บไซต์ลูกค้าจะใช้ไม่ได้จนกว่าจะเปิดโปรแกรมใหม่",
                "ระบบสมาชิก", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning);
            if (ok != System.Windows.Forms.DialogResult.Yes) return;
            Log("ปิดโปรแกรมจากเมนู");
            ExitApp(0);
        });

        Tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = MakeIcon(), Text = "ระบบสมาชิก ยามุมยาเภสัช " + AppVersion, ContextMenuStrip = menu, Visible = true
        };
        Tray.DoubleClick += (s, e) => OpenBrowser(url);

        // ครั้งแรกที่เปิด ตั้งให้เปิดเองตอนเปิดเครื่องไว้ก่อน (เอาออกได้จากเมนู)
        string marker = Path.Combine(Root, ".autostart-configured");
        if (!File.Exists(marker))
        {
            try { SetAutoStart(true); auto.Checked = true; File.WriteAllText(marker, DateTime.Now.ToString("s")); } catch { }
        }

        Tray.ShowBalloonTip(5000, "ระบบสมาชิกทำงานอยู่",
            afterUpdate ? "อัปเดตเป็นเวอร์ชัน " + AppVersion + " แล้ว"
                        : "โปรแกรมทำงานเบื้องหลัง — คลิกขวาที่ไอคอนนี้ (มุมขวาล่าง) เพื่อเปิดหน้าระบบหรือปิดโปรแกรม",
            System.Windows.Forms.ToolTipIcon.Info);
        System.Windows.Forms.Application.Run();
    }

    static System.Drawing.Icon MakeIcon()
    {
        using (var bmp = new System.Drawing.Bitmap(32, 32))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(15, 122, 95)))
        using (var font = new System.Drawing.Font("Leelawadee UI", 18, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.FillEllipse(bg, 1, 1, 30, 30);
            var fmt = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
            g.DrawString("ย", font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 0, 32, 33), fmt);
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
    }

    static bool AutoStartEnabled()
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunName) != null;
    }

    static void SetAutoStart(bool on)
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(RunName, "\"" + ExePath + "\" --autostart");
            else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
        }
        Log(on ? "ตั้งให้เปิดเองเมื่อเปิดเครื่อง" : "ยกเลิกเปิดเองเมื่อเปิดเครื่อง");
    }

    static void ExitApp(int code)
    {
        try { if (Tray != null) { Tray.Visible = false; Tray.Dispose(); } } catch { }
        Environment.Exit(code);
    }

    static void Fatal(string msg)
    {
        System.Windows.Forms.MessageBox.Show(msg, "ระบบสมาชิก ยามุมยาเภสัช",
            System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
    }

    static string LogFile { get { return Path.Combine(Root, "logs", DateTime.Now.ToString("yyyy-MM") + ".log"); } }
    static readonly object LogLock = new object();

    static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.Combine(Root, "logs"));
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    // ---------- พอร์ตสาธารณะ (สำหรับเว็บไซต์ผ่าน Cloudflare Tunnel) ----------
    // ให้บริการเฉพาะหน้าลูกค้า: แต้ม+ประวัติยา (ต้องใช้ PIN) และค้นหาวิธีใช้ยา — หน้าพนักงานเข้าไม่ได้จากพอร์ตนี้

    static HttpListener PublicListener;

    // HttpListener (HTTP.sys) รับเฉพาะคำขอที่ชื่อเว็บ (Host) ตรงกับที่ลงทะเบียนไว้ คือ localhost
    // แต่ Cloudflare ส่งชื่อโดเมนจริงมา → "400 Invalid Hostname"
    // จึงให้พอร์ตสาธารณะเป็นตัวส่งต่อ (TCP) ที่เปลี่ยน Host เป็น localhost แล้วส่งให้ HttpListener พอร์ตภายใน (พอร์ต+10000)
    static readonly List<System.Net.Sockets.TcpListener> ProxyListeners = new List<System.Net.Sockets.TcpListener>();
    static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    static void StartPublic(string prefix) { StartRelayed(prefix, false); }

    // admin = พอร์ตหน้าพนักงานสำหรับใช้นอกร้าน (ผ่าน Cloudflare Tunnel) ต้องเข้าสู่ระบบด้วยรหัสแอดมินก่อน
    static void StartRelayed(string prefix, bool admin)
    {
        if (prefix.Equals("off", StringComparison.OrdinalIgnoreCase)) return;
        string label = admin ? "หน้าแอดมินออนไลน์" : "หน้าเว็บลูกค้า";
        int port = new Uri(prefix.Replace("+", "localhost").Replace("*", "localhost")).Port;
        int internalPort = port + 10000;
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:" + internalPort + "/");
        try { listener.Start(); }
        catch (HttpListenerException e)
        {
            Log("  เปิดพอร์ต" + label + "ไม่ได้ (ภายใน " + internalPort + "): " + e.Message);
            return;
        }
        if (!admin) PublicListener = listener;
        // หลังอัปเดต เวอร์ชันเก่าอาจยังปล่อยพอร์ตไม่ทัน — ลองซ้ำได้ 20 วินาที
        foreach (var addr in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    var tl = new System.Net.Sockets.TcpListener(addr, port);
                    tl.Start();
                    lock (ProxyListeners) ProxyListeners.Add(tl);
                    new Thread(() => ProxyAcceptLoop(tl, internalPort)) { IsBackground = true }.Start();
                    break;
                }
                catch (Exception e)
                {
                    if (addr.Equals(IPAddress.IPv6Loopback) && !System.Net.Sockets.Socket.OSSupportsIPv6) break;
                    if (attempt == 39) Log("  เปิดพอร์ต" + label + "ไม่ได้ (" + addr + ":" + port + "): " + e.Message);
                    else Thread.Sleep(500);
                }
            }
        }
        Log("  " + label + " (สำหรับ Cloudflare Tunnel): " + prefix);
        new Thread(() =>
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { if (Restarting) return; Thread.Sleep(1000); continue; }
                ThreadPool.QueueUserWorkItem(o => Handle((HttpListenerContext)o, !admin, admin), ctx);
            }
        }) { IsBackground = true }.Start();
    }

    // ---------- หน้าแอดมินออนไลน์: เข้าสู่ระบบด้วยรหัสแอดมิน (เฉพาะพอร์ตที่ผ่าน Tunnel) ----------
    // ที่เครื่องร้าน (localhost:8088) เข้าได้เลยเหมือนเดิม · แก้ไขข้อมูลยังต้องใช้ PIN พนักงานทั้งสองทาง
    // ทำได้เฉพาะที่เครื่องร้าน: ตั้งรหัสแอดมิน, สร้างคีย์ซิงก์ออนไลน์
    static readonly Dictionary<string, DateTime> AdminSessions = new Dictionary<string, DateTime>();
    static readonly TimeSpan AdminSessionLife = TimeSpan.FromHours(12);
    static readonly string[] LocalOnlyRoutes = { "admin/password", "cloud/newkey" };
    const string AdminCookie = "ymadmin";

    static string HashAdminPassword(string pw, string salt)
    {
        using (var kdf = new System.Security.Cryptography.Rfc2898DeriveBytes(pw, Convert.FromBase64String(salt), 100000))
            return Convert.ToBase64String(kdf.GetBytes(32));
    }

    static bool AdminEnabled(Dictionary<string, string> s)
    {
        string v;
        return s.TryGetValue("AdminPasswordHash", out v) && v.Length > 0 && s["StaffPin"].Length > 0;
    }

    static string AdminToken(HttpListenerContext ctx)
    {
        var c = ctx.Request.Cookies[AdminCookie];
        return c == null ? "" : c.Value;
    }

    static bool AdminLoggedIn(HttpListenerContext ctx)
    {
        string t = AdminToken(ctx);
        lock (AdminSessions)
        {
            foreach (var k in AdminSessions.Where(kv => kv.Value < DateTime.Now).Select(kv => kv.Key).ToList()) AdminSessions.Remove(k);
            return t.Length > 0 && AdminSessions.ContainsKey(t);
        }
    }

    static object AdminLogin(HttpListenerContext ctx, Dictionary<string, string> s, Dictionary<string, object> b)
    {
        string ip = ClientIp(ctx);
        if (RecentHits("adminfail:" + ip, TimeSpan.FromMinutes(15), false) >= 8 || RecentHits("adminfail:all", TimeSpan.FromMinutes(15), false) >= 40)
            throw new ApiError(429, "ใส่รหัสผิดหลายครั้ง กรุณารอ 15 นาที");
        if (!AdminEnabled(s)) throw new ApiError(503, "ยังไม่ได้เปิดใช้หน้าแอดมินออนไลน์ (ตั้งรหัสที่เครื่องร้าน)");
        var parts = s["AdminPasswordHash"].Split(':');
        if (parts.Length != 2 || HashAdminPassword(Str(b, "password"), parts[0]) != parts[1])
        {
            RecentHits("adminfail:" + ip, TimeSpan.FromMinutes(15), true);
            RecentHits("adminfail:all", TimeSpan.FromMinutes(15), true);
            Log("เข้าหน้าแอดมินออนไลน์ไม่สำเร็จ จาก " + ip);
            throw new ApiError(403, "รหัสไม่ถูกต้อง");
        }
        string token = NewToken();
        lock (AdminSessions) AdminSessions[token] = DateTime.Now + AdminSessionLife;
        ctx.Response.Headers.Add("Set-Cookie", AdminCookie + "=" + token + "; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=" + (int)AdminSessionLife.TotalSeconds);
        Log("เข้าหน้าแอดมินออนไลน์ จาก " + ip);
        return new Dictionary<string, object> { { "ok", true } };
    }

    static object AdminLogout(HttpListenerContext ctx)
    {
        lock (AdminSessions) AdminSessions.Remove(AdminToken(ctx));
        ctx.Response.Headers.Add("Set-Cookie", AdminCookie + "=; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=0");
        return new Dictionary<string, object> { { "ok", true } };
    }

    // ตั้ง/เปลี่ยน/ปิด รหัสแอดมิน (เฉพาะเครื่องร้าน) — เปลี่ยนแล้วทุกเครื่องที่เข้าอยู่ต้องเข้าใหม่
    static object SetAdminPassword(Dictionary<string, object> b)
    {
        string pw = Str(b, "password");
        if (pw.Length == 0)
            Exec("DELETE FROM dbo.Setting WHERE [Key]='AdminPasswordHash'");
        else
        {
            if (pw.Length < 10) throw new ApiError(400, "รหัสต้องยาวอย่างน้อย 10 ตัวอักษร");
            if (pw.Distinct().Count() < 5) throw new ApiError(400, "รหัสเดาง่ายเกินไป");
            var salt = new byte[16];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(salt);
            string s64 = Convert.ToBase64String(salt);
            Exec("UPDATE dbo.Setting SET [Value]=@v WHERE [Key]='AdminPasswordHash'; IF @@ROWCOUNT=0 INSERT dbo.Setting VALUES('AdminPasswordHash',@v)",
                "@v", s64 + ":" + HashAdminPassword(pw, s64));
        }
        lock (AdminSessions) AdminSessions.Clear();
        Log(pw.Length == 0 ? "ปิดหน้าแอดมินออนไลน์" : "ตั้งรหัสหน้าแอดมินออนไลน์ใหม่");
        return new Dictionary<string, object> { { "ok", true } };
    }

    // ตรวจทุกคำขอ API ที่มาจากหน้าแอดมินออนไลน์
    static void CheckAdminRequest(HttpListenerContext ctx, string route, Dictionary<string, string> s)
    {
        // ต้องมี header นี้ (หน้าเว็บส่งเสมอ) — เว็บอื่นส่ง header แปลก ๆ ข้ามโดเมนไม่ได้ กันการแอบสั่งงาน
        if (ctx.Request.Headers["X-Staff-Pin"] == null) throw new ApiError(400, "คำขอไม่ถูกต้อง");
        if (RecentHits("adminall:" + ClientIp(ctx), TimeSpan.FromMinutes(1), true) > 300) throw new ApiError(429, "ใช้งานถี่เกินไป กรุณารอสักครู่");
        if (route == "admin/login" || route == "admin/me" || route == "version" || route == "mode") return;
        if (!AdminEnabled(s)) throw new AdminLoginRequired("ยังไม่ได้เปิดใช้หน้าแอดมินออนไลน์ (ตั้งรหัสที่เครื่องร้าน)");
        if (!AdminLoggedIn(ctx)) throw new AdminLoginRequired("กรุณาเข้าสู่ระบบ");
        if (LocalOnlyRoutes.Contains(route)) throw new ApiError(403, "ทำได้เฉพาะที่เครื่องร้าน");
        // กันการสุ่ม PIN พนักงานจากนอกร้าน
        if (RecentHits("pinfail:" + ClientIp(ctx), TimeSpan.FromMinutes(15), false) >= 10) throw new ApiError(429, "ใส่ PIN ผิดหลายครั้ง กรุณารอ 15 นาที");
    }

    class AdminLoginRequired : ApiError { public AdminLoginRequired(string msg) : base(401, msg) { } }

    static void ProxyAcceptLoop(System.Net.Sockets.TcpListener tl, int internalPort)
    {
        while (true)
        {
            System.Net.Sockets.TcpClient c;
            try { c = tl.AcceptTcpClient(); }
            catch { if (Restarting) return; Thread.Sleep(500); continue; }
            ThreadPool.QueueUserWorkItem(o => ProxyOne((System.Net.Sockets.TcpClient)o, internalPort), c);
        }
    }

    // ส่งต่อคำขอเดียว: อ่าน header, เปลี่ยน Host เป็น localhost, บังคับ Connection: close แล้วส่งต่อทั้งสองทาง
    static void ProxyOne(System.Net.Sockets.TcpClient client, int internalPort)
    {
        try
        {
            using (client)
            using (var upstream = new System.Net.Sockets.TcpClient())
            {
                client.ReceiveTimeout = 30000;
                var cs = client.GetStream();
                var buf = new MemoryStream();
                var chunk = new byte[8192];
                int headerEnd = -1;
                while (headerEnd < 0)
                {
                    int n = cs.Read(chunk, 0, chunk.Length);
                    if (n <= 0) return;
                    buf.Write(chunk, 0, n);
                    if (buf.Length > 65536) return; // header ใหญ่ผิดปกติ
                    headerEnd = Latin1.GetString(buf.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                }
                byte[] all = buf.ToArray();
                var lines = Latin1.GetString(all, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
                var head = new StringBuilder(lines[0]).Append("\r\n");
                for (int i = 1; i < lines.Length; i++)
                {
                    string name = lines[i].Split(':')[0].Trim().ToLowerInvariant();
                    if (name == "host" || name == "connection" || name == "keep-alive" || name == "proxy-connection") continue;
                    head.Append(lines[i]).Append("\r\n");
                }
                head.Append("Host: localhost:").Append(internalPort).Append("\r\nConnection: close\r\n\r\n");

                upstream.Connect(IPAddress.Loopback, internalPort);
                var us = upstream.GetStream();
                byte[] h = Latin1.GetBytes(head.ToString());
                us.Write(h, 0, h.Length);
                us.Write(all, headerEnd + 4, all.Length - headerEnd - 4); // ส่วน body ที่อ่านมาแล้ว
                var up = new Thread(() => { try { cs.CopyTo(us); } catch { } }) { IsBackground = true };
                up.Start();
                us.CopyTo(cs);
            }
        }
        catch { }
    }

    static readonly string[] PublicRoutes = { "mode", "settings", "my/login", "my/profile", "my/pin", "my/logout", "my/edit" };
    static bool IsPublicRoute(string route) { return PublicRoutes.Contains(route) || route.StartsWith("my/drug/"); }
    static bool IsPinRoute(string route) { return route == "my/login" || route == "my/pin"; }

    // จำกัดจำนวนครั้งต่อ IP กันการสุ่มเบอร์/PIN จากอินเทอร์เน็ต
    static readonly Dictionary<string, List<DateTime>> RateHits = new Dictionary<string, List<DateTime>>();

    static int RecentHits(string key, TimeSpan window, bool add)
    {
        lock (RateHits)
        {
            List<DateTime> list;
            if (!RateHits.TryGetValue(key, out list)) RateHits[key] = list = new List<DateTime>();
            var cutoff = DateTime.Now - window;
            list.RemoveAll(t => t < cutoff);
            if (add) list.Add(DateTime.Now);
            if (RateHits.Count > 20000) // กันหน่วยความจำโตไม่หยุด
                foreach (var k in RateHits.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) RateHits.Remove(k);
            return list.Count;
        }
    }

    static string ClientIp(HttpListenerContext ctx)
    {
        // มาจาก Cloudflare Tunnel เท่านั้น (พอร์ตนี้ฟังแค่ localhost) จึงเชื่อ header นี้ได้
        string ip = ctx.Request.Headers["CF-Connecting-IP"];
        return string.IsNullOrEmpty(ip) ? ctx.Request.RemoteEndPoint.Address.ToString() : ip;
    }

    static void CheckPublicRequest(HttpListenerContext ctx, string route)
    {
        string ip = ClientIp(ctx);
        if (RecentHits("all:" + ip, TimeSpan.FromMinutes(1), true) > 120)
            throw new ApiError(429, "ใช้งานถี่เกินไป กรุณารอสักครู่");
        if (IsPinRoute(route))
        {
            if (RecentHits("fail:" + ip, TimeSpan.FromMinutes(30), false) >= 8)
                throw new ApiError(429, "ใส่ข้อมูลผิดหลายครั้ง กรุณารอ 30 นาที");
            if (RecentHits("login:" + ip, TimeSpan.FromMinutes(15), true) > 30)
                throw new ApiError(429, "ใช้งานถี่เกินไป กรุณารอสักครู่");
        }
        if (ctx.Request.ContentLength64 > 8192) throw new ApiError(413, "ข้อมูลใหญ่เกินไป");
    }

    static void OpenBrowser(string url)
    {
        try { Process.Start(url); } catch { }
    }

    // ---------- auto update ----------
    // update.json: { "version": "1.2.0", "url": "YaMumMember.exe", "sha256": "...", "signature": "...", "notes": "..." }
    // url อาจเป็นลิงก์เต็ม หรือชื่อไฟล์ที่อยู่ที่เดียวกับ update.json ก็ได้
    // signature = ลายเซ็น RSA-SHA256 ของไฟล์ exe ด้วยกุญแจส่วนตัวของร้าน (สร้างด้วย publish.ps1)

    static readonly string AppVersion = typeof(App).Assembly.GetName().Version.ToString(3);
    const string DefaultUpdateUrl = "https://github.com/Porjai362/yamum-member/releases/latest/download/update.json";
    const string UpdatePublicKey = "<RSAKeyValue><Modulus>wgBC4HDrEO0ioFCzSIdkLPhBhl1fzwUjoOQR1sDOQEKQ3PVso4hByeFMtQh1/lDveN2bKM/0K16XB2FYJR30CPvtg4Seqy0H7MziJ25f69joJnsCN91/2i4N0PjKukfJL8gka+QkK6EO30sQ9mTwFY/KPOHGsudJTVagj/nkYHfmKkYB4ccZeGRnW2aTOWdHX8+udvrBSM7NIb+gsoVrPeX6/k6BQgFJX8857IilL+bDDx6PXlV/9UyqsLJU3XlpENxl3Uz7hwmSioB3H9hKHmmfKSJTszpj024L5WOVb/6JJmsgsjD35KJ+UrG/SJYBzWBZPj7zCFwHSG9q0qhFH7/IWpUZtcX8oxY8aPSNpAqP8GER7q2jXSPXgq+RO+VQEoEcAIb/ZpfBcnMRXIhjoyFv6lyARVlYNcphxSN5I6nfAsOsjCK8CRjO9j5bsPTvSdm7c3TG3D8lmLFppI7J3QdIchlCuSPCPyxPZLOv8D/Dnu4ier9XQ2Z7bFDI2515</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    static HttpListener Listener;
    static string BaseUrl, UpdateUrl;
    static volatile bool Restarting;
    static readonly object UpdateLock = new object();
    static readonly DateTime StartedAt = DateTime.Now;
    static DateTime LastActivity = DateTime.MinValue;
    static Dictionary<string, object> Latest;           // update.json ล่าสุดที่ตรวจพบ
    static string UpdateError = "", UpdateStatus = "idle";
    static DateTime? LastCheck;

    static string ExePath { get { return Process.GetCurrentProcess().MainModule.FileName; } }

    static void CleanupOldExe()
    {
        new Thread(() =>
        {
            string old = ExePath + ".old";
            for (int i = 0; i < 60 && File.Exists(old); i++)
            {
                try { File.Delete(old); } catch { Thread.Sleep(1000); }
            }
            try { File.Delete(ExePath + ".new"); } catch { }
        }) { IsBackground = true }.Start();
    }

    static void UpdateLoop()
    {
        Thread.Sleep(5000);
        while (true)
        {
            TimeSpan wait = TimeSpan.FromHours(6);
            try
            {
                CheckUpdate();
                if (UpdateAvailable() && Settings()["AutoUpdate"] == "1" && Str(Latest, "version") != SkippedVersion())
                {
                    // ติดตั้งเองเมื่อเพิ่งเปิดโปรแกรม หรือไม่มีใครใช้งานเกิน 5 นาที จะได้ไม่สะดุดตอนพนักงานกำลังทำรายการ
                    bool idle = DateTime.Now - LastActivity > TimeSpan.FromMinutes(5);
                    if (idle || DateTime.Now - StartedAt < TimeSpan.FromMinutes(2)) InstallUpdate();
                    else wait = TimeSpan.FromMinutes(10);
                }
            }
            catch (Exception e) { UpdateError = e.Message; Log("UPDATE " + e.Message); }
            Thread.Sleep(wait);
        }
    }

    // เวอร์ชันที่เคยติดตั้งแล้วเปิดไม่ขึ้น จะไม่ติดตั้งอัตโนมัติซ้ำ (กดอัปเดตเองได้)
    static string SkipFile { get { return Path.Combine(Root, "update-skip.txt"); } }
    static string SkippedVersion()
    {
        try { return File.Exists(SkipFile) ? File.ReadAllText(SkipFile).Trim() : ""; } catch { return ""; }
    }

    static bool UpdateAvailable()
    {
        var l = Latest;
        return l != null && new Version(Str(l, "version")) > new Version(AppVersion);
    }

    static string ResolveLocation(string baseLoc, string rel)
    {
        if (rel.StartsWith("http://") || rel.StartsWith("https://") || Path.IsPathRooted(rel)) return rel;
        if (baseLoc.StartsWith("http://") || baseLoc.StartsWith("https://")) return new Uri(new Uri(baseLoc), rel).ToString();
        return Path.Combine(Path.GetDirectoryName(baseLoc), rel);
    }

    static byte[] Fetch(string loc)
    {
        if (loc.StartsWith("http://") || loc.StartsWith("https://"))
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768; // TLS 1.2 / 1.1
            using (var wc = new WebClient())
            {
                wc.Headers["User-Agent"] = "YaMumMember/" + AppVersion;
                wc.Headers["Cache-Control"] = "no-cache";
                return wc.DownloadData(loc);
            }
        }
        return File.ReadAllBytes(loc);
    }

    static Dictionary<string, object> CheckUpdate()
    {
        if (UpdateUrl.Length == 0) throw new ApiError(400, "ยังไม่ได้ตั้งค่าที่อยู่อัปเดต (UpdateUrl)");
        lock (UpdateLock)
        {
            UpdateStatus = "checking";
            try
            {
                var m = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Fetch(UpdateUrl)).TrimStart('﻿'));
                new Version(Str(m, "version"));
                if (Str(m, "url").Length == 0 || Str(m, "signature").Length == 0) throw new Exception("ไฟล์ update.json ไม่ครบ");
                Latest = m;
                UpdateError = "";
            }
            catch (Exception e) { UpdateError = "ตรวจสอบอัปเดตไม่ได้: " + e.Message; throw new ApiError(502, UpdateError); }
            finally { LastCheck = DateTime.Now; UpdateStatus = "idle"; }
        }
        return UpdateInfo();
    }

    static Dictionary<string, object> UpdateInfo()
    {
        var l = Latest;
        return new Dictionary<string, object> {
            { "current", AppVersion }, { "latest", l == null ? null : Str(l, "version") },
            { "notes", l == null ? "" : Str(l, "notes") }, { "available", UpdateAvailable() },
            { "status", UpdateStatus }, { "error", UpdateError }, { "configured", UpdateUrl.Length > 0 },
            { "lastCheck", LastCheck.HasValue ? LastCheck.Value.ToString("yyyy-MM-dd HH:mm:ss") : null } };
    }

    static bool VerifySignature(byte[] data, string signatureB64)
    {
        var p = new System.Security.Cryptography.CspParameters(24) { Flags = System.Security.Cryptography.CspProviderFlags.UseMachineKeyStore };
        using (var rsa = new System.Security.Cryptography.RSACryptoServiceProvider(p))
        {
            rsa.PersistKeyInCsp = false;
            rsa.FromXmlString(UpdatePublicKey);
            return rsa.VerifyData(data, "SHA256", Convert.FromBase64String(signatureB64));
        }
    }

    static void InstallUpdate()
    {
        lock (UpdateLock)
        {
            if (Restarting || !UpdateAvailable()) return;
            var m = Latest;
            string ver = Str(m, "version");
            string exe = ExePath, fresh = exe + ".new", old = exe + ".old";
            UpdateStatus = "downloading";
            try
            {
                Log("กำลังดาวน์โหลดเวอร์ชัน " + ver + " ...");
                byte[] data = Fetch(ResolveLocation(UpdateUrl, Str(m, "url")));
                string sha = BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(data)).Replace("-", "");
                if (Str(m, "sha256").Length > 0 && !sha.Equals(Str(m, "sha256"), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("ไฟล์ที่ดาวน์โหลดเสียหาย (sha256 ไม่ตรง)");
                if (!VerifySignature(data, Str(m, "signature")))
                    throw new Exception("ลายเซ็นไม่ถูกต้อง — ไม่ติดตั้งไฟล์นี้");
                File.WriteAllBytes(fresh, data);
                var newVer = System.Reflection.AssemblyName.GetAssemblyName(fresh).Version;
                if (newVer.ToString(3) != ver) throw new Exception("เวอร์ชันในไฟล์ (" + newVer.ToString(3) + ") ไม่ตรงกับ update.json");

                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);       // exe ที่กำลังรันอยู่เปลี่ยนชื่อได้ แต่ลบ/เขียนทับไม่ได้
                File.Move(fresh, exe);
            }
            catch (Exception e)
            {
                try { File.Delete(fresh); } catch { }
                UpdateStatus = "idle";
                UpdateError = "อัปเดตไม่สำเร็จ: " + e.Message;
                throw new ApiError(500, UpdateError);
            }

            UpdateStatus = "restarting";
            Log("ติดตั้งเวอร์ชัน " + ver + " แล้ว กำลังเริ่มโปรแกรมใหม่...");
            new Thread(() => Restart(exe, old, ver)).Start();
        }
    }

    // เปิดในหน้าต่างใหม่ ถ้าไม่ได้ (บางเครื่องห้าม) ให้ใช้หน้าต่างเดิมต่อ
    static Process StartSelf(string exe, string args)
    {
        foreach (bool shell in new[] { true, false })
        {
            try { return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = shell, WorkingDirectory = Root }); }
            catch (Exception e) { Log("เปิดโปรแกรมไม่ได้ (" + (shell ? "หน้าต่างใหม่" : "หน้าต่างเดิม") + "): " + e.Message); }
        }
        return null;
    }

    // ปิดตัวเอง เปิดเวอร์ชันใหม่ แล้วรอจนเวอร์ชันใหม่ตอบได้ ถ้าไม่ขึ้นภายใน 60 วินาที ย้อนกลับเวอร์ชันเดิม
    static void Restart(string exe, string old, string ver)
    {
        Thread.Sleep(1500); // ให้คำตอบ API ส่งถึงหน้าเว็บก่อน
        Restarting = true;
        try { Listener.Stop(); } catch { }
        try { if (PublicListener != null) PublicListener.Stop(); } catch { }
        lock (ProxyListeners) foreach (var tl in ProxyListeners) { try { tl.Stop(); } catch { } }
        Process proc = StartSelf(exe, "--after-update");

        for (int i = 0; proc != null && i < 60; i++)
        {
            Thread.Sleep(1000);
            try
            {
                using (var wc = new WebClient())
                {
                    var v = Json.Deserialize<Dictionary<string, object>>(wc.DownloadString(BaseUrl + "api/version"));
                    if (Str(v, "version") == ver) ExitApp(0);
                }
            }
            catch { }
            if (proc.HasExited) break;
        }

        Log("เวอร์ชันใหม่เปิดไม่ขึ้น — ย้อนกลับเวอร์ชัน " + AppVersion);
        try { File.WriteAllText(SkipFile, ver); } catch { }
        try { if (proc != null && !proc.HasExited) { proc.Kill(); proc.WaitForExit(5000); } } catch { }
        try
        {
            string bad = exe + ".bad";
            if (File.Exists(bad)) File.Delete(bad);
            File.Move(exe, bad);
            File.Move(old, exe);
            if (StartSelf(exe, "--after-update --rolled-back") == null) throw new Exception("เปิดโปรแกรมไม่ได้");
        }
        catch (Exception e)
        {
            string msg = "ย้อนกลับเวอร์ชันไม่สำเร็จ: " + e.Message + "\n\nให้เปลี่ยนชื่อไฟล์ " + Path.GetFileName(old) + " กลับเป็น " + Path.GetFileName(exe) + " แล้วเปิดใหม่";
            Log(msg);
            Fatal(msg);
        }
        ExitApp(1);
    }

    // ---------- config / db ----------

    static Dictionary<string, string> LoadConfig(string file)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return d;
        foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
            int i = line.IndexOf('=');
            if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
        }
        return d;
    }

    static string Cfg(Dictionary<string, string> c, string k, string def)
    {
        string v;
        return c.TryGetValue(k, out v) && v.Length > 0 ? v : def;
    }

    static string Ident(string s)
    {
        if (!Regex.IsMatch(s, "^[A-Za-z0-9_]+$")) throw new Exception("ชื่อฐานข้อมูลไม่ถูกต้อง: " + s);
        return s;
    }

    static void InitDb()
    {
        using (var cn = new SqlConnection(MasterCs))
        {
            cn.Open();
            var cmd = cn.CreateCommand();
            cmd.CommandText = "IF DB_ID('" + MemberDb + "') IS NULL CREATE DATABASE [" + MemberDb + "]";
            cmd.ExecuteNonQuery();
        }
        Exec(@"
IF OBJECT_ID('dbo.Setting') IS NULL
  CREATE TABLE dbo.Setting([Key] nvarchar(50) NOT NULL PRIMARY KEY, [Value] nvarchar(1000) NOT NULL);
IF OBJECT_ID('dbo.Reward') IS NULL
  CREATE TABLE dbo.Reward(
    Id int IDENTITY PRIMARY KEY, Name nvarchar(200) NOT NULL, Points int NOT NULL,
    Note nvarchar(500) NULL, IsActive bit NOT NULL DEFAULT 1, CreatedAt datetime NOT NULL DEFAULT GETDATE());
IF OBJECT_ID('dbo.Ledger') IS NULL
BEGIN
  CREATE TABLE dbo.Ledger(
    Id int IDENTITY PRIMARY KEY, CustomerId int NOT NULL, Kind varchar(20) NOT NULL, Points int NOT NULL,
    RewardId int NULL, Description nvarchar(300) NULL, Staff nvarchar(100) NULL,
    CreatedAt datetime NOT NULL DEFAULT GETDATE(), IsCancelled bit NOT NULL DEFAULT 0,
    CancelledAt datetime NULL, CancelledBy nvarchar(100) NULL);
  CREATE INDEX IX_Ledger_Customer ON dbo.Ledger(CustomerId);
END
IF OBJECT_ID('dbo.CustomerEdit') IS NULL
BEGIN
  CREATE TABLE dbo.CustomerEdit(
    Id int IDENTITY PRIMARY KEY, CustomerId int NOT NULL, Field varchar(20) NOT NULL,
    OldValue nvarchar(600) NULL, NewValue nvarchar(600) NULL,
    Source varchar(10) NOT NULL, Status varchar(12) NOT NULL, Note nvarchar(300) NULL,
    CreatedAt datetime NOT NULL DEFAULT GETDATE(), DecidedAt datetime NULL, DecidedBy nvarchar(100) NULL);
  CREATE INDEX IX_CustomerEdit_Status ON dbo.CustomerEdit(Status, CustomerId);
END
IF OBJECT_ID('dbo.CloudPushed') IS NULL
  CREATE TABLE dbo.CloudPushed(Kind char(1) NOT NULL, Id int NOT NULL, Hash varchar(64) NOT NULL, PRIMARY KEY(Kind, Id));
IF OBJECT_ID('dbo.MemberPin') IS NULL
  CREATE TABLE dbo.MemberPin(
    CustomerId int NOT NULL PRIMARY KEY, PinHash varchar(100) NOT NULL, Salt varchar(50) NOT NULL,
    FailCount int NOT NULL DEFAULT 0, LockedUntil datetime NULL,
    UpdatedAt datetime NOT NULL DEFAULT GETDATE(), UpdatedBy nvarchar(100) NULL);");
        // แจ้งเตือน LINE (v1.11): สถานะที่แจ้งแล้ว / คำขอแก้ไขเก่าถือว่าแจ้งแล้ว (ไม่ส่งย้อนหลัง)
        Exec(@"
IF OBJECT_ID('dbo.LineNotify') IS NULL
  CREATE TABLE dbo.LineNotify(CustomerId int NOT NULL PRIMARY KEY, LastOrderId int NOT NULL, BirthdayYear int NOT NULL DEFAULT 0);
IF COL_LENGTH('dbo.MemberPin','MustChange') IS NULL
  ALTER TABLE dbo.MemberPin ADD MustChange bit NOT NULL CONSTRAINT DF_MemberPin_MustChange DEFAULT 0;
IF COL_LENGTH('dbo.CustomerEdit','Notified') IS NULL
BEGIN
  ALTER TABLE dbo.CustomerEdit ADD Notified bit NOT NULL CONSTRAINT DF_CustomerEdit_Notified DEFAULT 0;
  EXEC('UPDATE dbo.CustomerEdit SET Notified=1');
END");
        foreach (var kv in DefaultSettings)
            Exec("IF NOT EXISTS(SELECT 1 FROM dbo.Setting WHERE [Key]=@k) INSERT dbo.Setting([Key],[Value]) VALUES(@k,@v)", "@k", kv[0], "@v", kv[1]);
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.Reward")) == 0)
        {
            Exec("INSERT dbo.Reward(Name,Points,Note) VALUES(@n,@p,@x)", "@n", "ส่วนลด 50 บาท", "@p", 100, "@x", "แคชเชียร์ใส่ส่วนลดในบิล CW");
            Exec("INSERT dbo.Reward(Name,Points,Note) VALUES(@n,@p,@x)", "@n", "หน้ากากอนามัย 1 กล่อง", "@p", 80, "@x", "");
            Exec("INSERT dbo.Reward(Name,Points,Note) VALUES(@n,@p,@x)", "@n", "ส่วนลด 150 บาท", "@p", 250, "@x", "แคชเชียร์ใส่ส่วนลดในบิล CW");
        }
        // ทดสอบว่าอ่านฐานข้อมูล CW ได้
        Scalar("SELECT COUNT(*) FROM " + Cw + "Customer");
    }

    static SqlCommand Cmd(SqlConnection cn, string sql, object[] kv)
    {
        var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        for (int i = 0; i + 1 < kv.Length; i += 2)
            cmd.Parameters.AddWithValue((string)kv[i], kv[i + 1] ?? DBNull.Value);
        return cmd;
    }

    static int Exec(string sql, params object[] kv)
    {
        using (var cn = new SqlConnection(MemberCs)) { cn.Open(); return Cmd(cn, sql, kv).ExecuteNonQuery(); }
    }

    static object Scalar(string sql, params object[] kv)
    {
        using (var cn = new SqlConnection(MemberCs)) { cn.Open(); return Cmd(cn, sql, kv).ExecuteScalar(); }
    }

    static List<Dictionary<string, object>> Query(string sql, params object[] kv)
    {
        var list = new List<Dictionary<string, object>>();
        using (var cn = new SqlConnection(MemberCs))
        {
            cn.Open();
            using (var r = Cmd(cn, sql, kv).ExecuteReader())
            {
                while (r.Read())
                {
                    var d = new Dictionary<string, object>();
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        object v = r.GetValue(i);
                        if (v is DBNull) v = null;
                        else if (v is DateTime) v = ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss");
                        else if (v is decimal) v = (double)(decimal)v;
                        else if (v is string) v = ((string)v).Trim();
                        d[r.GetName(i)] = v;
                    }
                    list.Add(d);
                }
            }
        }
        return list;
    }

    // ---------- http ----------

    // pub = หน้าลูกค้าผ่าน Tunnel / remote = หน้าพนักงานผ่าน Tunnel (ต้องเข้าสู่ระบบ) / ไม่ใช่ทั้งคู่ = เครื่องร้าน
    static void Handle(HttpListenerContext ctx, bool pub, bool remote = false)
    {
        string route = null;
        try
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (pub || remote)
            {
                var h = ctx.Response.Headers;
                h["X-Frame-Options"] = "DENY";
                h["X-Content-Type-Options"] = "nosniff";
                h["Referrer-Policy"] = "no-referrer";
                h["Strict-Transport-Security"] = "max-age=31536000";
            }
            if (path.StartsWith("/api/"))
            {
                route = path.Substring(5).Trim('/');
                if (pub)
                {
                    if (!IsPublicRoute(route)) throw new ApiError(404, "ไม่พบหน้านี้");
                    CheckPublicRequest(ctx, route);
                }
                if (remote) CheckAdminRequest(ctx, route, Settings());
                WriteJson(ctx, 200, Api(ctx, route, ctx.Request.HttpMethod, pub, remote));
            }
            else if (pub)
            {
                // เว็บสาธารณะมีแค่หน้าลูกค้าหน้าเดียว
                if (path == "/" || path == "/check" || path == "/check.html") ServeStatic(ctx, "/check.html");
                else ctx.Response.StatusCode = 404;
            }
            else
                ServeStatic(ctx, path);
        }
        catch (ApiError e)
        {
            if (pub && IsPinRoute(route) && e.Status == 403) RecentHits("fail:" + ClientIp(ctx), TimeSpan.FromMinutes(30), true);
            if (remote && e.Status == 401 && !(e is AdminLoginRequired)) RecentHits("pinfail:" + ClientIp(ctx), TimeSpan.FromMinutes(15), true); // PIN พนักงานผิด
            var body = new Dictionary<string, object> { { "error", e.Message } };
            if (e is AdminLoginRequired) body["login"] = true;
            WriteJson(ctx, e.Status, body);
        }
        catch (Exception e)
        {
            Log("ERROR " + e.Message);
            // ไม่ส่งรายละเอียดข้อผิดพลาด (เช่น SQL) ออกไปยังอินเทอร์เน็ต
            try { WriteJson(ctx, 500, new Dictionary<string, object> { { "error", pub || remote ? "ระบบขัดข้อง กรุณาลองใหม่" : e.Message } }); } catch { }
        }
        finally { try { ctx.Response.OutputStream.Close(); } catch { } }
    }

    static void WriteJson(HttpListenerContext ctx, int status, object o)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(o));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    // ใช้ไฟล์ในโฟลเดอร์ wwwroot ถ้ามี (สำหรับแก้หน้าเว็บ) ไม่งั้นใช้ไฟล์ที่ฝังอยู่ใน exe
    static byte[] WebFile(string name)
    {
        string full = Path.GetFullPath(Path.Combine(WebRoot, name));
        if (full.StartsWith(WebRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            return File.ReadAllBytes(full);
        if (name.Contains("/") || name.Contains("\\")) return null;
        using (var st = typeof(App).Assembly.GetManifestResourceStream("www." + name))
        {
            if (st == null) return null;
            var ms = new MemoryStream();
            st.CopyTo(ms);
            return ms.ToArray();
        }
    }

    static void ServeStatic(HttpListenerContext ctx, string path)
    {
        if (path == "/") path = "/index.html";
        if (path == "/check") path = "/check.html";
        string name = Uri.UnescapeDataString(path).TrimStart('/');
        byte[] bytes = WebFile(name);
        if (bytes == null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        string ext = Path.GetExtension(name).ToLowerInvariant();
        string type = ext == ".html" ? "text/html; charset=utf-8" : ext == ".js" ? "text/javascript; charset=utf-8"
            : ext == ".css" ? "text/css; charset=utf-8" : ext == ".svg" ? "image/svg+xml" : ext == ".png" ? "image/png"
            : ext == ".ico" ? "image/x-icon" : "application/octet-stream";
        ctx.Response.ContentType = type;
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    static Dictionary<string, object> Body(HttpListenerContext ctx)
    {
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            string s = sr.ReadToEnd();
            if (string.IsNullOrWhiteSpace(s)) return new Dictionary<string, object>();
            return Json.Deserialize<Dictionary<string, object>>(s);
        }
    }

    static string Str(Dictionary<string, object> b, string k)
    {
        object v;
        return b.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture).Trim() : "";
    }

    static int Int(Dictionary<string, object> b, string k)
    {
        string s = Str(b, k);
        int n;
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) throw new ApiError(400, "ค่า " + k + " ต้องเป็นตัวเลข");
        return n;
    }

    // อ่าน query string เองเป็น UTF-8 (QueryString ของ HttpListener ถอดรหัสด้วย codepage ของ Windows ทำให้ภาษาไทยเพี้ยน)
    static string Q(HttpListenerContext ctx, string k)
    {
        foreach (var part in ctx.Request.Url.Query.TrimStart('?').Split('&'))
        {
            int i = part.IndexOf('=');
            string name = Uri.UnescapeDataString((i < 0 ? part : part.Substring(0, i)).Replace('+', ' '));
            if (name == k) return i < 0 ? "" : Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' ')).Trim();
        }
        return "";
    }

    static void RequirePin(HttpListenerContext ctx, Dictionary<string, string> s)
    {
        string pin = s["StaffPin"];
        if (pin.Length > 0 && ctx.Request.Headers["X-Staff-Pin"] != pin)
            throw new ApiError(401, "ต้องใส่ PIN พนักงาน");
    }

    static object Api(HttpListenerContext ctx, string route, string method, bool pub, bool remote = false)
    {
        var seg = route.Split('/');
        bool post = method == "POST";
        if (route == "version") return new Dictionary<string, object> { { "version", AppVersion } };
        if (route == "mode") return new Dictionary<string, object> { { "public", pub } };
        if (!route.StartsWith("update")) LastActivity = DateTime.Now;

        var s = Settings();
        if (pub && route == "settings") return new Dictionary<string, object> { { "ShopName", s["ShopName"] } };
        if (!pub && route == "admin/me")
            return new Dictionary<string, object> { { "remote", remote }, { "enabled", AdminEnabled(s) }, { "loggedIn", !remote || AdminLoggedIn(ctx) },
                { "hasPassword", s.ContainsKey("AdminPasswordHash") && s["AdminPasswordHash"].Length > 0 } };
        if (remote && route == "admin/login" && post) return AdminLogin(ctx, s, Body(ctx));
        if (remote && route == "admin/logout" && post) return AdminLogout(ctx);
        if (!pub && !remote && route == "admin/password" && post) { RequirePin(ctx, s); return SetAdminPassword(Body(ctx)); }
        if (route == "update" && !post) return UpdateInfo();
        if (route == "update/check" && post) return CheckUpdate();
        if (route == "update/install" && post)
        {
            RequirePin(ctx, s);
            CheckUpdate(); // อ่าน update.json ล่าสุดเสมอ ไม่ใช้ของเก่าที่ตรวจไว้
            if (!UpdateAvailable()) throw new ApiError(400, "ใช้เวอร์ชันล่าสุดอยู่แล้ว");
            InstallUpdate();
            return UpdateInfo();
        }

        if (route == "settings" && !post) return PublicSettings(s);
        if (route == "settings" && post) { RequirePin(ctx, s); return SaveSettings(Body(ctx)); }
        if (route == "pin" && post)
        {
            RequirePin(ctx, s);
            return new Dictionary<string, object> { { "ok", true } };
        }
        if (route == "dashboard") return Dashboard(s);
        if (route == "members" && !post) return Members(s, Q(ctx, "q"));
        if (seg[0] == "members" && seg.Length == 2 && !post) return MemberDetail(s, ParseId(seg[1]));
        if (seg[0] == "members" && seg.Length == 3 && post)
        {
            RequirePin(ctx, s);
            int id = ParseId(seg[1]);
            if (seg[2] == "redeem") return Redeem(s, id, Body(ctx));
            if (seg[2] == "adjust") return Adjust(s, id, Body(ctx));
            if (seg[2] == "pin") return SetMemberPin(id, Body(ctx));
            if (seg[2] == "temppin") return TempPin(id, Body(ctx));
            if (seg[2] == "edit") return StaffEditCustomer(s, id, Body(ctx));
        }
        if (seg[0] == "orders" && seg.Length == 2) return OrderItems(ParseId(seg[1]));
        if (route == "ledger" && !post) return LedgerList(0, 200);
        if (seg[0] == "ledger" && seg.Length == 3 && seg[2] == "cancel" && post)
        {
            RequirePin(ctx, s);
            return CancelLedger(ParseId(seg[1]), Str(Body(ctx), "staff"));
        }
        if (route == "pins/temp-all" && post) { RequirePin(ctx, s); return TempPinAll(s, Body(ctx)); }
        if (route == "edits" && !post) return EditList(Q(ctx, "status"), 0, 300);
        if (seg[0] == "edits" && seg.Length == 3 && post && (seg[2] == "approve" || seg[2] == "reject"))
        {
            RequirePin(ctx, s);
            return DecideEdit(ParseId(seg[1]), seg[2] == "approve", Body(ctx));
        }
        if (route == "points/recalc" && !post) return PointRecalc(s);
        if (route == "drugnames") return DrugNames();
        if (route == "cloud/status") return CloudStatus();
        if (route == "cloud/sync" && post) { RequirePin(ctx, s); return CloudSync(Q(ctx, "full") == "1"); }
        if (route == "cloud/newkey" && post) { RequirePin(ctx, s); return CloudNewKey(); }
        if (route == "rewards" && !post) return Query("SELECT * FROM dbo.Reward ORDER BY IsActive DESC, Points");
        if (route == "rewards" && post) { RequirePin(ctx, s); return SaveReward(Body(ctx)); }
        if (route == "check") return SelfCheck(s, Q(ctx, "phone"));
        // พื้นที่สมาชิก (ต้องเข้าสู่ระบบด้วยเบอร์ + PIN)
        if (route == "my/login" && post) return MyLogin(s, Body(ctx));
        if (route == "my/profile") return MyProfile(s, SessionCustomer(ctx));
        if (route == "my/pin" && post) return MyChangePin(ctx, Body(ctx));
        if (route == "my/logout" && post) return MyLogout(ctx);
        if (route == "my/edit" && post) return MyEditRequest(s, ctx, Body(ctx));
        if (seg.Length == 3 && seg[0] == "my" && seg[1] == "drug") { SessionCustomer(ctx); return DrugDetail(ParseId(seg[2])); }
        throw new ApiError(404, "ไม่พบ API: " + route);
    }

    static int ParseId(string s)
    {
        int n;
        if (!int.TryParse(s, out n)) throw new ApiError(400, "รหัสไม่ถูกต้อง");
        return n;
    }

    // ---------- settings ----------

    static Dictionary<string, string> Settings()
    {
        var d = new Dictionary<string, string>();
        foreach (var kv in DefaultSettings) d[kv[0]] = kv[1];
        foreach (var r in Query("SELECT [Key],[Value] FROM dbo.Setting")) d[(string)r["Key"]] = (string)r["Value"] ?? "";
        return d;
    }

    static Dictionary<string, object> PublicSettings(Dictionary<string, string> s)
    {
        var d = new Dictionary<string, object>();
        foreach (var kv in s) if (kv.Key != "StaffPin" && kv.Key != "CloudKey" && kv.Key != "AdminPasswordHash") d[kv.Key] = kv.Value;
        d["HasPin"] = s["StaffPin"].Length > 0;
        d["HasCloudKey"] = s["CloudKey"].Length > 0;
        d["CwPoint"] = CwPointConfig();
        d["CwDatabase"] = Cw.Split(']')[0].TrimStart('[');
        return d;
    }

    static object SaveSettings(Dictionary<string, object> b)
    {
        foreach (var kv in DefaultSettings)
        {
            string k = kv[0];
            if (!b.ContainsKey(k)) continue;
            string v = Str(b, k);
            if (k == "StaffPin" && v == "__keep__") continue;
            if (k == "CloudKey") continue; // สร้างด้วยปุ่ม "สร้างคีย์ใหม่" เท่านั้น
            if (k == "CloudUrl")
            {
                v = v.TrimEnd('/');
                if (v.Length > 0 && !Regex.IsMatch(v, @"^https://[a-z0-9.-]+(:\d+)?$", RegexOptions.IgnoreCase))
                    throw new ApiError(400, "ที่อยู่เว็บออนไลน์ต้องเป็น https://ชื่อโดเมน");
                if (v != Settings()["CloudUrl"]) Exec("DELETE FROM dbo.CloudPushed"); // ที่ใหม่ ส่งข้อมูลทั้งหมดใหม่
            }
            if (k == "BahtPerPoint" || k.StartsWith("Tier"))
            {
                double n;
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n) || n <= 0)
                    throw new ApiError(400, k + " ต้องเป็นตัวเลขมากกว่า 0");
            }
            if (k == "PointSource" && v != "cw" && v != "own") throw new ApiError(400, "แหล่งแต้มไม่ถูกต้อง");
            if (k == "PointStartDate")
            {
                DateTime dt;
                if (!DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                    throw new ApiError(400, "วันที่เริ่มนับแต้มไม่ถูกต้อง");
            }
            Exec("UPDATE dbo.Setting SET [Value]=@v WHERE [Key]=@k; IF @@ROWCOUNT=0 INSERT dbo.Setting VALUES(@k,@v)", "@k", k, "@v", v);
        }
        return PublicSettings(Settings());
    }

    // ---------- members ----------

    static double Num(Dictionary<string, string> s, string k)
    {
        return double.Parse(s[k], CultureInfo.InvariantCulture);
    }

    // ยอดขายสุทธิต่อบิล = ยอดบิล - ยอดรับคืนสินค้า (ใบลดหนี้ที่ไม่ถูกยกเลิก)
    static string OrdersCte()
    {
        return @"
ret AS (
  SELECT Order_Id, SUM(Price_Amount) amt,
         SUM(ISNULL(RecPoint_Value,0)) retRec, SUM(ISNULL(PayPoint_Value,0)) retPay
  FROM " + Cw + @"ProductBack
  WHERE ISNULL(IsCancel,0)=0 AND ProductBack_Status=2 GROUP BY Order_Id
), o AS (
  SELECT o.Id, o.Order_Code, o.Customer_Id cid, o.Date_Order d, o.OrderPriceNet gross,
         ISNULL(r.amt,0) returned, o.OrderPriceNet - ISNULL(r.amt,0) net,
         -- แต้มระบบ CW: ได้จากบิล (rec) / ใช้เป็นส่วนลด (pay) — หักส่วนที่คืนสินค้าแล้ว
         -- CW ตั้งชื่อจากฝั่งลูกค้า: PayPoint_Value = แต้มที่ได้จากการจ่ายเงิน, RecPoint_Value = แต้มที่ใช้รับส่วนลด
         ISNULL(o.PayPoint_Value,0) - ISNULL(r.retPay,0) rec, ISNULL(o.RecPoint_Value,0) - ISNULL(r.retRec,0) pay
  FROM " + Cw + @"[Order] o LEFT JOIN ret r ON r.Order_Id=o.Id
  WHERE ISNULL(o.IsOrderCancel,0)=0 AND o.Order_Status=2 AND ISNULL(o.IsDelete,0)=0
)";
    }

    // ---------- แต้มจากระบบ CW ----------
    // PointSource = "cw": ใช้แต้มสะสมของโปรแกรม CW เป็นหลัก (ยอดคงเหลือ = Customer.Rt_Point_Value)
    //   ได้แต้ม/ใช้แต้มทำที่หน้าขาย CW — ระบบสมาชิกอ่านอย่างเดียว ไม่แลก/ปรับแต้มเอง (กันแต้มซ้ำสองที่)
    // PointSource = "own": คำนวณแต้มเองจากยอดซื้อ + แลก/ปรับแต้มในระบบสมาชิก (แบบเดิม)

    static bool CwPoints(Dictionary<string, string> s) { return s["PointSource"] != "own"; }

    static Dictionary<string, object> CwPointCache;
    static DateTime CwPointCacheAt;

    // ตั้งค่าแต้มของ CW เก็บใน Branch.PointGlobalValue_Data (บีบอัด gzip + LosFormatter)
    static Dictionary<string, object> CwPointConfig()
    {
        if (CwPointCache != null && DateTime.Now - CwPointCacheAt < TimeSpan.FromMinutes(10)) return CwPointCache;
        var res = new Dictionary<string, object> { { "ok", false } };
        try
        {
            string b64 = Convert.ToString(Scalar("SELECT TOP 1 CAST(PointGlobalValue_Data AS nvarchar(max)) FROM " + Cw + "Branch ORDER BY Id"));
            if (!string.IsNullOrEmpty(b64))
            {
                byte[] b = Convert.FromBase64String(b64);
                string los;
                using (var gz = new System.IO.Compression.GZipStream(new MemoryStream(b, 4, b.Length - 4), System.IO.Compression.CompressionMode.Decompress))
                using (var sr = new StreamReader(gz, Encoding.UTF8)) los = sr.ReadToEnd();
                var d = new System.Web.UI.LosFormatter().Deserialize(los) as System.Collections.IDictionary;
                if (d != null)
                {
                    Func<string, object> v = k => d.Contains(k) ? d[k] : null;
                    Func<string, double> num = k => { double x; return double.TryParse(Convert.ToString(v(k), CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : 0; };
                    Func<string, string> day = k => v(k) is DateTime && ((DateTime)v(k)).Year > 1900 ? ((DateTime)v(k)).ToString("yyyy-MM-dd") : null;
                    res["ok"] = true;
                    res["Active"] = Convert.ToBoolean(v("Rt_IsActvie") ?? false);
                    // ชื่อใน CW มองจากฝั่งลูกค้า: Pay = ลูกค้าจ่ายเงินแล้วได้แต้ม / Rec = ลูกค้าได้รับส่วนลดจากแต้ม
                    // (ตรวจกับหน้าตั้งค่า CW แล้ว: ซื้อ 50 บาท = 1 แต้ม, 1 แต้ม = 1 บาท)
                    // ในระบบนี้ Rec* = อัตราได้แต้ม, Pay* = อัตราใช้แต้ม
                    res["RecActive"] = Convert.ToBoolean(v("Rt_IsActvie_Pay") ?? false);
                    res["PayActive"] = Convert.ToBoolean(v("Rt_IsActvie_Rec") ?? false);
                    res["RecPrice"] = num("Rt_PayPrice_Rate"); res["RecPoint"] = num("Rt_PayPoint_Rate");
                    res["PayPrice"] = num("Rt_RecPrice_Rate"); res["PayPoint"] = num("Rt_RecPoint_Rate");
                    res["Begin"] = day("Rt_Date_Begin"); res["End"] = day("Rt_Date_End");
                }
            }
        }
        catch (Exception e) { res["error"] = e.Message; }
        CwPointCache = res;
        CwPointCacheAt = DateTime.Now;
        return res;
    }

    // ประวัติแต้ม รวมเป็นรายการเดียว (วันที่, แต้ม +/-, คำอธิบาย) เรียงล่าสุดก่อน
    static List<Dictionary<string, object>> PointHistory(Dictionary<string, string> s, int id, List<Dictionary<string, object>> bills, bool forStaff)
    {
        var list = new List<Dictionary<string, object>>();
        Action<object, double, string> add = (date, pts, text) =>
            list.Add(new Dictionary<string, object> { { "Date", date }, { "Points", Math.Round(pts, 2) }, { "Text", text } });
        foreach (var b in bills)
        {
            double rec = Convert.ToDouble(b["Points"]), pay = Convert.ToDouble(b["PayPoints"]);
            if (rec != 0) add(b["Date"], rec, "ซื้อสินค้า ฿" + Convert.ToDouble(b["Net"]).ToString("#,0.##") + " · " + b["Code"]);
            if (pay != 0) add(b["Date"], -pay, "ใช้แต้มเป็นส่วนลด · " + b["Code"]);
        }
        if (!CwPoints(s))
            foreach (var l in Query("SELECT Kind, Points, Description, Staff, CreatedAt FROM dbo.Ledger WHERE CustomerId=@id AND IsCancelled=0", "@id", id))
                add(l["CreatedAt"], Convert.ToDouble(l["Points"]),
                    ((string)l["Kind"] == "redeem" ? "แลก " : "ปรับแต้ม · ") + l["Description"] + (forStaff && l["Staff"] != null ? " (" + l["Staff"] + ")" : ""));
        return list.OrderByDescending(x => DateTime.Parse((string)x["Date"], CultureInfo.InvariantCulture)).ToList();
    }

    static List<Dictionary<string, object>> MemberRows(Dictionary<string, string> s, string where, params object[] kv)
    {
        string sql = @"
WITH " + OrdersCte() + @", agg AS (
  SELECT cid, COUNT(*) visits, SUM(net) spendAll, MAX(d) lastVisit,
         SUM(CASE WHEN d >= DATEADD(day,-365,GETDATE()) THEN net ELSE 0 END) spend365,
         SUM(CASE WHEN d >= @pstart AND net > 0 THEN FLOOR(net/@bpp) ELSE 0 END) earned,
         SUM(rec) cwRec, SUM(pay) cwPay
  FROM o WHERE cid > 0 GROUP BY cid
), led AS (
  SELECT CustomerId,
         SUM(CASE WHEN Kind='redeem' THEN -Points ELSE 0 END) redeemed,
         SUM(CASE WHEN Kind<>'redeem' THEN Points ELSE 0 END) adjusted
  FROM dbo.Ledger WHERE IsCancelled=0 GROUP BY CustomerId
)
SELECT c.Id, c.Customer_Code Code, c.BarCode, c.FullName, c.Phone, c.EmailAddress Email, c.BirthDate, c.Sex,
       c.Address, c.Date_Register Registered, CAST(c.DrgGenName_ItemCsv AS nvarchar(max)) AllergyDrugs, c.Intolerance AllergyNote, c.CongenitalDisease Disease, c.Comment,
       CAST(ISNULL(c.IsWholesaleCustomer,0) AS bit) Wholesale,
       ISNULL(a.visits,0) Visits, ISNULL(a.spendAll,0) SpendAll, ISNULL(a.spend365,0) Spend365, a.lastVisit LastVisit,
       CAST(ISNULL(a.earned,0) AS int) Earned, ISNULL(l.redeemed,0) Redeemed, ISNULL(l.adjusted,0) Adjusted,
       ISNULL(c.Rt_Point_Value,0) CwBalance, ISNULL(a.cwRec,0) CwRec, ISNULL(a.cwPay,0) CwPay
FROM " + Cw + @"Customer c
LEFT JOIN agg a ON a.cid=c.Id
LEFT JOIN led l ON l.CustomerId=c.Id
WHERE ISNULL(c.IsDelete,0)=0" + (s["IncludeWholesale"] == "1" ? "" : " AND ISNULL(c.IsWholesaleCustomer,0)=0") + where;

        var args = new List<object>(kv);
        args.AddRange(new object[] {
            "@pstart", DateTime.ParseExact(s["PointStartDate"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "@bpp", (decimal)Num(s, "BahtPerPoint") });
        var rows = Query(sql, args.ToArray());
        bool cw = CwPoints(s);
        foreach (var r in rows)
        {
            if (cw)
            {
                // ยอดคงเหลือจาก CW / ได้-ใช้ จากบิล / ส่วนต่าง = ปรับแต้มใน CW (เช่น ยกยอด หมดอายุ)
                double bal = Math.Round(Convert.ToDouble(r["CwBalance"]), 2), rec = Math.Round(Convert.ToDouble(r["CwRec"]), 2), pay = Math.Round(Convert.ToDouble(r["CwPay"]), 2);
                r["Points"] = bal; r["Earned"] = rec; r["Redeemed"] = pay; r["Adjusted"] = Math.Round(bal - (rec - pay), 2);
            }
            else
                r["Points"] = Convert.ToInt32(r["Earned"]) + Convert.ToInt32(r["Adjusted"]) - Convert.ToInt32(r["Redeemed"]);
            r.Remove("CwBalance"); r.Remove("CwRec"); r.Remove("CwPay");
            // แพ้ยา = รายชื่อยาที่ CW ใช้เตือน + อาการแพ้/หมายเหตุ
            string drugs = ((string)r["AllergyDrugs"] ?? "").Replace(",", ", "), note = (string)r["AllergyNote"] ?? "";
            r["Allergy"] = drugs.Length > 0 && note.Length > 0 ? drugs + " (" + note + ")" : drugs + note;
            ApplyTier(r, s);
        }
        return rows;
    }

    static void ApplyTier(Dictionary<string, object> r, Dictionary<string, string> s)
    {
        double spend = Convert.ToDouble(r["Spend365"]);
        string[] tiers = { "member", "silver", "gold", "platinum" };
        double[] min = { 0, Num(s, "TierSilver"), Num(s, "TierGold"), Num(s, "TierPlatinum") };
        int t = 0;
        for (int i = 1; i < 4; i++) if (spend >= min[i]) t = i;
        r["Tier"] = tiers[t];
        r["Benefit"] = s["Benefit" + char.ToUpper(tiers[t][0]) + tiers[t].Substring(1)];
        r["NextTier"] = t < 3 ? tiers[t + 1] : null;
        r["ToNextTier"] = t < 3 ? (object)Math.Max(0, min[t + 1] - spend) : null;
        r["NextTierMin"] = t < 3 ? (object)min[t + 1] : null;
        r["TierMin"] = min[t];
    }

    static object Members(Dictionary<string, string> s, string q)
    {
        if (q.Length == 0) return MemberRows(s, "");
        string digits = Regex.Replace(q, "[^0-9]", "");
        return MemberRows(s, @" AND (c.FullName LIKE @q OR c.Customer_Code LIKE @q OR c.BarCode = @raw
            OR c.PersonalID = @raw OR (LEN(@d) >= 3 AND REPLACE(REPLACE(c.Phone,'-',''),' ','') LIKE @dq))",
            "@q", "%" + q + "%", "@raw", q, "@d", digits, "@dq", "%" + digits + "%");
    }

    static Dictionary<string, object> GetMember(Dictionary<string, string> s, int id)
    {
        var rows = MemberRows(s, " AND c.Id=@id", "@id", id);
        if (rows.Count == 0) throw new ApiError(404, "ไม่พบสมาชิก");
        return rows[0];
    }

    static object MemberDetail(Dictionary<string, string> s, int id)
    {
        var m = GetMember(s, id);
        return new Dictionary<string, object> {
            { "member", m }, { "orders", MemberOrders(s, id, 300) }, { "ledger", CwPoints(s) ? new List<Dictionary<string, object>>() : LedgerList(id, 500) },
            { "pointSource", CwPoints(s) ? "cw" : "own" }, { "edits", EditList("", id, 50) },
            { "pin", Query("SELECT UpdatedAt, UpdatedBy, FailCount, LockedUntil, MustChange FROM dbo.MemberPin WHERE CustomerId=@id", "@id", id).FirstOrDefault() } };
    }

    // บิลของสมาชิก พร้อมยอดสุทธิ (หักคืนสินค้า) และแต้มที่ได้ต่อบิล
    static List<Dictionary<string, object>> MemberOrders(Dictionary<string, string> s, int id, int top)
    {
        return Query("WITH " + OrdersCte() + @"
SELECT TOP (@top) Id, Order_Code Code, d Date, gross Gross, returned Returned, net Net,
       CASE WHEN @cw = 1 THEN ROUND(rec, 2)
            WHEN d >= @pstart AND net > 0 THEN FLOOR(net/@bpp) ELSE 0 END Points,
       CASE WHEN @cw = 1 THEN ROUND(pay, 2) ELSE 0 END PayPoints
FROM o WHERE cid=@id ORDER BY d DESC",
            "@top", top, "@id", id, "@pstart", DateTime.ParseExact(s["PointStartDate"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "@bpp", (decimal)Num(s, "BahtPerPoint"), "@cw", CwPoints(s) ? 1 : 0);
    }

    static object OrderItems(int orderId)
    {
        return Query(@"
SELECT oi.Qty, oi.Unit_Name Unit, oi.Product_Price Price, oi.Discount_Price Discount,
       ISNULL(NULLIF(oi.InvoiceItemName,''), p.Product_Name) Name
FROM " + Cw + "OrderItem oi LEFT JOIN " + Cw + @"Product p ON p.Id=oi.Product_Id
WHERE oi.Order_Id=@id ORDER BY oi.Id", "@id", orderId);
    }

    // ---------- points ----------

    static List<Dictionary<string, object>> LedgerList(int customerId, int top)
    {
        return Query(@"
SELECT TOP (@top) l.*, c.FullName, c.Customer_Code Code
FROM dbo.Ledger l LEFT JOIN " + Cw + @"Customer c ON c.Id=l.CustomerId
WHERE (@cid=0 OR l.CustomerId=@cid) ORDER BY l.Id DESC", "@top", top, "@cid", customerId);
    }

    const string CwPointMsg = "ระบบใช้แต้มจากโปรแกรม CW — ใช้แต้ม/ปรับแต้มที่โปรแกรม CW";

    static object Redeem(Dictionary<string, string> s, int id, Dictionary<string, object> b)
    {
        if (CwPoints(s)) throw new ApiError(400, CwPointMsg);
        int rewardId = Int(b, "rewardId");
        int qty = b.ContainsKey("qty") ? Int(b, "qty") : 1;
        string staff = Str(b, "staff");
        if (qty < 1 || qty > 100) throw new ApiError(400, "จำนวนไม่ถูกต้อง");
        if (staff.Length == 0) throw new ApiError(400, "กรุณาใส่ชื่อพนักงาน");
        var rw = Query("SELECT * FROM dbo.Reward WHERE Id=@id AND IsActive=1", "@id", rewardId);
        if (rw.Count == 0) throw new ApiError(400, "ไม่พบของรางวัล");
        int cost = Convert.ToInt32(rw[0]["Points"]) * qty;
        string desc = (string)rw[0]["Name"] + (qty > 1 ? " x" + qty : "");
        string note = Str(b, "note");
        if (note.Length > 0) desc += " — " + note;
        lock (WriteLock)
        {
            var m = GetMember(s, id);
            int bal = Convert.ToInt32(m["Points"]);
            if (bal < cost) throw new ApiError(400, "แต้มไม่พอ (มี " + bal + " ต้องใช้ " + cost + ")");
            Exec("INSERT dbo.Ledger(CustomerId,Kind,Points,RewardId,Description,Staff) VALUES(@c,'redeem',@p,@r,@d,@s)",
                "@c", id, "@p", -cost, "@r", rewardId, "@d", Trunc(desc, 300), "@s", Trunc(staff, 100));
        }
        return GetMember(s, id);
    }

    static object Adjust(Dictionary<string, string> s, int id, Dictionary<string, object> b)
    {
        if (CwPoints(s)) throw new ApiError(400, CwPointMsg);
        int pts = Int(b, "points");
        string reason = Str(b, "reason"), staff = Str(b, "staff");
        if (pts == 0) throw new ApiError(400, "จำนวนแต้มต้องไม่เป็น 0");
        if (reason.Length == 0) throw new ApiError(400, "กรุณาใส่เหตุผล");
        if (staff.Length == 0) throw new ApiError(400, "กรุณาใส่ชื่อพนักงาน");
        lock (WriteLock)
        {
            var m = GetMember(s, id);
            if (Convert.ToInt32(m["Points"]) + pts < 0) throw new ApiError(400, "หักแต้มเกินยอดคงเหลือ");
            Exec("INSERT dbo.Ledger(CustomerId,Kind,Points,Description,Staff) VALUES(@c,'adjust',@p,@d,@s)",
                "@c", id, "@p", pts, "@d", Trunc(reason, 300), "@s", Trunc(staff, 100));
        }
        return GetMember(s, id);
    }

    static object CancelLedger(int ledgerId, string staff)
    {
        if (staff.Length == 0) throw new ApiError(400, "กรุณาใส่ชื่อพนักงาน");
        int n = Exec("UPDATE dbo.Ledger SET IsCancelled=1, CancelledAt=GETDATE(), CancelledBy=@s WHERE Id=@id AND IsCancelled=0",
            "@id", ledgerId, "@s", Trunc(staff, 100));
        if (n == 0) throw new ApiError(400, "รายการนี้ถูกยกเลิกไปแล้ว");
        return new Dictionary<string, object> { { "ok", true } };
    }

    static object SaveReward(Dictionary<string, object> b)
    {
        string name = Str(b, "Name");
        int pts = Int(b, "Points");
        bool active = !b.ContainsKey("IsActive") || Convert.ToBoolean(b["IsActive"]);
        if (name.Length == 0) throw new ApiError(400, "กรุณาใส่ชื่อของรางวัล");
        if (pts <= 0) throw new ApiError(400, "แต้มต้องมากกว่า 0");
        int id = b.ContainsKey("Id") && b["Id"] != null ? Int(b, "Id") : 0;
        if (id > 0)
            Exec("UPDATE dbo.Reward SET Name=@n, Points=@p, Note=@x, IsActive=@a WHERE Id=@id",
                "@id", id, "@n", Trunc(name, 200), "@p", pts, "@x", Trunc(Str(b, "Note"), 500), "@a", active);
        else
            Exec("INSERT dbo.Reward(Name,Points,Note,IsActive) VALUES(@n,@p,@x,@a)",
                "@n", Trunc(name, 200), "@p", pts, "@x", Trunc(Str(b, "Note"), 500), "@a", active);
        return new Dictionary<string, object> { { "ok", true } };
    }

    static string Trunc(string s, int n) { return s.Length > n ? s.Substring(0, n) : s; }


    // ---------- แก้ไขข้อมูลลูกค้า → บันทึกลงโปรแกรม CW ----------
    // พนักงานแก้ได้ทันที / สมาชิกส่งคำขอจากหน้าเว็บ แล้วพนักงานอนุมัติ (ข้อมูลแพ้ยาสำคัญต่อความปลอดภัย จึงต้องผ่านเภสัชกร)
    // ทุกการเปลี่ยนแปลงเก็บประวัติไว้ในตาราง CustomerEdit (ค่าเดิม → ค่าใหม่ ใครทำ เมื่อไร)

    // คีย์ที่ใช้ในระบบ / คอลัมน์ใน CW Customer / ชื่อที่แสดง
    static readonly string[][] EditFields = {
        new[] { "Phone", "Phone", "เบอร์โทร" },
        new[] { "Email", "EmailAddress", "อีเมล" },
        new[] { "Address", "Address", "ที่อยู่" },
        new[] { "BirthDate", "BirthDate", "วันเกิด" },
        // แพ้ยาใน CW = รายชื่อยาสามัญ (จาก ListDrgGenName) คั่นด้วย , — CW ใช้เตือนตอนขายยา
        new[] { "Allergy", "DrgGenName_ItemCsv", "แพ้ยา" },
        new[] { "AllergyNote", "Intolerance", "อาการแพ้ / หมายเหตุ" },
        new[] { "Disease", "CongenitalDisease", "โรคประจำตัว" },
    };

    static string[] EditField(string key)
    {
        var f = EditFields.FirstOrDefault(x => x[0] == key);
        if (f == null) throw new ApiError(400, "แก้ไขช่อง " + key + " ไม่ได้");
        return f;
    }

    // ตรวจและจัดรูปแบบค่าใหม่ ให้ตรงกับชนิด/ความยาวคอลัมน์ใน CW
    static string NormalizeEdit(string key, string v, bool strict = true)
    {
        v = (v ?? "").Trim();
        switch (key)
        {
            case "Phone":
                v = Regex.Replace(v, "[^0-9]", "");
                if (v.Length != 0 && (v.Length < 9 || v.Length > 10)) throw new ApiError(400, "เบอร์โทรต้องเป็นตัวเลข 9-10 หลัก");
                return v;
            case "Email":
                if (v.Length > 50) throw new ApiError(400, "อีเมลยาวเกิน 50 ตัวอักษร");
                if (v.Length > 0 && !Regex.IsMatch(v, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new ApiError(400, "อีเมลไม่ถูกต้อง");
                return v;
            case "BirthDate":
                if (v.Length == 0) return v;
                DateTime d;
                if (!DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) || d.Year < 1900 || d > DateTime.Today)
                    throw new ApiError(400, "วันเกิดไม่ถูกต้อง");
                return d.ToString("yyyy-MM-dd");
            case "Address":
                if (v.Length > 500) throw new ApiError(400, "ที่อยู่ยาวเกินไป");
                return v;
            case "Allergy":
                return NormalizeDrugList(v, strict);
            default: // AllergyNote, Disease
                if (v.Length > 500) throw new ApiError(400, EditField(key)[2] + " ยาวเกินไป");
                return v;
        }
    }

    // รายชื่อยาสามัญของ CW (ใช้เลือกแพ้ยา) เก็บไว้ 10 นาที
    static List<string> DrugNameCache;
    static DateTime DrugNameCacheAt;
    static List<string> DrugNames()
    {
        if (DrugNameCache == null || DateTime.Now - DrugNameCacheAt > TimeSpan.FromMinutes(10))
        {
            DrugNameCache = Query("SELECT DISTINCT LTRIM(RTRIM(Name)) Name FROM " + Cw + "ListDrgGenName WHERE ISNULL(IsDelete,0)=0 AND LEN(Name) > 0 ORDER BY 1")
                .Select(r => (string)r["Name"]).ToList();
            DrugNameCacheAt = DateTime.Now;
        }
        return DrugNameCache;
    }

    // แพ้ยา: แยกชื่อด้วย , ; หรือขึ้นบรรทัดใหม่ แล้วรวมกลับเป็น "ยาA,ยาB" แบบที่ CW เก็บ
    // strict = ต้องตรงกับรายชื่อยาของ CW (พนักงานบันทึก/อนุมัติ) — ลูกค้าส่งคำขอพิมพ์อะไรก็ได้ ให้เภสัชกรเลือกชื่อยาตอนอนุมัติ
    static string NormalizeDrugList(string v, bool strict)
    {
        var names = new List<string>();
        var unknown = new List<string>();
        var master = strict ? DrugNames() : null;
        foreach (var part in Regex.Split(v, @"[,;\r\n]+"))
        {
            string x = part.Trim();
            if (x.Length == 0) continue;
            if (strict)
            {
                string hit = master.FirstOrDefault(m => string.Equals(m, x, StringComparison.OrdinalIgnoreCase));
                if (hit == null) { unknown.Add(x); continue; }
                x = hit;
            }
            if (!names.Any(n => string.Equals(n, x, StringComparison.OrdinalIgnoreCase))) names.Add(x);
        }
        if (unknown.Count > 0) throw new ApiError(400, "ไม่พบชื่อยาในรายชื่อยาของ CW: " + string.Join(", ", unknown) + " — เลือกชื่อจากรายการ");
        string res = string.Join(",", names);
        if (res.Length > 600) throw new ApiError(400, "รายการแพ้ยายาวเกินไป");
        return res;
    }

    static Dictionary<string, string> CustomerValues(int id)
    {
        var r = Query("SELECT Phone, EmailAddress, Address, BirthDate, CAST(DrgGenName_ItemCsv AS nvarchar(max)) DrgGenName_ItemCsv, Intolerance, CongenitalDisease FROM " + Cw + "Customer WHERE Id=@id AND ISNULL(IsDelete,0)=0", "@id", id).FirstOrDefault();
        if (r == null) throw new ApiError(404, "ไม่พบลูกค้าในโปรแกรม CW");
        var d = new Dictionary<string, string>();
        foreach (var f in EditFields)
        {
            object v = r[f[1]];
            d[f[0]] = v == null ? "" : f[0] == "BirthDate" ? ((string)v).Substring(0, 10) : Convert.ToString(v).Trim();
        }
        return d;
    }

    // เขียนลงตาราง Customer ของ CW (คอลัมน์จากรายการที่กำหนดไว้เท่านั้น)
    static void WriteCustomerField(int id, string key, string value)
    {
        var f = EditField(key);
        object p = value.Length == 0 ? (object)DBNull.Value
            : key == "BirthDate" ? (object)DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture) : value;
        int n = Exec("UPDATE " + Cw + "Customer SET [" + f[1] + "]=@v WHERE Id=@id", "@v", p, "@id", id);
        if (n != 1) throw new ApiError(404, "ไม่พบลูกค้าในโปรแกรม CW");
        Log("แก้ข้อมูลลูกค้า #" + id + " ใน CW: " + f[2]);
    }

    // พนักงานแก้ไขโดยตรง
    static object StaffEditCustomer(Dictionary<string, string> s, int id, Dictionary<string, object> b)
    {
        string staff = Str(b, "staff");
        if (staff.Length == 0) throw new ApiError(400, "กรุณาใส่ชื่อพนักงาน");
        var changes = new List<string[]>();
        lock (WriteLock)
        {
            var cur = CustomerValues(id);
            foreach (var f in EditFields)
            {
                if (!b.ContainsKey(f[0])) continue;
                string v = NormalizeEdit(f[0], Str(b, f[0]));
                if (v != cur[f[0]]) changes.Add(new[] { f[0], cur[f[0]], v });
            }
            foreach (var c in changes)
            {
                WriteCustomerField(id, c[0], c[2]);
                Exec(@"INSERT dbo.CustomerEdit(CustomerId,Field,OldValue,NewValue,Source,Status,DecidedAt,DecidedBy)
VALUES(@c,@f,@o,@n,'staff','applied',GETDATE(),@by)", "@c", id, "@f", c[0], "@o", c[1], "@n", c[2], "@by", Trunc(staff, 100));
                // คำขอที่ค้างอยู่ของช่องเดียวกันไม่ต้องใช้แล้ว
                Exec("UPDATE dbo.CustomerEdit SET Status='superseded', DecidedAt=GETDATE(), DecidedBy=@by WHERE CustomerId=@c AND Field=@f AND Status='pending'",
                    "@c", id, "@f", c[0], "@by", Trunc(staff, 100));
            }
        }
        if (changes.Count == 0) throw new ApiError(400, "ไม่มีข้อมูลที่เปลี่ยนแปลง");
        return GetMember(s, id);
    }

    static List<Dictionary<string, object>> EditList(string status, int customerId, int top)
    {
        var rows = Query(@"SELECT TOP (@top) e.*, c.FullName, c.Customer_Code Code FROM dbo.CustomerEdit e
LEFT JOIN " + Cw + @"Customer c ON c.Id=e.CustomerId
WHERE (@st='' OR e.Status=@st) AND (@cid=0 OR e.CustomerId=@cid) ORDER BY e.Id DESC",
            "@top", top, "@st", status, "@cid", customerId);
        foreach (var r in rows) r["Label"] = EditField((string)r["Field"])[2];
        return rows;
    }

    static object DecideEdit(int editId, bool approve, Dictionary<string, object> b)
    {
        string staff = Str(b, "staff");
        if (staff.Length == 0) throw new ApiError(400, "กรุณาใส่ชื่อพนักงาน");
        lock (WriteLock)
        {
            var e = Query("SELECT * FROM dbo.CustomerEdit WHERE Id=@id", "@id", editId).FirstOrDefault();
            if (e == null) throw new ApiError(404, "ไม่พบคำขอ");
            if ((string)e["Status"] != "pending") throw new ApiError(400, "คำขอนี้ดำเนินการไปแล้ว");
            int cid = Convert.ToInt32(e["CustomerId"]);
            string key = (string)e["Field"];
            if (approve)
            {
                // แพ้ยา: เภสัชกรเลือกชื่อยาที่ตรงกับรายชื่อยาของ CW แทนข้อความที่ลูกค้าพิมพ์มาได้ (ส่ง value)
                string v = NormalizeEdit(key, b.ContainsKey("value") ? Str(b, "value") : (string)e["NewValue"]);
                string old = CustomerValues(cid)[key];
                WriteCustomerField(cid, key, v);
                Exec("UPDATE dbo.CustomerEdit SET Status='approved', OldValue=@o, NewValue=@v, Note=CASE WHEN @v<>ISNULL(NewValue,'') THEN LEFT(ISNULL(NULLIF(Note,'')+N' · ','')+N'ลูกค้าเขียน: '+ISNULL(NewValue,''),300) ELSE Note END, DecidedAt=GETDATE(), DecidedBy=@by WHERE Id=@id",
                    "@id", editId, "@o", old, "@v", v, "@by", Trunc(staff, 100));
            }
            else
                Exec("UPDATE dbo.CustomerEdit SET Status='rejected', Note=@n, DecidedAt=GETDATE(), DecidedBy=@by WHERE Id=@id",
                    "@id", editId, "@n", Trunc(Str(b, "note"), 300), "@by", Trunc(staff, 100));
        }
        return new Dictionary<string, object> { { "ok", true } };
    }

    // สมาชิกส่งคำขอแก้ไขจากหน้าเว็บ (รอพนักงานอนุมัติ)
    static object MyEditRequest(Dictionary<string, string> s, HttpListenerContext ctx, Dictionary<string, object> b)
    {
        int id = SessionCustomer(ctx);
        string note = Trunc(Str(b, "note"), 300);
        var cur = CustomerValues(id);
        var changes = new List<string[]>();
        foreach (var f in EditFields)
        {
            if (!b.ContainsKey(f[0])) continue;
            string v = NormalizeEdit(f[0], Str(b, f[0]), false);
            if (v != cur[f[0]]) changes.Add(new[] { f[0], cur[f[0]], v });
        }
        if (changes.Count == 0) throw new ApiError(400, "ไม่มีข้อมูลที่เปลี่ยนแปลง");
        lock (WriteLock)
        {
            if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM dbo.CustomerEdit WHERE CustomerId=@c AND Status='pending' AND CreatedAt >= DATEADD(day,-1,GETDATE())", "@c", id)) >= 20)
                throw new ApiError(429, "ส่งคำขอมากเกินไป กรุณารอพนักงานตรวจสอบ");
            foreach (var c in changes)
            {
                // ส่งช่องเดิมซ้ำ ให้ใช้ค่าล่าสุด
                Exec("UPDATE dbo.CustomerEdit SET Status='superseded', DecidedAt=GETDATE() WHERE CustomerId=@c AND Field=@f AND Status='pending'", "@c", id, "@f", c[0]);
                Exec(@"INSERT dbo.CustomerEdit(CustomerId,Field,OldValue,NewValue,Source,Status,Note)
VALUES(@c,@f,@o,@n,'member','pending',@note)", "@c", id, "@f", c[0], "@o", c[1], "@n", c[2], "@note", note);
            }
        }
        Log("สมาชิก #" + id + " ส่งคำขอแก้ไขข้อมูล " + changes.Count + " รายการ");
        return MyProfile(s, id);
    }

    // ---------- คำนวณแต้มย้อนหลังตามอัตราของ CW (ดูอย่างเดียว ยังไม่เขียนลง CW) ----------
    // ใช้ตั้งค่าแต้มใน CW (ช่วงวันที่ + อัตราได้แต้ม) คิดจากยอดสุทธิแต่ละบิล (หักคืนสินค้าแล้ว) ปัดเศษแต้มลง
    static object PointRecalc(Dictionary<string, string> s)
    {
        var c = CwPointConfig();
        if (!(c["ok"] is bool && (bool)c["ok"])) throw new ApiError(400, "อ่านตั้งค่าแต้มของ CW ไม่ได้");
        double price = Convert.ToDouble(c["RecPrice"]), pts = Convert.ToDouble(c["RecPoint"]);
        if (price <= 0 || pts <= 0) throw new ApiError(400, "ยังไม่ได้ตั้งอัตราได้แต้มในโปรแกรม CW");
        DateTime begin = c["Begin"] == null ? new DateTime(1900, 1, 1) : DateTime.ParseExact((string)c["Begin"], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTime end = c["End"] == null ? DateTime.MaxValue.Date : DateTime.ParseExact((string)c["End"], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = Query("WITH " + OrdersCte() + @"
SELECT o.cid Id, c.Customer_Code Code, c.FullName, COUNT(*) Bills, SUM(o.net) Net,
       SUM(FLOOR(o.net / @price) * @pts) Calc, ISNULL(MAX(c.Rt_Point_Value),0) InCw
FROM o JOIN " + Cw + @"Customer c ON c.Id=o.cid
WHERE o.cid > 0 AND o.net > 0 AND o.d >= @b AND o.d < @e AND ISNULL(c.IsDelete,0)=0" +
            (s["IncludeWholesale"] == "1" ? "" : " AND ISNULL(c.IsWholesaleCustomer,0)=0") + @"
GROUP BY o.cid, c.Customer_Code, c.FullName ORDER BY Calc DESC",
            "@price", (decimal)price, "@pts", pts, "@b", begin, "@e", end);
        return new Dictionary<string, object> {
            { "config", c }, { "rows", rows },
            { "totalCalc", rows.Sum(r => Convert.ToDouble(r["Calc"])) },
            { "totalCurrent", rows.Sum(r => Convert.ToDouble(r["InCw"])) } };
    }

    // ---------- dashboard / self check ----------

    static object Dashboard(Dictionary<string, string> s)
    {
        var all = MemberRows(s, "");
        var now = DateTime.Now;
        var month = new DateTime(now.Year, now.Month, 1);
        Func<object, DateTime?> dt = v => v == null ? (DateTime?)null : DateTime.Parse((string)v, CultureInfo.InvariantCulture);

        var sales = Query("WITH " + OrdersCte() + @"
SELECT COUNT(*) Bills, ISNULL(SUM(net),0) Total,
       SUM(CASE WHEN cid>0 THEN 1 ELSE 0 END) MemberBills, ISNULL(SUM(CASE WHEN cid>0 THEN net ELSE 0 END),0) MemberTotal
FROM o WHERE d >= @m", "@m", month)[0];

        var tiers = new Dictionary<string, int> { { "member", 0 }, { "silver", 0 }, { "gold", 0 }, { "platinum", 0 } };
        foreach (var r in all) tiers[(string)r["Tier"]]++;

        // ใช้แต้มเดือนนี้: แบบ CW = บิลที่ใช้แต้มเป็นส่วนลด / แบบเดิม = การแลกในระบบสมาชิก
        var redeemMonth = CwPoints(s)
            ? Query("WITH " + OrdersCte() + " SELECT COUNT(*) N, ISNULL(SUM(pay),0) Points FROM o WHERE pay > 0 AND d >= @m", "@m", month)[0]
            : Query(@"SELECT COUNT(*) N, ISNULL(-SUM(Points),0) Points FROM dbo.Ledger
            WHERE Kind='redeem' AND IsCancelled=0 AND CreatedAt >= @m", "@m", month)[0];
        var recent = CwPoints(s)
            ? Query("WITH " + OrdersCte() + @" SELECT TOP 10 o.d CreatedAt, o.cid CustomerId, c.FullName, c.Customer_Code Code,
  CASE WHEN o.pay > 0 THEN 'redeem' ELSE 'earn' END Kind, ROUND(o.rec - o.pay, 2) Points,
  o.Order_Code + CASE WHEN o.pay > 0 THEN N' · ใช้ ' + CAST(ROUND(o.pay,2) AS nvarchar(20)) + N' แต้ม' ELSE N'' END Description,
  CAST(0 AS bit) IsCancelled, NULL Staff
FROM o JOIN " + Cw + "Customer c ON c.Id=o.cid WHERE o.cid > 0 AND (o.rec <> 0 OR o.pay <> 0) ORDER BY o.d DESC")
            : LedgerList(0, 10);

        return new Dictionary<string, object> {
            { "members", all.Count },
            { "active90", all.Count(r => { var d = dt(r["LastVisit"]); return d.HasValue && d.Value >= now.AddDays(-90); }) },
            { "newThisMonth", all.Count(r => { var d = dt(r["Registered"]); return d.HasValue && d.Value >= month; }) },
            { "pointsOutstanding", Math.Round(all.Sum(r => Math.Max(0, Convert.ToDouble(r["Points"]))), 2) },
            { "tiers", tiers },
            { "sales", sales },
            { "redeemMonth", redeemMonth },
            { "birthdays", all.Where(r => { var d = dt(r["BirthDate"]); return d.HasValue && d.Value.Month == now.Month && d.Value.Year > 1900; })
                              .OrderBy(r => dt(r["BirthDate"]).Value.Day).ToList() },
            { "top", all.OrderByDescending(r => Convert.ToDouble(r["Spend365"])).Take(10).ToList() },
            { "dormant", all.Where(r => { var d = dt(r["LastVisit"]); return d.HasValue && d.Value < now.AddDays(-60) && Convert.ToInt32(r["Visits"]) >= 2; })
                            .OrderByDescending(r => Convert.ToDouble(r["Spend365"])).Take(10).ToList() },
            { "recent", recent },
        };
    }

    static object SelfCheck(Dictionary<string, string> s, string phone)
    {
        string digits = Regex.Replace(phone, "[^0-9]", "");
        if (digits.Length < 9) throw new ApiError(400, "กรุณาใส่เบอร์โทรให้ครบ");
        var rows = MemberRows(s, " AND REPLACE(REPLACE(c.Phone,'-',''),' ','') = @d", "@d", digits);
        if (rows.Count == 0) throw new ApiError(404, "ไม่พบเบอร์นี้ในระบบสมาชิก");
        var r = rows[0];
        return new Dictionary<string, object> {
            { "name", MaskName((string)r["FullName"]) }, { "points", r["Points"] }, { "tier", r["Tier"] }, { "benefit", r["Benefit"] },
            { "nextTier", r["NextTier"] }, { "toNextTier", r["ToNextTier"] }, { "spend365", r["Spend365"] },
            { "rewards", CwPoints(s) ? new List<Dictionary<string, object>>() : Query("SELECT Name, Points FROM dbo.Reward WHERE IsActive=1 ORDER BY Points") } };
    }

    static string MaskName(string name)
    {
        var parts = (name ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[0] + " " + parts[1].Substring(0, 1) + "." : (name ?? "");
    }

    // ---------- วิธีใช้ยา (ดูได้ทุกคน ไม่ต้องเป็นสมาชิก) ----------
    // ข้อมูลจากฉลากยาใน CW (ProductLabel: สรรพคุณ/วิธีใช้/คำเตือน) ถ้าไม่มีใช้รายละเอียดสินค้า (Product_Using/Product_Warning)

    static string DrugInfoSql(string where, string top, string extraCols = "")
    {
        return @"
SELECT " + top + @" p.Id, " + extraCols + @" p.Product_Name Name, NULLIF(p.Product_NameEng,'') NameEng,
  STUFF((SELECT ', ' + g.DrugGenericName FROM " + Cw + @"ProductListDrgGenName g
         WHERE g.Product_Id=p.Id AND ISNULL(g.IsDelete,0)=0 FOR XML PATH(''), TYPE).value('.','nvarchar(max)'),1,2,'') Generic,
  NULLIF(LTRIM(RTRIM(pl.Prod_Property)),'') Property,
  COALESCE(NULLIF(LTRIM(RTRIM(pl.Prod_Using)),''), NULLIF(LTRIM(RTRIM(CAST(p.Product_Using AS nvarchar(max)))),'')) Using,
  COALESCE(NULLIF(LTRIM(RTRIM(pl.Prod_Warnign)),''), NULLIF(LTRIM(RTRIM(CAST(p.Product_Warning AS nvarchar(max)))),'')) Warning
FROM " + Cw + @"Product p
OUTER APPLY (SELECT TOP 1 * FROM " + Cw + @"ProductLabel x WHERE x.Product_Id=p.Id ORDER BY x.Id DESC) pl
WHERE ISNULL(p.IsDelete,0)=0 " + where;
    }


    static object DrugDetail(int id)
    {
        var rows = Query(DrugInfoSql(" AND p.Id=@id", "TOP 1"), "@id", id);
        if (rows.Count == 0) throw new ApiError(404, "ไม่พบข้อมูลยา");
        return rows[0];
    }

    // ---------- ประวัติการจ่ายยา (ลูกค้าดูเองด้วย เบอร์โทร + PIN ที่พนักงานตั้งให้) ----------

    const int PinLockAfter = 5, PinBlockAfter = 10;

    static string HashPin(string pin, string salt)
    {
        using (var kdf = new System.Security.Cryptography.Rfc2898DeriveBytes(pin, Convert.FromBase64String(salt), 20000))
            return Convert.ToBase64String(kdf.GetBytes(32));
    }

    static void ValidateNewPin(string pin)
    {
        if (!Regex.IsMatch(pin, "^[0-9]{4,6}$")) throw new ApiError(400, "PIN ต้องเป็นตัวเลข 4-6 หลัก");
        if (Regex.IsMatch(pin, @"^(\d)\1+$") || "0123456789".Contains(pin) || "9876543210".Contains(pin))
            throw new ApiError(400, "PIN เดาง่ายเกินไป (เช่น 1111, 1234) กรุณาเลือกใหม่");
    }

    // mustChange = PIN ชั่วคราวที่พนักงานสุ่มให้ ลูกค้าต้องตั้งใหม่ก่อนดูข้อมูล
    static void StorePin(int id, string pin, string by, bool mustChange = false)
    {
        var saltBytes = new byte[16];
        using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(saltBytes);
        string salt = Convert.ToBase64String(saltBytes);
        Exec(@"DELETE FROM dbo.MemberPin WHERE CustomerId=@id;
INSERT dbo.MemberPin(CustomerId,PinHash,Salt,UpdatedBy,MustChange) VALUES(@id,@h,@s,@by,@mc)",
            "@id", id, "@h", HashPin(pin, salt), "@s", salt, "@by", Trunc(by, 100), "@mc", mustChange);
    }

    // PIN ชั่วคราว 6 หลักแบบสุ่ม (ไม่ใช่แบบเดาง่าย) — แสดงให้พนักงานครั้งเดียว ไม่เก็บตัวเลขจริง
    static string RandomPin()
    {
        var b = new byte[4];
        using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
            while (true)
            {
                rng.GetBytes(b);
                string pin = (BitConverter.ToUInt32(b, 0) % 1000000).ToString("D6");
                try { ValidateNewPin(pin); return pin; } catch (ApiError) { }
            }
    }

    static object TempPin(int id, Dictionary<string, object> b)
    {
        string staff = Str(b, "staff");
        if (staff.Length == 0) throw new ApiError(400, "กรุณาใส่ชื่อพนักงาน");
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM " + Cw + "Customer WHERE Id=@id AND ISNULL(IsDelete,0)=0", "@id", id)) == 0) throw new ApiError(404, "ไม่พบสมาชิก");
        EndSessions(id);
        string pin = RandomPin();
        StorePin(id, pin, staff + " (PIN ชั่วคราว)", true);
        Log("สร้าง PIN ชั่วคราวให้สมาชิก #" + id + " โดย " + staff);
        return new Dictionary<string, object> { { "pin", pin } };
    }

    // สร้าง PIN ชั่วคราวให้สมาชิกทุกคนที่มีเบอร์โทรแต่ยังไม่มี PIN
    static object TempPinAll(Dictionary<string, string> s, Dictionary<string, object> b)
    {
        string staff = Str(b, "staff");
        if (staff.Length == 0) throw new ApiError(400, "กรุณาใส่ชื่อพนักงาน");
        var list = new List<Dictionary<string, object>>();
        lock (WriteLock)
            foreach (var r in Query(@"SELECT c.Id, c.Customer_Code Code, c.FullName, c.Phone FROM " + Cw + @"Customer c
WHERE ISNULL(c.IsDelete,0)=0 AND LEN(REPLACE(REPLACE(ISNULL(c.Phone,''),'-',''),' ','')) >= 9" +
                (s["IncludeWholesale"] == "1" ? "" : " AND ISNULL(c.IsWholesaleCustomer,0)=0") + @"
  AND NOT EXISTS(SELECT 1 FROM dbo.MemberPin m WHERE m.CustomerId=c.Id) ORDER BY c.Id"))
            {
                string pin = RandomPin();
                StorePin(Convert.ToInt32(r["Id"]), pin, staff + " (PIN ชั่วคราว)", true);
                r["Pin"] = pin;
                list.Add(r);
            }
        Log("สร้าง PIN ชั่วคราว " + list.Count + " คน โดย " + staff);
        return list;
    }

    static object SetMemberPin(int id, Dictionary<string, object> b)
    {
        string pin = Str(b, "pin"), staff = Str(b, "staff");
        if (staff.Length == 0) throw new ApiError(400, "กรุณาใส่ชื่อพนักงาน");
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM " + Cw + "Customer WHERE Id=@id", "@id", id)) == 0) throw new ApiError(404, "ไม่พบสมาชิก");
        EndSessions(id); // ตั้ง/ยกเลิก PIN แล้ว ให้ออกจากระบบทุกเครื่อง
        if (pin.Length == 0)
        {
            Exec("DELETE FROM dbo.MemberPin WHERE CustomerId=@id", "@id", id);
            return new Dictionary<string, object> { { "ok", true } };
        }
        ValidateNewPin(pin);
        StorePin(id, pin, staff);
        return new Dictionary<string, object> { { "ok", true } };
    }

    // ตรวจเบอร์ + PIN → คืนรหัสลูกค้า (ใส่ผิดนับครั้ง/ล็อกเหมือนเดิม)
    static int VerifyMemberPin(string phone, string pin)
    {
        string digits = Regex.Replace(phone, "[^0-9]", "");
        if (digits.Length < 9 || pin.Length == 0) throw new ApiError(400, "กรุณาใส่เบอร์โทรและ PIN");
        // ข้อความเดียวกันทุกกรณี — ไม่บอกว่าเบอร์นี้มีในระบบ/มี PIN หรือไม่
        const string wrong = "เบอร์โทรหรือ PIN ไม่ถูกต้อง (ถ้ายังไม่มี PIN ติดต่อพนักงานเพื่อตั้ง PIN)";
        // เบอร์เดียวอาจมีหลายคนในครอบครัว — หาคนที่ PIN ตรง
        var cands = Query(@"SELECT c.Id, m.PinHash, m.Salt, m.FailCount, m.LockedUntil
FROM " + Cw + @"Customer c JOIN dbo.MemberPin m ON m.CustomerId=c.Id
WHERE ISNULL(c.IsDelete,0)=0 AND REPLACE(REPLACE(c.Phone,'-',''),' ','') = @d", "@d", digits);
        if (cands.Count == 0) throw new ApiError(403, wrong);
        lock (WriteLock)
        {
            foreach (var c in cands)
            {
                if (Convert.ToInt32(c["FailCount"]) >= PinBlockAfter) continue;
                if (c["LockedUntil"] != null && DateTime.Parse((string)c["LockedUntil"], CultureInfo.InvariantCulture) > DateTime.Now) continue;
                if (HashPin(pin, (string)c["Salt"]) == (string)c["PinHash"])
                {
                    Exec("UPDATE dbo.MemberPin SET FailCount=0, LockedUntil=NULL WHERE CustomerId=@id", "@id", c["Id"]);
                    return Convert.ToInt32(c["Id"]);
                }
            }
            foreach (var c in cands)
                Exec(@"UPDATE dbo.MemberPin SET FailCount=FailCount+1,
  LockedUntil = CASE WHEN (FailCount+1) % @lock = 0 THEN DATEADD(minute,30,GETDATE()) ELSE LockedUntil END
WHERE CustomerId=@id", "@id", c["Id"], "@lock", PinLockAfter);
            int fails = cands.Min(c => Convert.ToInt32(c["FailCount"])) + 1;
            if (fails >= PinBlockAfter) throw new ApiError(403, "ใส่ PIN ผิดเกินกำหนด — ติดต่อพนักงานเพื่อตั้ง PIN ใหม่");
            bool locked = cands.All(c => c["LockedUntil"] != null &&
                DateTime.Parse((string)c["LockedUntil"], CultureInfo.InvariantCulture) > DateTime.Now) || fails % PinLockAfter == 0;
            throw new ApiError(403, locked ? "ใส่ PIN ผิดหลายครั้ง กรุณารอ 30 นาที หรือติดต่อพนักงาน" : wrong);
        }
    }

    // ---------- พื้นที่สมาชิก: เข้าด้วยเบอร์ + PIN แล้วได้ token ใช้ต่อ 15 นาที (นับใหม่ทุกครั้งที่ใช้งาน) ----------

    class MemberSession { public int CustomerId; public DateTime Expires; public bool MustChange; }
    static readonly Dictionary<string, MemberSession> Sessions = new Dictionary<string, MemberSession>();
    static readonly TimeSpan SessionIdle = TimeSpan.FromMinutes(15);

    static string NewToken()
    {
        var b = new byte[32];
        using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(b);
        return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
    }

    static string BearerToken(HttpListenerContext ctx)
    {
        string auth = ctx.Request.Headers["Authorization"] ?? "";
        return auth.StartsWith("Bearer ") ? auth.Substring(7).Trim() : "";
    }

    // allowMustChange = ใช้ได้แม้ยังเป็น PIN ชั่วคราว (เฉพาะหน้าเปลี่ยน PIN)
    static int SessionCustomer(HttpListenerContext ctx, bool allowMustChange = false)
    {
        string token = BearerToken(ctx);
        lock (Sessions)
        {
            foreach (var k in Sessions.Where(kv => kv.Value.Expires < DateTime.Now).Select(kv => kv.Key).ToList()) Sessions.Remove(k);
            MemberSession s;
            if (token.Length == 0 || !Sessions.TryGetValue(token, out s)) throw new ApiError(401, "หมดเวลาการใช้งาน กรุณาเข้าสู่ระบบใหม่");
            s.Expires = DateTime.Now + SessionIdle;
            if (s.MustChange && !allowMustChange) throw new ApiError(409, "กรุณาตั้ง PIN ใหม่ก่อนใช้งาน");
            return s.CustomerId;
        }
    }

    static void EndSessions(int customerId)
    {
        lock (Sessions)
            foreach (var k in Sessions.Where(kv => kv.Value.CustomerId == customerId).Select(kv => kv.Key).ToList()) Sessions.Remove(k);
    }

    static object MyLogin(Dictionary<string, string> s, Dictionary<string, object> b)
    {
        int id = VerifyMemberPin(Str(b, "phone"), Str(b, "pin"));
        string token = NewToken();
        bool must = Convert.ToBoolean(Scalar("SELECT MustChange FROM dbo.MemberPin WHERE CustomerId=@id", "@id", id) ?? false);
        lock (Sessions) Sessions[token] = new MemberSession { CustomerId = id, Expires = DateTime.Now + SessionIdle, MustChange = must };
        if (must) // PIN ชั่วคราว: ยังไม่ให้เห็นข้อมูล ให้ตั้ง PIN ใหม่ก่อน
        {
            var row = Query("SELECT FullName FROM " + Cw + "Customer WHERE Id=@id", "@id", id).FirstOrDefault();
            return new Dictionary<string, object> { { "mustChange", true }, { "token", token },
                { "name", MaskName(row == null ? "" : (string)row["FullName"]) } };
        }
        var res = MyProfile(s, id);
        res["token"] = token;
        return res;
    }

    static Dictionary<string, object> MyProfile(Dictionary<string, string> s, int id)
    {
        var m = MemberRows(s, " AND c.Id=@id", "@id", id).FirstOrDefault();
        if (m == null) throw new ApiError(401, "ไม่พบข้อมูลสมาชิก กรุณาติดต่อพนักงาน");
        var res = new Dictionary<string, object> { { "name", MaskName((string)m["FullName"]) } };
        foreach (var k in new[] { "Points", "Earned", "Redeemed", "Adjusted", "Tier", "Benefit", "NextTier", "ToNextTier",
                                   "NextTierMin", "TierMin", "Spend365", "Visits", "Registered", "Allergy", "Disease" })
            res[k] = m[k];
        DateTime bd;
        res["BirthdayMonth"] = m["BirthDate"] != null && DateTime.TryParse((string)m["BirthDate"], CultureInfo.InvariantCulture, DateTimeStyles.None, out bd)
            && bd.Year > 1900 && bd.Month == DateTime.Now.Month;
        res["Contact"] = CustomerValues(id);
        res["PendingEdits"] = Query("SELECT Field, NewValue, CreatedAt FROM dbo.CustomerEdit WHERE CustomerId=@id AND Status='pending' ORDER BY Id", "@id", id);
        res["RejectedEdits"] = Query(@"SELECT Field, NewValue, Note, DecidedAt FROM dbo.CustomerEdit
WHERE CustomerId=@id AND Status='rejected' AND DecidedAt >= DATEADD(day,-14,GETDATE()) ORDER BY Id DESC", "@id", id);
        res["PointInfo"] = PointRuleText(s);
        res["PointSource"] = CwPoints(s) ? "cw" : "own";
        var bills = MemberOrders(s, id, 100);
        res["Bills"] = bills;
        res["PointHistory"] = PointHistory(s, id, bills, false);
        res["Items"] = Query(@"
SELECT TOP 500 o.Id OrderId, o.Date_Order Date, oi.Product_Id ProductId,
  ISNULL(NULLIF(oi.InvoiceItemName,''), p.Product_Name) Name, oi.Qty, oi.Unit_Name Unit,
  CASE WHEN EXISTS(SELECT 1 FROM " + Cw + "ProductBackItem bi JOIN " + Cw + @"ProductBack pb ON pb.Id=bi.ProductBack_Id
       WHERE bi.OrderItem_Id=oi.Id AND ISNULL(pb.IsCancel,0)=0 AND pb.ProductBack_Status=2) THEN 1 ELSE 0 END Returned,
  CASE WHEN LEN(CAST(ISNULL(p.Product_Using,'') AS nvarchar(max))) > 0 OR EXISTS(SELECT 1 FROM " + Cw + @"ProductLabel pl
       WHERE pl.Product_Id=oi.Product_Id AND (LEN(pl.Prod_Using) > 0 OR LEN(pl.Prod_Property) > 0)) THEN 1 ELSE 0 END HasInfo
FROM " + Cw + "[Order] o JOIN " + Cw + "OrderItem oi ON oi.Order_Id=o.Id LEFT JOIN " + Cw + @"Product p ON p.Id=oi.Product_Id
WHERE o.Customer_Id=@cid AND ISNULL(o.IsOrderCancel,0)=0 AND o.Order_Status=2 AND ISNULL(o.IsDelete,0)=0
ORDER BY o.Date_Order DESC, oi.Id", "@cid", id);
        // ของรางวัลในระบบสมาชิกใช้เฉพาะแบบคิดแต้มเอง — แบบ CW ใช้แต้มแทนเงินสดที่หน้าขาย (ไม่ส่งหมายเหตุ เป็นโน้ตพนักงาน)
        res["Rewards"] = CwPoints(s) ? new List<Dictionary<string, object>>()
            : Query("SELECT Name, Points FROM dbo.Reward WHERE IsActive=1 ORDER BY Points");
        return res;
    }

    // คำอธิบายวิธีได้แต้ม สำหรับแสดงให้ลูกค้า
    static string PointRuleText(Dictionary<string, string> s)
    {
        if (!CwPoints(s))
            return "ซื้อครบทุก " + Num(s, "BahtPerPoint").ToString("#,0.##") + " บาท = 1 แต้ม";
        var c = CwPointConfig();
        if (!(c["ok"] is bool && (bool)c["ok"]) || !(bool)c["Active"] || !(bool)c["RecActive"]) return "สะสมแต้มทุกการซื้อที่ร้าน";
        double price = Convert.ToDouble(c["RecPrice"]), pts = Convert.ToDouble(c["RecPoint"]);
        string text = price > 0 && pts > 0 ? "ซื้อทุก " + price.ToString("#,0.##") + " บาท ได้ " + pts.ToString("#,0.##") + " แต้ม" : "สะสมแต้มทุกการซื้อที่ร้าน";
        double usePts = Convert.ToDouble(c["PayPoint"]), useBaht = Convert.ToDouble(c["PayPrice"]);
        if ((bool)c["PayActive"] && usePts > 0 && useBaht > 0)
            text += " · ใช้ " + usePts.ToString("#,0.##") + " แต้ม แทนเงิน " + useBaht.ToString("#,0.##") + " บาท";
        return text;
    }

    static object MyChangePin(HttpListenerContext ctx, Dictionary<string, object> b)
    {
        int id = SessionCustomer(ctx, true);
        string current = Str(b, "current"), fresh = Str(b, "pin");
        var row = Query("SELECT PinHash, Salt FROM dbo.MemberPin WHERE CustomerId=@id", "@id", id).FirstOrDefault();
        if (row == null || HashPin(current, (string)row["Salt"]) != (string)row["PinHash"])
            throw new ApiError(403, "PIN ปัจจุบันไม่ถูกต้อง");
        ValidateNewPin(fresh);
        if (fresh == current) throw new ApiError(400, "PIN ใหม่ต้องไม่ซ้ำกับ PIN เดิม");
        StorePin(id, fresh, "ลูกค้าเปลี่ยนเอง");
        // ออกจากระบบเครื่องอื่นที่อาจค้างอยู่ คงไว้แค่เครื่องนี้
        string mine = BearerToken(ctx);
        lock (Sessions)
        {
            foreach (var k in Sessions.Where(kv => kv.Value.CustomerId == id && kv.Key != mine).Select(kv => kv.Key).ToList()) Sessions.Remove(k);
            MemberSession cur;
            if (Sessions.TryGetValue(mine, out cur)) cur.MustChange = false; // ตั้ง PIN ของตัวเองแล้ว ใช้งานต่อได้
        }
        Log("สมาชิก #" + id + " เปลี่ยน PIN เอง");
        return new Dictionary<string, object> { { "ok", true } };
    }

    static object MyLogout(HttpListenerContext ctx)
    {
        lock (Sessions) Sessions.Remove(BearerToken(ctx));
        return new Dictionary<string, object> { { "ok", true } };
    }

    // ---------- หน้าสมาชิกออนไลน์ (Cloudflare Worker + D1) ให้ลูกค้าดูได้แม้คอมร้านปิด ----------
    // ทุก 5 นาที: รับคำขอแก้ไข/เปลี่ยน PIN ที่ลูกค้าทำออนไลน์ → แล้วส่งข้อมูลสมาชิกที่มี PIN ขึ้นไป (ส่งเฉพาะคนที่ข้อมูลเปลี่ยน)
    // สมาชิกที่ไม่มี PIN ไม่ถูกส่งขึ้นออนไลน์

    static readonly object CloudLock = new object();
    static string CloudLastOk, CloudLastError;
    static int CloudMembers, CloudLastPushed, CloudNotified;

    // หาเรื่องที่ต้องแจ้งลูกค้าทาง LINE: บิลใหม่ที่ได้/ใช้แต้ม และอวยพรเดือนเกิด (ปีละครั้ง)
    // ครั้งแรกที่เจอสมาชิก แค่จำบิลล่าสุดไว้ ไม่แจ้งย้อนหลัง · ปิดการแจ้งเตือนอยู่ก็ยังเลื่อนสถานะ (เปิดทีหลังไม่ส่งของเก่าทีเดียว)
    static void CollectNotifications(Dictionary<string, string> s, int cid, Dictionary<string, object> data,
        Dictionary<int, int[]> state, List<object> notify, Dictionary<int, int[]> updates)
    {
        var bills = (List<Dictionary<string, object>>)data["Bills"];
        int maxId = bills.Count > 0 ? bills.Max(b => Convert.ToInt32(b["Id"])) : 0;
        int year = DateTime.Now.Year;
        bool bday = data["BirthdayMonth"] is bool && (bool)data["BirthdayMonth"];
        int[] st;
        if (!state.TryGetValue(cid, out st))
        {
            updates[cid] = new[] { maxId, bday ? year : 0 };
            return;
        }
        int last = st[0], bYear = st[1];
        if (s["LineNotifyPoints"] == "1")
            foreach (var b in bills.Where(b => Convert.ToInt32(b["Id"]) > last).OrderBy(b => Convert.ToInt32(b["Id"])))
            {
                double rec = Convert.ToDouble(b["Points"]), pay = Convert.ToDouble(b["PayPoints"]);
                DateTime d;
                if ((rec == 0 && pay == 0) || !DateTime.TryParse((string)b["Date"], CultureInfo.InvariantCulture, DateTimeStyles.None, out d) || d < DateTime.Now.AddDays(-3)) continue;
                notify.Add(new Dictionary<string, object> { { "cid", cid }, { "kind", "points" }, { "code", b["Code"] }, { "net", b["Net"] },
                    { "rec", rec }, { "pay", pay }, { "balance", data["Points"] } });
            }
        if (bday && bYear != year && s["LineNotifyBirthday"] == "1")
            notify.Add(new Dictionary<string, object> { { "cid", cid }, { "kind", "birthday" }, { "name", data["name"] } });
        int newLast = Math.Max(last, maxId), newYear = bday ? year : bYear;
        if (newLast != last || newYear != bYear) updates[cid] = new[] { newLast, newYear };
    }

    static void CloudLoop()
    {
        Thread.Sleep(20000);
        while (true)
        {
            try { CloudSync(false); }
            catch (Exception e) { CloudLastError = e.Message; }
            Thread.Sleep(TimeSpan.FromMinutes(5));
        }
    }

    static object CloudStatus()
    {
        var s = Settings();
        return new Dictionary<string, object> {
            { "enabled", s["CloudUrl"].Length > 0 && s["CloudKey"].Length > 0 }, { "url", s["CloudUrl"] },
            { "lastOk", CloudLastOk }, { "lastError", CloudLastError }, { "members", CloudMembers }, { "lastPushed", CloudLastPushed },
            { "notified", CloudNotified } };
    }

    // สร้างคีย์ใหม่ (ต้องนำไปใส่เป็น Secret SYNC_KEY ใน Cloudflare Worker)
    static object CloudNewKey()
    {
        var b = new byte[32];
        using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(b);
        string key = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        Exec("UPDATE dbo.Setting SET [Value]=@v WHERE [Key]='CloudKey'; IF @@ROWCOUNT=0 INSERT dbo.Setting VALUES('CloudKey',@v); DELETE FROM dbo.CloudPushed", "@v", key);
        Log("สร้างคีย์ซิงก์ออนไลน์ใหม่");
        return new Dictionary<string, object> { { "key", key } };
    }

    static object CloudCall(string url, string key, object body)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768;
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = body == null ? "GET" : "POST";
        req.Headers["Authorization"] = "Bearer " + key;
        req.UserAgent = "YaMumMember/" + AppVersion;
        req.Timeout = req.ReadWriteTimeout = 120000;
        if (body != null)
        {
            byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(body));
            req.ContentType = "application/json; charset=utf-8";
            req.ContentLength = data.Length;
            using (var st = req.GetRequestStream()) st.Write(data, 0, data.Length);
        }
        try
        {
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                return Json.DeserializeObject(sr.ReadToEnd());
        }
        catch (WebException e)
        {
            string msg = e.Message;
            var res = e.Response as HttpWebResponse;
            if (res != null)
            {
                using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) msg = sr.ReadToEnd();
                if ((int)res.StatusCode == 401) msg = "คีย์ซิงก์ไม่ตรงกับใน Cloudflare (SYNC_KEY)";
                else if (msg.Length > 200 || msg.StartsWith("<")) msg = "HTTP " + (int)res.StatusCode;
            }
            throw new Exception("ซิงก์ออนไลน์ไม่สำเร็จ: " + msg);
        }
    }

    static string Sha256Hex(string s)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "");
    }

    // รับคำขอแก้ไขจากหน้าออนไลน์ → เป็นคำขอรออนุมัติ (เหมือนส่งจากหน้าร้าน)
    static void ImportOnlineEdit(int cid, Dictionary<string, object> d)
    {
        string key = Convert.ToString(d["Field"]);
        EditField(key);
        string v = NormalizeEdit(key, Convert.ToString(d["NewValue"]), false);
        var cur = CustomerValues(cid);
        if (v == cur[key]) return;
        DateTime at;
        if (!DateTime.TryParse(Convert.ToString(d["CreatedAt"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out at)) at = DateTime.Now;
        lock (WriteLock)
        {
            Exec("UPDATE dbo.CustomerEdit SET Status='superseded', DecidedAt=GETDATE() WHERE CustomerId=@c AND Field=@f AND Status='pending'", "@c", cid, "@f", key);
            Exec(@"INSERT dbo.CustomerEdit(CustomerId,Field,OldValue,NewValue,Source,Status,Note,CreatedAt)
VALUES(@c,@f,@o,@n,'member','pending',@note,@at)", "@c", cid, "@f", key, "@o", cur[key], "@n", v,
                "@note", Trunc(Convert.ToString(d.ContainsKey("Note") ? d["Note"] : "") + " (ส่งจากออนไลน์)", 300), "@at", at);
        }
        Log("สมาชิก #" + cid + " ส่งคำขอแก้ไขข้อมูลจากออนไลน์: " + EditField(key)[2]);
    }

    static object CloudSync(bool full)
    {
        var s = Settings();
        string url = s["CloudUrl"].TrimEnd('/'), key = s["CloudKey"];
        if (url.Length == 0 || key.Length == 0) throw new ApiError(400, "ยังไม่ได้ตั้งค่าหน้าสมาชิกออนไลน์");
        lock (CloudLock)
        {
            try
            {
                if (full) Exec("DELETE FROM dbo.CloudPushed");

                // 1) รับสิ่งที่ลูกค้าทำออนไลน์
                var pulled = (Dictionary<string, object>)CloudCall(url + "/sync/pull", key, null);
                var ack = new List<int>();
                foreach (Dictionary<string, object> it in (System.Collections.IEnumerable)pulled["items"])
                {
                    int id = Convert.ToInt32(it["id"]), cid = Convert.ToInt32(it["cid"]);
                    var d = (Dictionary<string, object>)it["data"];
                    try
                    {
                        if ((string)it["kind"] == "pin")
                        {
                            DateTime at = DateTime.ParseExact((string)d["pin_at"], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                            int n = Exec(@"UPDATE dbo.MemberPin SET PinHash=@h, Salt=@s, UpdatedAt=@at, UpdatedBy=N'ลูกค้าเปลี่ยนเอง (ออนไลน์)', FailCount=0, LockedUntil=NULL, MustChange=0
WHERE CustomerId=@c AND UpdatedAt < @at", "@h", (string)d["hash"], "@s", (string)d["salt"], "@at", at, "@c", cid);
                            if (n > 0) { EndSessions(cid); Log("สมาชิก #" + cid + " เปลี่ยน PIN เอง (ออนไลน์)"); }
                        }
                        else if ((string)it["kind"] == "edit") ImportOnlineEdit(cid, d);
                        ack.Add(id);
                    }
                    catch (ApiError e) { Log("ข้ามรายการออนไลน์ #" + id + ": " + e.Message); ack.Add(id); }
                }
                if (ack.Count > 0) CloudCall(url + "/sync/ack", key, new Dictionary<string, object> { { "ids", ack } });

                // 2) ส่งข้อมูลสมาชิกที่มี PIN ขึ้นไป (เฉพาะที่เปลี่ยน)
                var pushed = new Dictionary<string, string>();
                foreach (var r in Query("SELECT Kind, Id, Hash FROM dbo.CloudPushed")) pushed[(string)r["Kind"] + r["Id"]] = (string)r["Hash"];
                var rows = Query(@"SELECT m.CustomerId, m.PinHash, m.Salt, m.UpdatedAt, m.FailCount, m.MustChange, c.Phone
FROM dbo.MemberPin m JOIN " + Cw + "Customer c ON c.Id=m.CustomerId WHERE ISNULL(c.IsDelete,0)=0");
                var members = new List<object>();
                var all = new List<int>();
                var drugIds = new HashSet<int>();
                var newHash = new List<object[]>();
                // แจ้งเตือน LINE: สถานะล่าสุดที่แจ้งไปแล้วของแต่ละคน (บิลล่าสุด / ปีที่อวยพรวันเกิด)
                var notifyState = new Dictionary<int, int[]>();
                foreach (var r in Query("SELECT CustomerId, LastOrderId, BirthdayYear FROM dbo.LineNotify"))
                    notifyState[Convert.ToInt32(r["CustomerId"])] = new[] { Convert.ToInt32(r["LastOrderId"]), Convert.ToInt32(r["BirthdayYear"]) };
                var notify = new List<object>();
                var stateUpdates = new Dictionary<int, int[]>();
                foreach (var r in rows)
                {
                    int cid = Convert.ToInt32(r["CustomerId"]);
                    string phone = Regex.Replace((string)r["Phone"] ?? "", "[^0-9]", "");
                    if (phone.Length < 9) continue; // ไม่มีเบอร์ เข้าสู่ระบบไม่ได้อยู่แล้ว
                    Dictionary<string, object> data;
                    try { data = MyProfile(s, cid); } catch (ApiError) { continue; }
                    all.Add(cid);
                    CollectNotifications(s, cid, data, notifyState, notify, stateUpdates);
                    foreach (Dictionary<string, object> i in (List<Dictionary<string, object>>)data["Items"])
                        if (i["ProductId"] != null && Convert.ToInt32(i["HasInfo"]) == 1) drugIds.Add(Convert.ToInt32(i["ProductId"]));
                    var m = new Dictionary<string, object> {
                        { "cid", cid }, { "phone", phone }, { "pin_hash", r["PinHash"] }, { "salt", r["Salt"] },
                        { "pin_at", r["UpdatedAt"] }, { "blocked", Convert.ToInt32(r["FailCount"]) >= PinBlockAfter },
                        { "must_change", Convert.ToBoolean(r["MustChange"]) }, { "data", data } };
                    string h = Sha256Hex(Json.Serialize(m));
                    string old;
                    if (pushed.TryGetValue("m" + cid, out old) && old == h) continue;
                    members.Add(m);
                    newHash.Add(new object[] { "m", cid, h });
                }
                var drugs = new List<object>();
                foreach (int id in drugIds)
                {
                    object d;
                    try { d = DrugDetail(id); } catch (ApiError) { continue; }
                    string h = Sha256Hex(Json.Serialize(d)), old;
                    if (pushed.TryGetValue("d" + id, out old) && old == h) continue;
                    drugs.Add(new Dictionary<string, object> { { "id", id }, { "data", d } });
                    newHash.Add(new object[] { "d", id, h });
                }
                var settings = new Dictionary<string, object> { { "ShopName", s["ShopName"] }, { "page", Encoding.UTF8.GetString(WebFile("check.html")) } };
                string sh = Sha256Hex(Json.Serialize(settings)), sold;
                bool settingsChanged = !(pushed.TryGetValue("s0", out sold) && sold == sh);
                if (settingsChanged) newHash.Add(new object[] { "s", 0, sh });

                // ส่งเป็นชุด ชุดแรกมีรายชื่อทั้งหมด (ลบคนที่ยกเลิก PIN ออกจากออนไลน์)
                int batches = Math.Max(1, Math.Max((members.Count + 24) / 25, (drugs.Count + 49) / 50));
                object result = null;
                for (int i = 0; i < batches; i++)
                {
                    var b = new Dictionary<string, object> {
                        { "members", members.Skip(i * 25).Take(25).ToList() }, { "drugs", drugs.Skip(i * 50).Take(50).ToList() } };
                    if (i == 0)
                    {
                        b["all"] = all;
                        b["allDrugs"] = drugIds.ToList();
                        if (settingsChanged) b["settings"] = settings;
                    }
                    result = CloudCall(url + "/sync/push", key, b);
                }
                // ผลคำขอแก้ไขที่ลูกค้าส่งมา (อนุมัติ/ไม่อนุมัติ) ภายใน 3 วัน ที่ยังไม่ได้แจ้ง
                var editIds = new List<int>();
                foreach (var e in Query(@"SELECT Id, CustomerId, Field, Status, Note FROM dbo.CustomerEdit
WHERE Source='member' AND Status IN ('approved','rejected') AND Notified=0 AND DecidedAt >= DATEADD(day,-3,GETDATE())"))
                {
                    editIds.Add(Convert.ToInt32(e["Id"]));
                    if (s["LineNotifyEdits"] == "1" && all.Contains(Convert.ToInt32(e["CustomerId"])))
                        notify.Add(new Dictionary<string, object> { { "cid", e["CustomerId"] }, { "kind", "edit" }, { "field", e["Field"] },
                            { "status", e["Status"] }, { "note", (string)e["Status"] == "rejected" ? (e["Note"] ?? "") : "" } });
                }
                int sent = 0;
                for (int i = 0; i < notify.Count; i += 30)
                {
                    var nr = CloudCall(url + "/sync/push", key, new Dictionary<string, object> { { "notify", notify.Skip(i).Take(30).ToList() } }) as Dictionary<string, object>;
                    if (nr != null && nr.ContainsKey("sent")) sent += Convert.ToInt32(nr["sent"]);
                }
                foreach (var kv in stateUpdates)
                    Exec("UPDATE dbo.LineNotify SET LastOrderId=@o, BirthdayYear=@y WHERE CustomerId=@c; IF @@ROWCOUNT=0 INSERT dbo.LineNotify(CustomerId,LastOrderId,BirthdayYear) VALUES(@c,@o,@y)",
                        "@c", kv.Key, "@o", kv.Value[0], "@y", kv.Value[1]);
                if (editIds.Count > 0)
                    Exec("UPDATE dbo.CustomerEdit SET Notified=1 WHERE Id IN (" + string.Join(",", editIds) + ")"); // เลข Id ล้วน
                CloudNotified += sent;
                if (sent > 0) Log("แจ้งเตือน LINE " + sent + " ข้อความ");
                foreach (var h in newHash)
                    Exec("UPDATE dbo.CloudPushed SET Hash=@h WHERE Kind=@k AND Id=@id; IF @@ROWCOUNT=0 INSERT dbo.CloudPushed(Kind,Id,Hash) VALUES(@k,@id,@h)",
                        "@k", h[0], "@id", h[1], "@h", h[2]);
                var res = result as Dictionary<string, object>;
                CloudMembers = res != null && res.ContainsKey("members") ? Convert.ToInt32(res["members"]) : all.Count;
                CloudLastPushed = members.Count;
                CloudLastOk = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                CloudLastError = null;
                if (members.Count > 0 || ack.Count > 0) Log("ซิงก์ออนไลน์: ส่งสมาชิก " + members.Count + " คน ยา " + drugs.Count + " รายการ รับ " + ack.Count + " รายการ");
            }
            catch (Exception e)
            {
                CloudLastError = e.Message;
                Log(e.Message);
                throw;
            }
        }
        return CloudStatus();
    }
}
