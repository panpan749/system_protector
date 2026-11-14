using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace PopupGuard
{
    public class ProcessInfo
    {
        public int Pid; public int ParentPid; public long Created; public string Path=""; public string Name="";
        public string Key { get { return Pid+":"+Created; } }
    }
    public class WindowInfo
    {
        [ScriptIgnore] public IntPtr Handle; public string Title=""; public string Class=""; public ProcessInfo Process;
        [ScriptIgnore] public DateTime ObservedAt;
        public Rectangle Bounds; public long Style, ExStyle; public string Hash=""; public int Score; public string Reason=""; public uint Dpi=96;
        public string Key { get { return Handle.ToInt64()+":"+Process.Key+":"+Class+":"+Title; } }
    }
    public class Rule
    {
        public string Id=Guid.NewGuid().ToString("N"); public string Path=""; public string Hash=""; public string WindowClass="";
        public string Title=""; public bool AnyTitle; public bool Allow; public bool Enabled=true; public DateTime Created=DateTime.UtcNow;
        public bool Matches(WindowInfo w)
        {
            return Enabled && Hash.Length==64 && string.Equals(Path,w.Process.Path,StringComparison.OrdinalIgnoreCase)
                && string.Equals(Hash,w.Hash,StringComparison.OrdinalIgnoreCase) && WindowClass==w.Class && (AnyTitle || Title==w.Title);
        }
        public static Rule From(WindowInfo w, bool allow, bool anyTitle)
        {
            if(w.Hash.Length!=64 || string.IsNullOrEmpty(w.Process.Path) || w.Process.Created==0) throw new InvalidOperationException("无法验证文件指纹或进程身份，不能创建规则。");
            return new Rule { Path=w.Process.Path,Hash=w.Hash,WindowClass=w.Class,Title=w.Title,Allow=allow,AnyTitle=anyTitle };
        }
    }
    public class Settings
    {
        public int Version=1; public bool AutoPrompt=true; public bool CaptureScreenshots=true; public bool UiAnimations=true; public List<Rule> Rules=new List<Rule>();
        public bool EnablePresets=true; public List<string> DisabledPresetIds=new List<string>();
    }
    public class AuditEntry
    {
        public DateTime Time=DateTime.Now; public string Action,Title,Path,Reason; public int Pid; public long Created;
    }
    public class Store
    {
        public readonly string DirectoryPath; public Settings Settings; public string Warning="";
        readonly JavaScriptSerializer json=new JavaScriptSerializer(); readonly object gate=new object();
        public Store(string directory)
        {
            DirectoryPath=directory; Directory.CreateDirectory(directory); string path=System.IO.Path.Combine(directory,"settings.json");
            Settings=new Settings();
            if(File.Exists(path)) try
            {
                if(new FileInfo(path).Length>2*1024*1024) throw new InvalidDataException("配置文件过大");
                var loaded=json.Deserialize<Settings>(File.ReadAllText(path));
                if(loaded==null || loaded.Version!=1 || loaded.Rules==null || loaded.Rules.Any(r=>r==null || r.Path==null || r.Hash==null || r.WindowClass==null || r.Title==null)) throw new InvalidDataException("配置格式错误");
                if(loaded.DisabledPresetIds==null) loaded.DisabledPresetIds=new List<string>();
                Settings=loaded;
            } catch(Exception e) { Warning="配置损坏，已使用安全默认设置："+e.Message; File.Copy(path,path+".invalid-"+DateTime.Now.ToString("yyyyMMddHHmmssfff"),true); }
        }
        public void Save()
        {
            lock(gate)
            {
                string target=Path.Combine(DirectoryPath,"settings.json"), temp=target+".tmp";
                File.WriteAllText(temp,json.Serialize(Settings));
                if(File.Exists(target)) File.Replace(temp,target,target+".bak"); else File.Move(temp,target);
            }
        }
        public void Log(string action, WindowInfo w, string reason)
        {
            lock(gate) try
            {
                string path=Path.Combine(DirectoryPath,"audit.jsonl");
                if(File.Exists(path) && new FileInfo(path).Length>5*1024*1024) { File.Copy(path,path+".1",true); File.WriteAllText(path,""); }
                File.AppendAllText(path,json.Serialize(new AuditEntry { Action=action, Title=w==null?"":w.Title, Path=w==null?"":w.Process.Path, Pid=w==null?0:w.Process.Pid, Created=w==null?0:w.Process.Created, Reason=reason })+Environment.NewLine);
            } catch(IOException) { Warning="日志写入失败，请检查剩余空间。"; } catch(UnauthorizedAccessException) { Warning="日志目录不可写。"; }
        }
    }
    public static class Safety
    {
        static readonly HashSet<string> Hosts=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "explorer.exe","dwm.exe","csrss.exe","winlogon.exe","lsass.exe","services.exe","svchost.exe","sihost.exe","shellexperiencehost.exe","startmenuexperiencehost.exe","applicationframehost.exe","searchhost.exe","runtimebroker.exe","consent.exe",
            "chrome.exe","msedge.exe","firefox.exe","brave.exe","opera.exe","vivaldi.exe","iexplore.exe","browser.exe","360chrome.exe","360chromex.exe","360se.exe","qqbrowser.exe","sogouexplorer.exe",
            "cmd.exe","powershell.exe","pwsh.exe","rundll32.exe","wscript.exe","cscript.exe","mshta.exe","regsvr32.exe","msiexec.exe","dllhost.exe" };
        public static bool IsWithin(string file, string directory)
        {
            if(string.IsNullOrWhiteSpace(file)||string.IsNullOrWhiteSpace(directory)) return false;
            try { string d=Path.GetFullPath(directory).TrimEnd('\\','/'); return Path.GetFullPath(file).StartsWith(d+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        public static string ProtectedReason(ProcessInfo p)
        {
            if(p==null || p.Created==0 || string.IsNullOrWhiteSpace(p.Path)) return "进程身份或路径不可读取";
            if(p.Pid==Process.GetCurrentProcess().Id) return "弹窗卫士自身";
            if(IsWithin(p.Path,Environment.GetFolderPath(Environment.SpecialFolder.Windows))) return "Windows 系统组件";
            if(Hosts.Contains(Path.GetFileName(p.Path))) return "浏览器、系统宿主或命令解释器；需要在所属应用中处理";
            return "";
        }
        public static bool SameInstance(ProcessInfo a, ProcessInfo b) { return a!=null && b!=null && a.Created!=0 && a.Pid==b.Pid && a.Created==b.Created && string.Equals(a.Path,b.Path,StringComparison.OrdinalIgnoreCase); }
        public static string HashFile(string path)
        {
            try { using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""); }
            catch { return ""; }
        }
    }
    public static class Classifier
    {
        public static void Score(WindowInfo w, Rectangle work)
        {
            int score=0; var reasons=new List<string>();
            string[] words={"广告","推广","今日热点","热门资讯","红包福利","限时秒杀","领红包","ad promotion","sponsored"};
            if(words.Any(x=>w.Title.IndexOf(x,StringComparison.OrdinalIgnoreCase)>=0)) { score+=45; reasons.Add("标题含推广词"); }
            double scale=Math.Max(96,w.Dpi)/96.0;
            bool small=w.Bounds.Width>=120*scale && w.Bounds.Height>=70*scale && w.Bounds.Width<=700*scale && w.Bounds.Height<=550*scale;
            if(small) { score+=10; reasons.Add("小窗口"); }
            if((w.ExStyle&8)!=0) { score+=15; reasons.Add("置顶"); }
            if((w.ExStyle&0x80)!=0) { score+=10; reasons.Add("工具窗口"); }
            if((w.Style&0xC00000)==0) { score+=10; reasons.Add("无标题栏"); }
            if(small && Math.Abs(w.Bounds.Right-work.Right)<80*scale && Math.Abs(w.Bounds.Bottom-work.Bottom)<100*scale) { score+=15; reasons.Add("屏幕右下角"); }
            w.Score=score; w.Reason=string.Join("、",reasons);
        }
    }
}
