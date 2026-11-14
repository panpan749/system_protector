using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace PopupGuard
{
    public static class SoftwareScanner
    {
        public static string Clean(string value) { return (value??"").Trim('\0',' ','\t','\r','\n'); }
        public static bool Equal(string a,string b) { return string.Equals(Clean(a),Clean(b),StringComparison.OrdinalIgnoreCase); }
        public static string ComponentRoot(SoftwareRule rule)
        {
            Environment.SpecialFolder folder;
            switch(rule.Base) { case "Roaming": folder=Environment.SpecialFolder.ApplicationData; break; case "Local": folder=Environment.SpecialFolder.LocalApplicationData; break; case "ProgramFilesX86": folder=Environment.SpecialFolder.ProgramFilesX86; break; default: throw new InvalidDataException("不支持的根目录"); }
            string parent=Environment.GetFolderPath(folder), root=Path.GetFullPath(Path.Combine(parent,rule.RelativeRoot));
            if(!Uninstall.SafeInstallRoot(root)||!Safety.IsWithin(root,parent)||Safety.IsWithin(AppDomain.CurrentDomain.BaseDirectory,root)) throw new InvalidDataException("清理目录范围不安全");
            if(Directory.Exists(root)) SafeTree.CheckPath(parent,root);
            return root;
        }
        public static List<InstalledApp> Inventory(List<string> errors)
        {
            var apps=new List<InstalledApp>();
            foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
            foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
            try
            {
                using(var root=RegistryKey.OpenBaseKey(hive,view)) using(var items=root.OpenSubKey(Uninstall.RegistryPath))
                {
                    if(items==null) continue;
                    foreach(string id in items.GetSubKeyNames())
                    try
                    {
                        using(var k=items.OpenSubKey(id))
                        {
                            if(k==null||Convert.ToInt32(k.GetValue("SystemComponent",0))==1) continue;
                            string name=Clean(Convert.ToString(k.GetValue("DisplayName",""))); if(name.Length==0) continue;
                            apps.Add(new InstalledApp { Name=name,Publisher=Clean(Convert.ToString(k.GetValue("Publisher",""))),Id=id,Key=Uninstall.RegistryPath+"\\"+id,Hive=hive,View=view,
                                Location=Clean(Environment.ExpandEnvironmentVariables(Convert.ToString(k.GetValue("InstallLocation","")))).Trim('"'),Command=Convert.ToString(k.GetValue("UninstallString","")),Msi=Convert.ToInt32(k.GetValue("WindowsInstaller",0))==1 });
                        }
                    } catch(Exception e) { errors.Add("安装项 "+id+"："+e.Message); }
                }
            } catch(Exception e) { errors.Add("安装列表 "+hive+"/"+view+"："+e.Message); }
            return apps.GroupBy(x=>x.Hive+"|"+x.Id+"|"+x.Location+"|"+x.Command).Select(g=>g.First()).ToList();
        }
        public static bool MatchInstalled(SoftwareRule rule,InstalledApp app)
        { return Equal(rule.InstallId,app.Id)&&Equal(rule.ProductName,app.Name)&&Equal(rule.Publisher,app.Publisher); }
        public static bool SameRegistration(InstalledApp a,InstalledApp b)
        {
            return a!=null&&b!=null&&a.Hive==b.Hive&&a.View==b.View&&Equal(a.Key,b.Key)&&Equal(a.Id,b.Id)&&Equal(a.Name,b.Name)&&Equal(a.Publisher,b.Publisher)&&Equal(a.Location,b.Location)&&a.Command==b.Command&&a.Msi==b.Msi;
        }
        public static string AppIdentity(InstalledApp app)
        { return app.Hive+"|"+app.View+"|"+app.Key+"|"+app.Name+"|"+app.Publisher+"|"+app.Location+"|"+app.Command+"|"+app.Msi; }
        public static string Fingerprint(SoftwareRule rule,SoftwareFinding finding,string material)
        { return MaintenanceJson.Digest(MaintenanceJson.Encode(rule)+"|"+finding.Root+"|"+finding.Profile+"|"+material); }
        public static SoftwareScan Scan(SoftwareCatalog catalog)
        {
            SoftwareDatabase.Validate(catalog);
            var scan=new SoftwareScan { CatalogHash=MaintenanceJson.Digest(MaintenanceJson.Encode(catalog)) };
            var apps=Inventory(scan.Errors);
            foreach(var rule in catalog.Rules)
            {
                try
                {
                    if(rule.Kind=="ChromeExtension") ScanChrome(rule,scan);
                    else if(rule.Kind=="Component") ScanComponent(rule,apps,scan);
                    else foreach(var app in apps.Where(a=>MatchInstalled(rule,a)))
                    {
                        var plan=Uninstall.Plan(app); var f=NewFinding(rule,app.Location,AppIdentity(app)); f.App=app;
                        f.Evidence="安装项 ID、显示名称、发布者精确匹配。\r\n"+app.Id+"\r\n"+app.Publisher;
                        f.Actionable=plan.Valid; f.Status=plan.Valid?"待确认":"需手动卸载";
                        f.Detail=plan.Valid?"使用注册的卸载程序；不会通用删除安装目录。":plan.Reason;
                        var hash=plan.Valid?Safety.HashFile(plan.FileName):"";
                        if(plan.Valid&&hash.Length!=64) { f.Actionable=false; f.Status="需手动卸载"; f.Detail="无法读取卸载程序指纹。"; }
                        f.Fingerprint=Fingerprint(rule,f,AppIdentity(app)+"|"+hash); scan.Findings.Add(f);
                    }
                } catch(Exception e) { scan.Errors.Add(rule.Name+"："+e.Message); }
            }
            return scan;
        }
        static SoftwareFinding NewFinding(SoftwareRule rule,string root,string identity)
        {
            return new SoftwareFinding { RuleId=rule.Id,Name=rule.Name,Category=rule.Category,Root=root,Identity=identity,
                Key=MaintenanceJson.Digest(rule.Id+"|"+identity.ToUpperInvariant()),Detail=rule.Reason };
        }
        static void ScanComponent(SoftwareRule rule,List<InstalledApp> apps,SoftwareScan scan)
        {
            string root=ComponentRoot(rule);
            var registered=apps.Where(a=>Equal(a.Id,rule.InstallId)&&Equal(a.Publisher,rule.Publisher)).ToList();
            string service=rule.Id=="component.quickfolder"?"QuickFolderAppSupportSvc":rule.Id=="component.winboost"?"WinBoostAppSupportSvc":"";
            string driver="";
            if(service.Length>0) using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+service))
            {
                if(key!=null) { string path=SoftwareCleanup.Executable(Convert.ToString(key.GetValue("ImagePath",""))); if(Safety.IsWithin(path,root)) driver=service+" / "+path+" / Start="+key.GetValue("Start",-1); }
            }
            bool exists=Directory.Exists(root);
            if(!exists&&registered.Count==0&&driver.Length==0) return;
            if(exists) SafeTree.CheckRoot(root);
            var markers=new List<string>(); var material=new List<string>();
            foreach(string relative in rule.Markers)
            {
                string full=Path.Combine(root,relative); if(!Safety.IsWithin(full,root)) throw new InvalidDataException("标记越界");
                if(File.Exists(full))
                {
                    SafeTree.CheckPath(root,full); string hash=Safety.HashFile(full);
                    if(hash.Length!=64) { scan.Errors.Add(rule.Name+"：文件无法读取（可能被安全软件拦截）："+full); continue; }
                    var info=FileVersionInfo.GetVersionInfo(full);
                    markers.Add(relative); material.Add(relative+"|"+hash+"|"+Clean(info.CompanyName)+"|"+Clean(info.ProductName));
                }
                else if(Directory.Exists(full)) { SafeTree.CheckPath(root,full); markers.Add(relative); material.Add(relative+"|directory"); }
            }
            if(exists&&markers.Count==0&&registered.Count==0)
            {
                // A same-named folder by itself is never enough to authorize deletion.
                var unknown=NewFinding(rule,root,root); unknown.Status="仅有目录残留"; unknown.Evidence="目录存在，但缺少规则要求的组件文件。"; unknown.Actionable=false;
                unknown.Fingerprint=Fingerprint(rule,unknown,"unidentified-directory"); scan.Findings.Add(unknown); return;
            }
            var f=NewFinding(rule,root,root); f.Marker=string.Join("\r\n",markers);
            f.Evidence="固定组件目录："+root+"\r\n文件特征："+string.Join("、",markers)+(driver.Length>0?"\r\n驱动注册："+driver:"")+"\r\n"+rule.Reason;
            // Installed registry evidence plus a marker, or at least one compiled exact file marker.
            f.Actionable=exists&&markers.Any(x=>File.Exists(Path.Combine(root,x))) || exists&&markers.Count>0&&registered.Count>0;
            f.Status=f.Actionable?"待确认":"需手动检查";
            f.Detail="清除该组件专属目录（含设置、缓存）、精确关联的自启动、计划任务、服务、加载项和安装注册项；占用文件可能需重启。";
            if(exists&&PendingRemoval.Covers(root)) { f.Status="等待重启"; f.Actionable=false; f.Detail="现有文件已在 Windows 重启删除队列中；保存工作并重启后再复查，无需重复清理。"; }
            if(!exists&&driver.Length>0) { f.Status="驱动注册残留"; f.Actionable=false; f.Detail="组件目录已不存在，但驱动注册仍可见。请重启后复查。"; }
            else if(!exists&&registered.Count>0) { f.Status="安装登记残留"; f.Actionable=false; f.Detail="组件目录已不存在，Windows 安装列表仍有登记；需核对后移除该登记，无需反复重启。"; f.Evidence+="\r\n安装登记："+string.Join("、",registered.Select(a=>a.Hive+"\\"+a.Key)); }
            material.Add(driver); material.AddRange(registered.Select(AppIdentity)); f.Fingerprint=Fingerprint(rule,f,string.Join("\n",material.OrderBy(x=>x,StringComparer.Ordinal)));
            scan.Findings.Add(f);
        }
        public static string ChromeRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Google\Chrome\User Data"); } }
        public static Dictionary<string,object> Object(object value) { return value as Dictionary<string,object> ?? new Dictionary<string,object>(); }
        public static object Value(Dictionary<string,object> obj,string key) { object value; return obj.TryGetValue(key,out value)?value:null; }
        public static Dictionary<string,object> ChromeSettings(string profile)
        {
            var entries=new Dictionary<string,object>();
            foreach(string file in new[]{"Preferences","Secure Preferences"})
            {
                string p=Path.Combine(profile,file); if(!File.Exists(p)) continue;
                if(new FileInfo(p).Length>32*1024*1024) throw new InvalidDataException("Chrome 配置超过 32 MB，跳过该配置文件");
                var json=MaintenanceJson.Serializer(); json.MaxJsonLength=32*1024*1024;
                var data=Object(json.DeserializeObject(File.ReadAllText(p)));
                var settings=Object(Value(Object(Value(data,"extensions")),"settings"));
                foreach(var entry in settings) entries[entry.Key]=entry.Value;
            }
            return entries;
        }
        static void ScanChrome(SoftwareRule rule,SoftwareScan scan)
        {
            string root=ChromeRoot; if(!Directory.Exists(root)) return;
            foreach(string profile in Directory.GetDirectories(root).Where(p=>Path.GetFileName(p)=="Default"||Regex.IsMatch(Path.GetFileName(p),@"^Profile \d+$")))
            try
            {
                SafeTree.CheckPath(root,profile);
                var entries=ChromeSettings(profile); var entry=Object(Value(entries,rule.ExtensionId));
                string ext=Path.Combine(profile,@"Extensions\"+rule.ExtensionId); bool exists=Directory.Exists(ext);
                if(!exists&&entry.Count==0) continue;
                if(exists) SafeTree.CheckPath(root,ext);
                var f=NewFinding(rule,ext,profile+"|"+rule.ExtensionId); f.Profile=profile; f.Actionable=true;
                f.Evidence="Chrome 扩展 ID 精确匹配："+rule.ExtensionId+"\r\n配置："+Path.GetFileName(profile)+"\r\n"+rule.Reason;
                f.Detail="设置该 ID 的移除策略（影响本用户全部 Chrome 配置），保留其他扩展策略；Chrome 刷新策略或重启后复查。";
                f.Status=exists?"待确认":"等待浏览器复查";
                using(var policy=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Policies\Google\Chrome"))
                {
                    string text=policy==null?"":Convert.ToString(policy.GetValue("ExtensionSettings",""));
                    if(text.Length>0)
                    {
                        var settings=MaintenanceJson.Serializer().Deserialize<Dictionary<string,object>>(text);
                        var item=Object(Value(settings,rule.ExtensionId));
                        if(Convert.ToString(Value(item,"installation_mode"))=="removed") { f.Status="等待浏览器复查"; f.Actionable=false; f.Detail="已经设置精确 ID 的移除策略，等待 Chrome 刷新策略或重启后复查。"; }
                    }
                }
                f.Fingerprint=Fingerprint(rule,f,MaintenanceJson.Encode(entry)+"|"+exists); scan.Findings.Add(f);
            } catch(Exception e) { scan.Errors.Add("Chrome "+Path.GetFileName(profile)+"："+e.Message); }
        }
    }
    public static class PendingRemoval
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint length,uint flags);
        public static string Normalize(string path)
        {
            string value=Regex.Replace(path??"",@"^\*\d+","");
            if(value.StartsWith(@"\??\")||value.StartsWith(@"\\?\")) value=value.Substring(4);
            if(!Path.IsPathRooted(value)||value.StartsWith(@"\\")) return "";
            using(var handle=CreateFile(value,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero))
            {
                if(!handle.IsInvalid) { var buffer=new StringBuilder(32768); uint length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Capacity,0); if(length>0&&length<buffer.Capacity) { value=buffer.ToString(); if(value.StartsWith(@"\\?\")) value=value.Substring(4); } }
            }
            return Path.GetFullPath(value).TrimEnd('\\').ToUpperInvariant();
        }
        public static bool Covers(string root)
        {
            using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            {
                var pending=key==null?null:key.GetValue("PendingFileRenameOperations") as string[]; if(pending==null) return false;
                var deleted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for(int i=0;i+1<pending.Length;i+=2) if(string.IsNullOrEmpty(pending[i+1])) deleted.Add(Normalize(pending[i]));
                // Do not recursively enumerate arbitrary large folders during daily checks; only evaluate when the root is itself queued.
                if(!deleted.Contains(Normalize(root))) return false;
                var files=SafeTree.Enumerate(root).Where(File.Exists).ToList(); return files.Count>0&&files.All(f=>deleted.Contains(Normalize(f)));
            }
        }
    }
    public static class SafeTree
    {
        public static void CheckRoot(string root)
        {
            if(!Path.IsPathRooted(root)||root.StartsWith(@"\\")||!Uninstall.SafeInstallRoot(root)) throw new IOException("拒绝共享或系统根目录。");
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0) throw new IOException("拒绝清理符号链接或目录联接："+root);
        }
        public static void CheckPath(string root,string path)
        {
            if(!Path.IsPathRooted(root)||root.StartsWith(@"\\")||(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0) throw new IOException("拒绝目录联接或非本地根目录。");
            if(!SoftwareScanner.Equal(path,root)&&!Safety.IsWithin(path,root)) throw new IOException("路径超出已确认目录。");
            string current=path;
            while(!SoftwareScanner.Equal(current,root))
            {
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new IOException("路径含目录联接或符号链接："+current);
                current=Path.GetDirectoryName(current); if(string.IsNullOrEmpty(current)) throw new IOException("目录核验失败");
            }
        }
        public static List<string> Enumerate(string root)
        {
            CheckRoot(root); var found=new List<string>(); var pending=new Stack<string>(); pending.Push(root);
            while(pending.Count>0)
            {
                string dir=pending.Pop(); CheckPath(root,dir);
                foreach(string item in Directory.GetFileSystemEntries(dir))
                {
                    CheckPath(root,item); found.Add(item); if(found.Count>100000) throw new IOException("目录超过 100000 项，停止清理。");
                    if((File.GetAttributes(item)&FileAttributes.Directory)!=0) pending.Push(item);
                }
            }
            return found;
        }
    }
}
