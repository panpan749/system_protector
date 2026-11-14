using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PopupGuard
{
    public sealed class SoftwareRule
    {
        public string Id="", Name="", Kind="Installed", Category="用户不需要", Reason="", Source="";
        public int Revision=1;
        public string Base="", RelativeRoot="", InstallId="", Publisher="", ProductName="", ExtensionId="";
        public string[] Markers=new string[0];
    }
    public sealed class SoftwareCatalog
    {
        public int SchemaVersion=1, Revision=1;
        public List<SoftwareRule> Rules=new List<SoftwareRule>();
    }
    public sealed class SoftwareFinding
    {
        public string Key="", RuleId="", Name="", Category="", Root="", Evidence="", Fingerprint="", Status="待确认", Detail="";
        public string Profile="", Identity="", Marker=""; public InstalledApp App; public bool Actionable;
        public DateTime Seen=DateTime.Now;
    }
    public sealed class SoftwareScan
    {
        public DateTime Time=DateTime.Now; public string CatalogHash="";
        public List<SoftwareFinding> Findings=new List<SoftwareFinding>();
        public List<string> Errors=new List<string>();
    }
    public sealed class SoftwareState
    {
        public int Version=1, Hour=20, Minute=0; public bool DailyEnabled=true;
        public DateTime LastAttempt=DateTime.MinValue, LastSuccess=DateTime.MinValue;
        public List<string> AllowedRules=new List<string>(), DisabledRules=new List<string>();
        public Dictionary<string,string> Ignored=new Dictionary<string,string>();
        public SoftwareScan Latest=new SoftwareScan();
        public List<CleanupResult> Results=new List<CleanupResult>();
    }
    public sealed class CleanupResult
    {
        public string Key="", Name="", Status="", Detail=""; public DateTime Time=DateTime.Now;
        public List<string> Operations=new List<string>();
    }
    public sealed class CleanupRequest
    {
        public int Version=1; public string UserSid="", CatalogHash="";
        public SoftwareCatalog Catalog; public List<SoftwareFinding> Selected=new List<SoftwareFinding>();
        public DateTime Created=DateTime.UtcNow;
    }
    public static class MaintenanceJson
    {
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=8*1024*1024,RecursionLimit=64 }; }
        public static string Encode(object value)
        {
            // CLR reflection field order can change after deserialization. Canonical key order is required for identities and approvals.
            var json=Serializer(); return json.Serialize(Canonical(json.DeserializeObject(json.Serialize(value))));
        }
        static object Canonical(object value)
        {
            var dictionary=value as Dictionary<string,object>;
            if(dictionary!=null) { var sorted=new SortedDictionary<string,object>(StringComparer.Ordinal); foreach(var pair in dictionary) sorted[pair.Key]=Canonical(pair.Value); return sorted; }
            var array=value as object[]; if(array!=null) return array.Select(Canonical).ToArray();
            return value;
        }
        public static T Read<T>(string path)
        {
            if(new FileInfo(path).Length>8*1024*1024) throw new InvalidDataException("数据文件超过 8 MB 限制。");
            return Serializer().Deserialize<T>(File.ReadAllText(path,Encoding.UTF8));
        }
        public static T ReadVerified<T>(string path,string expected)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length>8*1024*1024) throw new InvalidDataException("请求文件过大");
                using(var memory=new MemoryStream())
                {
                    stream.CopyTo(memory); byte[] bytes=memory.ToArray(); string digest;
                    using(var sha=SHA256.Create()) digest=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
                    if(digest!=expected) throw new InvalidDataException("请求内容自确认后发生变化。");
                    return Serializer().Deserialize<T>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                }
            }
        }
        public static string Digest(string text)
        { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-",""); }
        public static void Save(string path,object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp=path+".tmp";
            File.WriteAllText(temp,Encode(value),new UTF8Encoding(false));
            if(File.Exists(path)) File.Replace(temp,path,path+".bak"); else File.Move(temp,path);
        }
    }
    public sealed class SoftwareDatabase
    {
        public readonly string DirectoryPath; public SoftwareCatalog Catalog; public SoftwareState State; public string Warning="";
        public string CatalogPath { get { return Path.Combine(DirectoryPath,"software-rules.json"); } }
        static readonly Regex IdPattern=new Regex(@"^[a-z0-9][a-z0-9._-]{2,79}$");
        public static SoftwareCatalog Bundled()
        {
            using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("PopupGuard.software-rules.json"))
            using(var r=new StreamReader(s,Encoding.UTF8)) return MaintenanceJson.Serializer().Deserialize<SoftwareCatalog>(r.ReadToEnd());
        }
        public SoftwareDatabase(string directory)
        {
            DirectoryPath=directory; Directory.CreateDirectory(directory); State=new SoftwareState(); Catalog=Bundled();
            try { if(File.Exists(CatalogPath)) { var c=MaintenanceJson.Read<SoftwareCatalog>(CatalogPath); Validate(c); Catalog=c; } else MaintenanceJson.Save(CatalogPath,Catalog); }
            catch(Exception e) { Warning="软件名单无法加载，已停止自动扫描："+e.Message; State.DailyEnabled=false; }
            string path=Path.Combine(directory,"software-state.json");
            try
            {
                if(File.Exists(path))
                {
                    var s=MaintenanceJson.Read<SoftwareState>(path);
                    if(s==null||s.Version!=1||s.Hour<0||s.Hour>23||s.Minute<0||s.Minute>59||s.AllowedRules==null||s.DisabledRules==null||s.Ignored==null||s.Latest==null||s.Latest.Findings==null||s.Latest.Errors==null||s.Results==null||s.Latest.Findings.Any(f=>f==null||f.Key==null||f.RuleId==null||f.Name==null||f.Fingerprint==null||f.Status==null)||s.Results.Any(r=>r==null||r.Name==null||r.Status==null||r.Operations==null)) throw new InvalidDataException("状态格式不正确");
                    State=s;
                    State.LastAttempt=Local(State.LastAttempt); State.LastSuccess=Local(State.LastSuccess); State.Latest.Time=Local(State.Latest.Time);
                    foreach(var finding in State.Latest.Findings) finding.Seen=Local(finding.Seen);
                    foreach(var result in State.Results) result.Time=Local(result.Time);
                }
            }
            catch(Exception e) { Warning="软件扫描状态无法加载，自动扫描已停用："+e.Message; State.DailyEnabled=false; }
            if(Warning.Length>0) State.DailyEnabled=false;
        }
        public static DateTime Local(DateTime value) { return value==DateTime.MinValue?DateTime.MinValue:value.Kind==DateTimeKind.Utc?value.ToLocalTime():value; }
        public static void Validate(SoftwareCatalog catalog)
        {
            if(catalog==null||catalog.SchemaVersion!=1||catalog.Revision<1||catalog.Rules==null||catalog.Rules.Count>1000) throw new InvalidDataException("名单版本或数量不正确。");
            var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var bundled=Bundled();
            foreach(var r in catalog.Rules)
            {
                if(r==null||r.Id==null||!IdPattern.IsMatch(r.Id)||!ids.Add(r.Id)||r.Revision<1||string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>120||r.Reason==null||r.Source==null||r.Category==null||r.Name.Any(char.IsControl)) throw new InvalidDataException("规则 ID、名称或版本不正确。");
                if(r.Kind=="Component")
                {
                    // File/driver removal recipes are compiled and reviewed. Imported data cannot add deletion code or broaden their roots.
                    var original=bundled.Rules.FirstOrDefault(x=>x.Id==r.Id&&x.Kind=="Component");
                    if(original==null||MaintenanceJson.Encode(original)!=MaintenanceJson.Encode(r)) throw new InvalidDataException("组件清理规则只能随程序版本更新；可在界面停用。");
                }
                else if(r.Kind=="Installed")
                {
                    if(string.IsNullOrWhiteSpace(r.InstallId)||r.InstallId.Length>250||r.InstallId.IndexOfAny(new[]{'\\','/','\0'})>=0||string.IsNullOrWhiteSpace(r.Publisher)||string.IsNullOrWhiteSpace(r.ProductName)) throw new InvalidDataException("软件规则必须包含精确安装项 ID、名称和发布者。");
                }
                else if(r.Kind=="ChromeExtension")
                {
                    if(r.ExtensionId==null||!Regex.IsMatch(r.ExtensionId,@"^[a-p]{32}$")) throw new InvalidDataException("Chrome 扩展 ID 必须为 32 位 a-p 字符。");
                }
                else throw new InvalidDataException("不支持的规则类型。");
                var reserved=bundled.Rules.FirstOrDefault(x=>x.Id==r.Id);
                if(reserved!=null&&reserved.Kind!=r.Kind) throw new InvalidDataException("不能更改内置规则的类型。");
            }
        }
        public string CatalogHash { get { return MaintenanceJson.Digest(MaintenanceJson.Encode(Catalog)); } }
        public void Save() { MaintenanceJson.Save(Path.Combine(DirectoryPath,"software-state.json"),State); }
        public SoftwareCatalog MergePreview(SoftwareCatalog incoming)
        {
            Validate(incoming); var merged=MaintenanceJson.Serializer().Deserialize<SoftwareCatalog>(MaintenanceJson.Encode(Catalog));
            foreach(var r in incoming.Rules)
            {
                var old=merged.Rules.FirstOrDefault(x=>x.Id==r.Id);
                if(old!=null)
                {
                    if(r.Revision<old.Revision) throw new InvalidDataException("拒绝降级规则："+r.Id);
                    if(r.Revision==old.Revision&&MaintenanceJson.Encode(r)!=MaintenanceJson.Encode(old)) throw new InvalidDataException("修改规则需要增加 Revision："+r.Id);
                    merged.Rules.Remove(old);
                }
                merged.Rules.Add(r);
            }
            merged.Revision=Math.Max(Catalog.Revision+1,incoming.Revision); Validate(merged); return merged;
        }
        public void Commit(SoftwareCatalog next) { Validate(next); MaintenanceJson.Save(CatalogPath,next); Catalog=next; }
        public SoftwareRule InstalledRule(InstalledApp app)
        {
            if(app==null) return null;
            var existing=Catalog.Rules.FirstOrDefault(r=>r.Kind=="Installed"&&SoftwareScanner.MatchInstalled(r,app));
            if(existing!=null) return existing;
            // Reuse reviewed component rules at the same installation root; never bypass their keep/disabled state with a second rule.
            return Catalog.Rules.FirstOrDefault(r=>r.Kind=="Component"&&SoftwareScanner.Equal(r.Publisher,app.Publisher)&&
                SoftwareScanner.Equal(SoftwareScanner.ComponentRoot(r),app.Location)&&
                (string.IsNullOrEmpty(r.InstallId)||SoftwareScanner.Equal(r.InstallId,app.Id)));
        }
        public SoftwareRule AddInstalled(InstalledApp app,string reason)
        {
            if(app==null) throw new InvalidOperationException("请选择来源软件。");
            var existing=InstalledRule(app); if(existing!=null) return existing;
            string id=SoftwareScanner.Clean(app.Id),publisher=SoftwareScanner.Clean(app.Publisher),name=SoftwareScanner.Clean(app.Name);
            var rule=new SoftwareRule { Id="local.app."+MaintenanceJson.Digest(id+"|"+publisher+"|"+name).Substring(0,24).ToLowerInvariant(),Name=name,Kind="Installed",InstallId=id,ProductName=name,Publisher=publisher,
                Reason=reason,Source="本机用户选择 "+DateTime.Now.ToString("yyyy-MM-dd") };
            Commit(MergePreview(new SoftwareCatalog { Rules=new List<SoftwareRule> { rule } })); return rule;
        }
        public bool Due(DateTime now)
        {
            if(!State.DailyEnabled||Warning.Length>0) return false;
            if(State.LastAttempt!=DateTime.MinValue&&(now-State.LastAttempt).TotalMinutes<60) return false;
            if(State.LastSuccess==DateTime.MinValue||State.Latest.CatalogHash!=CatalogHash) return true;
            var mostRecent=now.Date.AddHours(State.Hour).AddMinutes(State.Minute); if(now<mostRecent) mostRecent=mostRecent.AddDays(-1);
            return State.LastSuccess<mostRecent;
        }
        public static bool AwaitingRecheck(string status) { return status=="等待重启"||status=="等待浏览器重启"||status=="等待浏览器复查"; }
        public bool StartupRecheckNeeded { get { return Warning.Length==0&&State.DailyEnabled&&(State.Latest.Findings.Any(f=>AwaitingRecheck(f.Status))||State.Results.Any(r=>AwaitingRecheck(r.Status))); } }
        public void ApplyScan(SoftwareScan scan,DateTime completed)
        {
            var previous=State.Latest; State.Latest=scan;
            if(scan.Errors.Count>0||scan.CatalogHash!=CatalogHash) return;
            State.LastSuccess=completed;
            // A changed scan scope or an incomplete inventory cannot prove an old source was removed.
            if(previous.CatalogHash!=scan.CatalogHash) return;
            var remaining=new HashSet<string>(scan.Findings.Select(f=>f.Key));
            var updated=new HashSet<string>();
            foreach(var result in State.Results.Where(r=>AwaitingRecheck(r.Status)&&!remaining.Contains(r.Key))) {
                result.Status="复查未检出"; result.Detail+="\r\n"+completed.ToString("yyyy-MM-dd HH:mm:ss")+"：本次扫描未再命中该来源的已知标记。"; updated.Add(result.Key);
            }
            foreach(var finding in previous.Findings.Where(f=>AwaitingRecheck(f.Status)&&!remaining.Contains(f.Key)&&!updated.Contains(f.Key))) {
                State.Results.Insert(0,new CleanupResult { Key=finding.Key,Name=finding.Name,Status="复查未检出",Time=completed,
                    Detail="原状态："+finding.Status+"\r\n来源："+finding.Root+"\r\n本次扫描未再命中该来源的已知文件、安装登记或扩展标记。" });
            }
            State.Results=State.Results.Take(500).ToList();
        }
        public string Disposition(SoftwareFinding item)
        {
            if(State.AllowedRules.Contains(item.RuleId)) return "永久保留";
            if(State.DisabledRules.Contains(item.RuleId)) return "规则停用";
            string value; if(State.Ignored.TryGetValue(item.Key,out value)&&value==item.Fingerprint) return "已忽略此版本";
            return item.Status;
        }
        public bool Pending(SoftwareFinding item) { return !State.AllowedRules.Contains(item.RuleId)&&!State.DisabledRules.Contains(item.RuleId)&&Disposition(item)==item.Status; }
    }
}
