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
[assembly: System.Reflection.AssemblyVersion("1.1.0")]
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
    };

    static void Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;

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
        Console.Title = "ระบบสมาชิก ร้านยามุมยาเภสัช";

        try { InitDb(); }
        catch (Exception e)
        {
            Console.WriteLine("เชื่อมต่อฐานข้อมูลไม่ได้: " + e.Message);
            Console.WriteLine("ตรวจสอบว่า SQL Server (" + server + ") ทำงานอยู่ และมีฐานข้อมูล " + cwDb + " ของ CW Pharma");
            Console.WriteLine("ถ้า SQL Server ชื่ออื่น ให้สร้างไฟล์ config.ini ไว้ข้าง exe (ดูตัวอย่างใน README)");
            Console.WriteLine("กดปุ่มใดก็ได้เพื่อปิด");
            Console.ReadKey();
            return;
        }

        var args = Environment.GetCommandLineArgs();
        bool afterUpdate = args.Contains("--after-update");
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
                    // เปิดโปรแกรมไว้อยู่แล้ว — แค่เปิดหน้าเว็บให้ และบอกให้รู้ว่าทำไมหน้าต่างนี้จะปิด
                    Console.WriteLine("ระบบสมาชิกเปิดอยู่แล้ว (พอร์ต " + new Uri(url).Port + " ถูกใช้งานอยู่)");
                    Console.WriteLine("  กำลังเปิดหน้าเว็บ " + url);
                    Console.WriteLine("  ถ้าหาหน้าต่างโปรแกรมเดิมไม่เจอ: เปิด Task Manager แล้ว End task \"YaMumMember\" ก่อน แล้วเปิดใหม่");
                    Console.WriteLine("  หน้าต่างนี้จะปิดเองใน 10 วินาที");
                    OpenBrowser(url);
                    Thread.Sleep(10000);
                    return;
                }
                Console.WriteLine("เปิดพอร์ตไม่ได้ (" + prefix + "): " + e.Message);
                Console.WriteLine("ถ้าใช้ http://+:port/ ต้องรันแบบ Administrator หรือเพิ่ม urlacl ก่อน");
                Console.ReadKey();
                return;
            }
        }
        Console.WriteLine("ระบบสมาชิก ร้านยามุมยาเภสัช  เวอร์ชัน " + AppVersion + (afterUpdate ? "  (อัปเดตแล้ว)" : ""));
        Console.WriteLine("  ฐานข้อมูล CW : " + server + " / " + cwDb);
        Console.WriteLine("  ฐานข้อมูลสมาชิก: " + MemberDb);
        Console.WriteLine("  เปิดเบราว์เซอร์ที่ " + url);
        Console.WriteLine("  (ปิดหน้าต่างนี้ = ปิดระบบสมาชิก)");
        if (!afterUpdate && !args.Contains("--no-browser")) OpenBrowser(url);

        CleanupOldExe();
        new Thread(UpdateLoop) { IsBackground = true }.Start();

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = Listener.GetContext(); }
            catch { if (Restarting) { Thread.Sleep(Timeout.Infinite); } throw; }
            ThreadPool.QueueUserWorkItem(o => Handle((HttpListenerContext)o), ctx);
        }
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
            catch (Exception e) { UpdateError = e.Message; Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " UPDATE " + e.Message); }
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
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " กำลังดาวน์โหลดเวอร์ชัน " + ver + " ...");
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
            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " ติดตั้งเวอร์ชัน " + ver + " แล้ว กำลังเริ่มโปรแกรมใหม่...");
            new Thread(() => Restart(exe, old, ver)).Start();
        }
    }

    // เปิดในหน้าต่างใหม่ ถ้าไม่ได้ (บางเครื่องห้าม) ให้ใช้หน้าต่างเดิมต่อ
    static Process StartSelf(string exe, string args)
    {
        foreach (bool shell in new[] { true, false })
        {
            try { return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = shell, WorkingDirectory = Root }); }
            catch (Exception e) { Console.WriteLine("เปิดโปรแกรมไม่ได้ (" + (shell ? "หน้าต่างใหม่" : "หน้าต่างเดิม") + "): " + e.Message); }
        }
        return null;
    }

    // ปิดตัวเอง เปิดเวอร์ชันใหม่ แล้วรอจนเวอร์ชันใหม่ตอบได้ ถ้าไม่ขึ้นภายใน 60 วินาที ย้อนกลับเวอร์ชันเดิม
    static void Restart(string exe, string old, string ver)
    {
        Thread.Sleep(1500); // ให้คำตอบ API ส่งถึงหน้าเว็บก่อน
        Restarting = true;
        try { Listener.Stop(); } catch { }
        Process proc = StartSelf(exe, "--after-update");

        for (int i = 0; proc != null && i < 60; i++)
        {
            Thread.Sleep(1000);
            try
            {
                using (var wc = new WebClient())
                {
                    var v = Json.Deserialize<Dictionary<string, object>>(wc.DownloadString(BaseUrl + "api/version"));
                    if (Str(v, "version") == ver) Environment.Exit(0);
                }
            }
            catch { }
            if (proc.HasExited) break;
        }

        Console.WriteLine("เวอร์ชันใหม่เปิดไม่ขึ้น — ย้อนกลับเวอร์ชัน " + AppVersion);
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
            Console.WriteLine("ย้อนกลับไม่สำเร็จ: " + e.Message + " — เปลี่ยนชื่อ " + Path.GetFileName(old) + " กลับเป็น " + Path.GetFileName(exe) + " เอง");
            Console.ReadKey();
        }
        Environment.Exit(1);
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

    static void Handle(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (path.StartsWith("/api/"))
                WriteJson(ctx, 200, Api(ctx, path.Substring(5).Trim('/'), ctx.Request.HttpMethod));
            else
                ServeStatic(ctx, path);
        }
        catch (ApiError e) { WriteJson(ctx, e.Status, new Dictionary<string, object> { { "error", e.Message } }); }
        catch (Exception e)
        {
            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " ERROR " + e.Message);
            try { WriteJson(ctx, 500, new Dictionary<string, object> { { "error", e.Message } }); } catch { }
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

    static void ServeStatic(HttpListenerContext ctx, string path)
    {
        if (path == "/") path = "/index.html";
        if (path == "/check") path = "/check.html";
        string name = Uri.UnescapeDataString(path).TrimStart('/');
        string full = Path.GetFullPath(Path.Combine(WebRoot, name));
        byte[] bytes = null;
        // ใช้ไฟล์ในโฟลเดอร์ wwwroot ถ้ามี (สำหรับแก้หน้าเว็บ) ไม่งั้นใช้ไฟล์ที่ฝังอยู่ใน exe
        if (full.StartsWith(WebRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            bytes = File.ReadAllBytes(full);
        else if (!name.Contains("/") && !name.Contains("\\"))
        {
            using (var st = typeof(App).Assembly.GetManifestResourceStream("www." + name))
            {
                if (st != null)
                {
                    var ms = new MemoryStream();
                    st.CopyTo(ms);
                    bytes = ms.ToArray();
                }
            }
        }
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

    static string Q(HttpListenerContext ctx, string k) { return (ctx.Request.QueryString[k] ?? "").Trim(); }

    static void RequirePin(HttpListenerContext ctx, Dictionary<string, string> s)
    {
        string pin = s["StaffPin"];
        if (pin.Length > 0 && ctx.Request.Headers["X-Staff-Pin"] != pin)
            throw new ApiError(401, "ต้องใส่ PIN พนักงาน");
    }

    static object Api(HttpListenerContext ctx, string route, string method)
    {
        var seg = route.Split('/');
        bool post = method == "POST";
        if (route == "version") return new Dictionary<string, object> { { "version", AppVersion } };
        if (!route.StartsWith("update")) LastActivity = DateTime.Now;

        var s = Settings();
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
        }
        if (seg[0] == "orders" && seg.Length == 2) return OrderItems(ParseId(seg[1]));
        if (route == "ledger" && !post) return LedgerList(0, 200);
        if (seg[0] == "ledger" && seg.Length == 3 && seg[2] == "cancel" && post)
        {
            RequirePin(ctx, s);
            return CancelLedger(ParseId(seg[1]), Str(Body(ctx), "staff"));
        }
        if (route == "rewards" && !post) return Query("SELECT * FROM dbo.Reward ORDER BY IsActive DESC, Points");
        if (route == "rewards" && post) { RequirePin(ctx, s); return SaveReward(Body(ctx)); }
        if (route == "check") return SelfCheck(s, Q(ctx, "phone"));
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
        foreach (var kv in s) if (kv.Key != "StaffPin") d[kv.Key] = kv.Value;
        d["HasPin"] = s["StaffPin"].Length > 0;
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
            if (k == "BahtPerPoint" || k.StartsWith("Tier"))
            {
                double n;
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n) || n <= 0)
                    throw new ApiError(400, k + " ต้องเป็นตัวเลขมากกว่า 0");
            }
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
  SELECT Order_Id, SUM(Price_Amount) amt FROM " + Cw + @"ProductBack
  WHERE ISNULL(IsCancel,0)=0 AND ProductBack_Status=2 GROUP BY Order_Id
), o AS (
  SELECT o.Id, o.Order_Code, o.Customer_Id cid, o.Date_Order d, o.OrderPriceNet gross,
         ISNULL(r.amt,0) returned, o.OrderPriceNet - ISNULL(r.amt,0) net
  FROM " + Cw + @"[Order] o LEFT JOIN ret r ON r.Order_Id=o.Id
  WHERE ISNULL(o.IsOrderCancel,0)=0 AND o.Order_Status=2 AND ISNULL(o.IsDelete,0)=0
)";
    }

    static List<Dictionary<string, object>> MemberRows(Dictionary<string, string> s, string where, params object[] kv)
    {
        string sql = @"
WITH " + OrdersCte() + @", agg AS (
  SELECT cid, COUNT(*) visits, SUM(net) spendAll, MAX(d) lastVisit,
         SUM(CASE WHEN d >= DATEADD(day,-365,GETDATE()) THEN net ELSE 0 END) spend365,
         SUM(CASE WHEN d >= @pstart AND net > 0 THEN FLOOR(net/@bpp) ELSE 0 END) earned
  FROM o WHERE cid > 0 GROUP BY cid
), led AS (
  SELECT CustomerId,
         SUM(CASE WHEN Kind='redeem' THEN -Points ELSE 0 END) redeemed,
         SUM(CASE WHEN Kind<>'redeem' THEN Points ELSE 0 END) adjusted
  FROM dbo.Ledger WHERE IsCancelled=0 GROUP BY CustomerId
)
SELECT c.Id, c.Customer_Code Code, c.BarCode, c.FullName, c.Phone, c.EmailAddress Email, c.BirthDate, c.Sex,
       c.Address, c.Date_Register Registered, c.Intolerance Allergy, c.CongenitalDisease Disease, c.Comment,
       CAST(ISNULL(c.IsWholesaleCustomer,0) AS bit) Wholesale,
       ISNULL(a.visits,0) Visits, ISNULL(a.spendAll,0) SpendAll, ISNULL(a.spend365,0) Spend365, a.lastVisit LastVisit,
       CAST(ISNULL(a.earned,0) AS int) Earned, ISNULL(l.redeemed,0) Redeemed, ISNULL(l.adjusted,0) Adjusted
FROM " + Cw + @"Customer c
LEFT JOIN agg a ON a.cid=c.Id
LEFT JOIN led l ON l.CustomerId=c.Id
WHERE ISNULL(c.IsDelete,0)=0" + (s["IncludeWholesale"] == "1" ? "" : " AND ISNULL(c.IsWholesaleCustomer,0)=0") + where;

        var args = new List<object>(kv);
        args.AddRange(new object[] {
            "@pstart", DateTime.ParseExact(s["PointStartDate"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "@bpp", (decimal)Num(s, "BahtPerPoint") });
        var rows = Query(sql, args.ToArray());
        foreach (var r in rows)
        {
            r["Points"] = Convert.ToInt32(r["Earned"]) + Convert.ToInt32(r["Adjusted"]) - Convert.ToInt32(r["Redeemed"]);
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
        var orders = Query("WITH " + OrdersCte() + @"
SELECT TOP 300 Id, Order_Code Code, d Date, gross Gross, returned Returned, net Net,
       CASE WHEN d >= @pstart AND net > 0 THEN CAST(FLOOR(net/@bpp) AS int) ELSE 0 END Points
FROM o WHERE cid=@id ORDER BY d DESC",
            "@id", id, "@pstart", DateTime.ParseExact(s["PointStartDate"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "@bpp", (decimal)Num(s, "BahtPerPoint"));
        return new Dictionary<string, object> {
            { "member", m }, { "orders", orders }, { "ledger", LedgerList(id, 500) } };
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

    static object Redeem(Dictionary<string, string> s, int id, Dictionary<string, object> b)
    {
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

        var redeemMonth = Query(@"SELECT COUNT(*) N, ISNULL(-SUM(Points),0) Points FROM dbo.Ledger
            WHERE Kind='redeem' AND IsCancelled=0 AND CreatedAt >= @m", "@m", month)[0];

        return new Dictionary<string, object> {
            { "members", all.Count },
            { "active90", all.Count(r => { var d = dt(r["LastVisit"]); return d.HasValue && d.Value >= now.AddDays(-90); }) },
            { "newThisMonth", all.Count(r => { var d = dt(r["Registered"]); return d.HasValue && d.Value >= month; }) },
            { "pointsOutstanding", all.Sum(r => Math.Max(0, Convert.ToInt32(r["Points"]))) },
            { "tiers", tiers },
            { "sales", sales },
            { "redeemMonth", redeemMonth },
            { "birthdays", all.Where(r => { var d = dt(r["BirthDate"]); return d.HasValue && d.Value.Month == now.Month && d.Value.Year > 1900; })
                              .OrderBy(r => dt(r["BirthDate"]).Value.Day).ToList() },
            { "top", all.OrderByDescending(r => Convert.ToDouble(r["Spend365"])).Take(10).ToList() },
            { "dormant", all.Where(r => { var d = dt(r["LastVisit"]); return d.HasValue && d.Value < now.AddDays(-60) && Convert.ToInt32(r["Visits"]) >= 2; })
                            .OrderByDescending(r => Convert.ToDouble(r["Spend365"])).Take(10).ToList() },
            { "recent", LedgerList(0, 10) },
        };
    }

    static object SelfCheck(Dictionary<string, string> s, string phone)
    {
        string digits = Regex.Replace(phone, "[^0-9]", "");
        if (digits.Length < 9) throw new ApiError(400, "กรุณาใส่เบอร์โทรให้ครบ");
        var rows = MemberRows(s, " AND REPLACE(REPLACE(c.Phone,'-',''),' ','') = @d", "@d", digits);
        if (rows.Count == 0) throw new ApiError(404, "ไม่พบเบอร์นี้ในระบบสมาชิก");
        var r = rows[0];
        string name = (string)r["FullName"] ?? "";
        var parts = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string masked = parts.Length > 1 ? parts[0] + " " + parts[1].Substring(0, 1) + "." : name;
        return new Dictionary<string, object> {
            { "name", masked }, { "points", r["Points"] }, { "tier", r["Tier"] }, { "benefit", r["Benefit"] },
            { "nextTier", r["NextTier"] }, { "toNextTier", r["ToNextTier"] }, { "spend365", r["Spend365"] },
            { "rewards", Query("SELECT Name, Points FROM dbo.Reward WHERE IsActive=1 ORDER BY Points") } };
    }
}
