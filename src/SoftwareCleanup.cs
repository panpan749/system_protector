using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PopupGuard
{
    public sealed class RegistryChange
    {
        public RegistryHive Hive; public RegistryView View; public string Key,Name; public object Value; public RegistryValueKind Kind;
    }
    public sealed class ComponentTask
    {
        public string Folder,Name,Xml;
    }
    public static class SoftwareCleanup
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool MoveFileEx(string existing,string replacement,int flags);
        public static string UserSid { get { using(var id=WindowsIdentity.GetCurrent()) return id.User.Value; } }
        public static string Quote(string value) { if(value.IndexOfAny(new[]{'"','\r','\n','\0'})>=0) throw new InvalidDataException("参数包含非法字符"); return "\""+value+"\""; }
        public static void ValidateRequest(CleanupRequest request)
        {
            if(request==null||request.Version!=1||request.UserSid!=UserSid||request.Selected==null||request.Selected.Count==0||request.Selected.Count>50||request.Selected.Any(x=>x==null)||request.Selected.Select(x=>x.Key).Distinct().Count()!=request.Selected.Count||Math.Abs((DateTime.UtcNow-request.Created).TotalMinutes)>30) throw new InvalidDataException("清理请求已失效或不属于当前用户。");
            SoftwareDatabase.Validate(request.Catalog);
            if(request.CatalogHash!=MaintenanceJson.Digest(MaintenanceJson.Encode(request.Catalog))) throw new InvalidDataException("名单校验不一致。");
        }
        public static List<CleanupResult> Execute(CleanupRequest request,string logDirectory,Action<CleanupResult> progress)
        {
            ValidateRequest(request); var results=new List<CleanupResult>(); Directory.CreateDirectory(logDirectory);
            foreach(var selected in request.Selected)
            {
                var result=new CleanupResult { Key=selected.Key,Name=selected.Name,Status="未执行" }; results.Add(result);
                try
                {
                    var rule=request.Catalog.Rules.Single(x=>x.Id==selected.RuleId);
                    var fresh=SoftwareScanner.Scan(new SoftwareCatalog { Rules=new List<SoftwareRule> { rule } });
                    var current=fresh.Findings.FirstOrDefault(x=>x.Key==selected.Key);
                    if(current==null) { result.Status="未执行"; result.Detail="目标已变化或不可读取，请重新扫描。"; }
                    else if(!current.Actionable||current.Fingerprint!=selected.Fingerprint) { result.Status="已跳过"; result.Detail="目标或卸载程序自确认后发生变化；请重新扫描。"; }
                    else
                    {
                        // Never consume paths, commands or App objects supplied by the request; all actions use a fresh inventory.
                        string itemLog=Path.Combine(logDirectory,current.Key); Directory.CreateDirectory(itemLog);
                        MaintenanceJson.Save(Path.Combine(itemLog,"before.json"),current);
                        if(rule.Kind=="ChromeExtension") RemoveChrome(rule,current,itemLog,result);
                        else if(rule.Kind=="Component") RemoveComponent(rule,current,itemLog,result);
                        else UninstallApp(current,result);
                    }
                }
                catch(Exception e) { result.Status="未完成"; result.Detail=e.Message; }
                MaintenanceJson.Save(Path.Combine(logDirectory,"results.json"),results);
                if(progress!=null) progress(result);
                // A timed-out uninstaller may still be active; do not launch another installer against it.
                if(result.Status=="卸载仍在运行")
                {
                    foreach(var remaining in request.Selected.Skip(results.Count)) results.Add(new CleanupResult { Key=remaining.Key,Name=remaining.Name,Status="未执行",Detail="前一卸载程序未退出；完成后重新选择清理。" });
                    break;
                }
            }
            MaintenanceJson.Save(Path.Combine(logDirectory,"results.json"),results); return results;
        }
        static void UninstallApp(SoftwareFinding finding,CleanupResult result)
        {
            var app=finding.App; var plan=Uninstall.Plan(app);
            if(!plan.Valid||!Uninstall.CommandUnchanged(app)) throw new IOException("卸载信息已变化："+plan.Reason);
            if(app.Msi) plan.Arguments+=" /passive /norestart";
            result.Operations.Add("启动卸载："+plan.FileName+" "+plan.Arguments);
            using(var p=Process.Start(new ProcessStartInfo(plan.FileName,plan.Arguments) { UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden }))
            {
                if(p==null) throw new IOException("无法获得卸载进程。");
                if(!p.WaitForExit(180000)) { result.Status="卸载仍在运行"; result.Detail="等待已超过 3 分钟，未强行结束进程；请查看厂商卸载界面。"; return; }
                result.Operations.Add("卸载退出码："+p.ExitCode);
                if(p.ExitCode!=0&&p.ExitCode!=3010) { result.Status="未完成"; result.Detail="卸载返回 "+p.ExitCode+"；没有删除其安装目录。"; return; }
                bool registered=Uninstall.StillRegistered(app), remains=Uninstall.SafeInstallRoot(app.Location)&&Directory.Exists(app.Location)&&Directory.EnumerateFileSystemEntries(app.Location).Any();
                result.Status=p.ExitCode==3010?"等待重启":registered||remains?"仍有残留":"已卸载";
                result.Detail=registered?"安装项仍存在，需要复查。":remains?"安装项已移除，目录仍有内容；未把可能的用户数据自动删除。":"安装项已移除；扫描范围内未发现安装目录内容。";
            }
        }
        public static string Executable(string raw)
        {
            string value=Environment.ExpandEnvironmentVariables(raw??"").Trim();
            if(value.StartsWith(@"\??\")) value=value.Substring(4);
            if(value.StartsWith("\"")) { int end=value.IndexOf('"',1); return end>1?value.Substring(1,end-1):""; }
            if(Path.IsPathRooted(value)&&Regex.IsMatch(value,@"\.(exe|sys|dll)$",RegexOptions.IgnoreCase)) return value;
            if(File.Exists(value)) return value;
            // Accept an unquoted path only when its executable boundary is unambiguous and exists.
            var match=Regex.Match(value,@"^(.+?\.(?:exe|sys|dll))(?:\s|$)",RegexOptions.IgnoreCase);
            return match.Success&&File.Exists(match.Groups[1].Value)?match.Groups[1].Value:"";
        }
        static List<RegistryChange> RelatedValues(string directory)
        {
            var list=new List<RegistryChange>();
            foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
            foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
            using(var root=RegistryKey.OpenBaseKey(hive,view))
            {
                foreach(string keyPath in new[]{@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"})
                using(var key=root.OpenSubKey(keyPath)) if(key!=null) foreach(string name in key.GetValueNames())
                {
                    object value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if(value is string&&Safety.IsWithin(Executable((string)value),directory)) list.Add(new RegistryChange { Hive=hive,View=view,Key=keyPath,Name=name,Value=value,Kind=key.GetValueKind(name) });
                }
                const string clsid=@"SOFTWARE\Classes\CLSID";
                using(var parent=root.OpenSubKey(clsid)) if(parent!=null) foreach(string id in parent.GetSubKeyNames())
                {
                    string sub=clsid+"\\"+id+@"\InprocServer32";
                    using(var key=root.OpenSubKey(sub)) if(key!=null)
                    {
                        object value=key.GetValue("",null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if(value is string&&Safety.IsWithin(Environment.ExpandEnvironmentVariables((string)value).Trim('"'),directory)) list.Add(new RegistryChange { Hive=hive,View=view,Key=sub,Name="",Value=value,Kind=key.GetValueKind("") });
                    }
                }
            }
            return list;
        }
        static void RemoveValue(RegistryChange change,CleanupResult result)
        {
            using(var root=RegistryKey.OpenBaseKey(change.Hive,change.View)) using(var key=root.OpenSubKey(change.Key,true))
            {
                if(key==null||!object.Equals(key.GetValue(change.Name,null,RegistryValueOptions.DoNotExpandEnvironmentNames),change.Value)) throw new IOException("注册值发生变化，停止移除："+change.Key);
                key.DeleteValue(change.Name,false); result.Operations.Add("清理加载项："+change.Hive+"\\"+change.Key+" / "+change.Name);
            }
        }
        static void RemoveComponent(SoftwareRule rule,SoftwareFinding finding,string log,CleanupResult result)
        {
            string directory=SoftwareScanner.ComponentRoot(rule);
            if(!SoftwareScanner.Equal(finding.Root,directory)) throw new IOException("组件目录已变化");
            var files=SafeTree.Enumerate(directory); // Validate the entire tree before stopping processes or deleting anything.
            var values=RelatedValues(directory); MaintenanceJson.Save(Path.Combine(log,"registry-values.json"),values);
            var tasks=FindTasks(directory); MaintenanceJson.Save(Path.Combine(log,"scheduled-tasks.json"),tasks);
            MaintenanceJson.Save(Path.Combine(log,"file-list.json"),files);
            var errors=new List<string>(); var apps=SoftwareScanner.Inventory(errors).Where(a=>SoftwareScanner.Equal(a.Id,rule.InstallId)&&SoftwareScanner.Equal(a.Publisher,rule.Publisher)).ToList();
            MaintenanceJson.Save(Path.Combine(log,"install-entries.json"),apps);
            bool driver=DisableServices(directory,log,result);
            StopOwnedProcesses(directory,result);
            foreach(var value in values) RemoveValue(value,result);
            RemoveTasks(tasks,directory,result);
            int queued=DeleteTree(directory,result);
            foreach(var app in apps)
            using(var root=RegistryKey.OpenBaseKey(app.Hive,app.View))
            {
                using(var key=root.OpenSubKey(app.Key))
                {
                    if(key==null) continue;
                    if(!SoftwareScanner.Equal(Convert.ToString(key.GetValue("Publisher","")),app.Publisher)||!SoftwareScanner.Equal(Convert.ToString(key.GetValue("DisplayName","")),app.Name)||Convert.ToString(key.GetValue("UninstallString",""))!=app.Command) throw new IOException("安装注册项发生变化，停止移除。");
                    var backup=new Dictionary<string,object>(); foreach(string name in key.GetValueNames()) backup[name]=new { Value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames),Kind=key.GetValueKind(name) };
                    MaintenanceJson.Save(Path.Combine(log,"uninstall-"+app.Hive+"-"+app.View+".json"),backup);
                }
                root.DeleteSubKeyTree(app.Key,false); result.Operations.Add("移除精确安装项："+app.Id);
            }
            result.Status=queued>0||driver?"等待重启":Directory.Exists(directory)?"仍有残留":"已清理";
            result.Detail=queued>0||driver?queued+" 个占用文件或目录已安排重启删除；相关驱动已标记删除。请保存工作后自行重启并复查。":"已复查组件目录。注册值备份及文件清单保存在清理记录中；未备份程序文件或用户数据。";
        }
        public static bool TaskOwned(IEnumerable<string> executables,string root)
        {
            var paths=executables.ToList(); return paths.Count>0&&paths.All(p=>!string.IsNullOrWhiteSpace(p)&&Path.IsPathRooted(Environment.ExpandEnvironmentVariables(p).Trim('"'))&&Safety.IsWithin(Environment.ExpandEnvironmentVariables(p).Trim('"'),root));
        }
        static bool TaskDefinitionOwned(dynamic definition,string root)
        {
            var paths=new List<string>(); dynamic actions=definition.Actions;
            try
            {
                for(int i=1;i<=actions.Count;i++)
                {
                    dynamic action=actions.Item(i);
                    try { if((int)action.Type!=0) return false; paths.Add((string)action.Path); }
                    finally { Marshal.FinalReleaseComObject(action); }
                }
            } finally { Marshal.FinalReleaseComObject(actions); }
            return TaskOwned(paths,root);
        }
        static List<ComponentTask> FindTasks(string root)
        {
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            var found=new List<ComponentTask>(); var folders=new Queue<string>(); folders.Enqueue("\\"); int visited=0;
            try
            {
                service.Connect();
                while(folders.Count>0)
                {
                    if(++visited>2000) throw new IOException("计划任务目录过多，停止清理。");
                    string path=folders.Dequeue(); dynamic folder=service.GetFolder(path); dynamic tasks=null,children=null;
                    try
                    {
                        tasks=folder.GetTasks(1);
                        for(int i=1;i<=tasks.Count;i++)
                        {
                            dynamic task=tasks.Item(i); dynamic definition=null;
                            try { definition=task.Definition; if(TaskDefinitionOwned(definition,root)) found.Add(new ComponentTask { Folder=path,Name=(string)task.Name,Xml=(string)task.Xml }); }
                            finally { if(definition!=null) Marshal.FinalReleaseComObject(definition); Marshal.FinalReleaseComObject(task); }
                        }
                        children=folder.GetFolders(0);
                        for(int i=1;i<=children.Count;i++) { dynamic child=children.Item(i); try { folders.Enqueue((string)child.Path); } finally { Marshal.FinalReleaseComObject(child); } }
                    }
                    finally { if(children!=null) Marshal.FinalReleaseComObject(children); if(tasks!=null) Marshal.FinalReleaseComObject(tasks); Marshal.FinalReleaseComObject(folder); }
                }
            } finally { Marshal.FinalReleaseComObject(service); }
            return found;
        }
        static void RemoveTasks(List<ComponentTask> tasks,string root,CleanupResult result)
        {
            if(tasks.Count==0) return;
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            try
            {
                service.Connect();
                foreach(var saved in tasks)
                {
                    dynamic folder=service.GetFolder(saved.Folder),task=null,definition=null;
                    try
                    {
                        task=folder.GetTask(saved.Name); definition=task.Definition;
                        if((string)task.Xml!=saved.Xml||!TaskDefinitionOwned(definition,root)) throw new IOException("计划任务已变化，停止移除："+saved.Name);
                        folder.DeleteTask(saved.Name,0); result.Operations.Add("移除组件计划任务："+saved.Folder+saved.Name);
                    }
                    finally { if(definition!=null) Marshal.FinalReleaseComObject(definition); if(task!=null) Marshal.FinalReleaseComObject(task); Marshal.FinalReleaseComObject(folder); }
                }
            } finally { Marshal.FinalReleaseComObject(service); }
        }
        static void StopOwnedProcesses(string root,CleanupResult result)
        {
            using(var search=new ManagementObjectSearcher("SELECT ProcessId,ExecutablePath FROM Win32_Process"))
            using(var rows=search.Get()) foreach(ManagementObject row in rows)
            {
                string path=Convert.ToString(row["ExecutablePath"]); int id=Convert.ToInt32(row["ProcessId"]);
                if(!Safety.IsWithin(path,root)||id==Process.GetCurrentProcess().Id) continue;
                try
                {
                    using(var p=Process.GetProcessById(id))
                    {
                        string live=p.MainModule.FileName; if(!SoftwareScanner.Equal(live,path)||!Safety.IsWithin(live,root)) continue;
                        var identity=new ProcessInfo { Pid=id,Path=live,Created=p.StartTime.ToUniversalTime().Ticks };
                        if(Safety.ProtectedReason(identity)!="") continue;
                        p.Kill(); p.WaitForExit(5000); result.Operations.Add("停止组件进程："+id+" "+live);
                    }
                } catch(ArgumentException) { } // Process already exited.
            }
        }
        static int Sc(string args)
        {
            using(var p=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe"),args) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden }))
            { if(!p.WaitForExit(15000)) throw new IOException("服务管理操作超时"); return p.ExitCode; }
        }
        static bool DisableServices(string directory,string log,CleanupResult result)
        {
            bool driver=false;
            using(var root=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
            foreach(string name in root.GetSubKeyNames())
            {
                using(var key=root.OpenSubKey(name))
                {
                    string image=Convert.ToString(key.GetValue("ImagePath",""));
                    if(!Safety.IsWithin(Executable(image),directory)) continue;
                    if(!Regex.IsMatch(name,@"^[a-zA-Z0-9_.-]+$")) throw new IOException("服务名称不适合安全处理："+name);
                    var backup=new Dictionary<string,object>(); foreach(string n in key.GetValueNames()) backup[n]=new { Value=key.GetValue(n,null,RegistryValueOptions.DoNotExpandEnvironmentNames),Kind=key.GetValueKind(n) };
                    MaintenanceJson.Save(Path.Combine(log,"service-"+name+".json"),backup);
                    int type=Convert.ToInt32(key.GetValue("Type",0));
                    if((type&3)!=0) driver=true;
                    int code=Sc("config "+Quote(name)+" start= disabled"); if(code!=0&&code!=1072) throw new IOException("无法禁用服务 "+name+"："+code);
                    // Kernel drivers remain loaded until reboot; never force-unload them.
                    if((type&3)==0) Sc("stop "+Quote(name));
                    code=Sc("delete "+Quote(name)); if(code!=0&&code!=1072&&code!=1060) throw new IOException("无法标记删除服务 "+name+"："+code);
                    result.Operations.Add("禁用并标记删除服务："+name+((type&3)!=0?"（驱动需重启复查）":""));
                }
            }
            return driver;
        }
        public static int DeleteTree(string root,CleanupResult result)
        {
            var entries=SafeTree.Enumerate(root).OrderByDescending(p=>p.Count(c=>c=='\\')).ThenByDescending(p=>p.Length).ToList(); entries.Add(root); int queued=0;
            foreach(string item in entries)
            {
                if(!File.Exists(item)&&!Directory.Exists(item)) continue;
                SafeTree.CheckPath(root,item); bool directory=(File.GetAttributes(item)&FileAttributes.Directory)!=0;
                try
                {
                    if(directory) Directory.Delete(item,false);
                    else { File.SetAttributes(item,File.GetAttributes(item)&~FileAttributes.ReadOnly); File.Delete(item); }
                }
                catch(Exception e)
                {
                    int error=e.HResult&0xffff;
                    if(!(e is IOException)&&!(e is UnauthorizedAccessException)) throw;
                    if(error!=5&&error!=32&&error!=33&&!(directory&&error==145&&queued>0)) throw;
                    if(!MoveFileEx(item,null,4)) throw new IOException("文件仍被占用且无法安排重启删除："+item+"，错误 "+Marshal.GetLastWin32Error());
                    queued++; result.Operations.Add("重启删除："+item);
                }
            }
            return queued;
        }
        public static Dictionary<string,object> MergeExtensionPolicy(string json,string id)
        {
            if(!Regex.IsMatch(id??"",@"^[a-p]{32}$")) throw new InvalidDataException("扩展 ID 不正确");
            Dictionary<string,object> policy;
            if(string.IsNullOrWhiteSpace(json)) policy=new Dictionary<string,object>();
            else { policy=MaintenanceJson.Serializer().DeserializeObject(json) as Dictionary<string,object>; if(policy==null) throw new InvalidDataException("现有 Chrome 策略不是对象，停止修改。"); }
            var previous=SoftwareScanner.Object(SoftwareScanner.Value(policy,id)); var next=new Dictionary<string,object>(previous);
            next["installation_mode"]="removed"; policy[id]=next; return policy;
        }
        static void RemoveChrome(SoftwareRule rule,SoftwareFinding finding,string log,CleanupResult result)
        {
            const string path=@"SOFTWARE\Policies\Google\Chrome";
            using(var machine=Registry.LocalMachine.OpenSubKey(path))
            {
                string policy=machine==null?"":Convert.ToString(machine.GetValue("ExtensionSettings",""));
                if(policy.Length>0)
                {
                    var values=MaintenanceJson.Serializer().Deserialize<Dictionary<string,object>>(policy);
                    var item=SoftwareScanner.Object(SoftwareScanner.Value(values,rule.ExtensionId));
                    var mode=Convert.ToString(SoftwareScanner.Value(item,"installation_mode"));
                    if(mode!="removed") throw new IOException("存在系统级 ExtensionSettings 策略；本版本不覆盖系统策略，请由管理员在原策略中移除该扩展。");
                }
            }
            using(var key=Registry.CurrentUser.CreateSubKey(path))
            {
                var old=key.GetValue("ExtensionSettings",null); var merged=MergeExtensionPolicy(Convert.ToString(old),rule.ExtensionId);
                MaintenanceJson.Save(Path.Combine(log,"chrome-policy-before.json"),new { Key=path,Name="ExtensionSettings",Value=old });
                string next=MaintenanceJson.Encode(merged); key.SetValue("ExtensionSettings",next,RegistryValueKind.String);
                if(Convert.ToString(key.GetValue("ExtensionSettings"))!=next) throw new IOException("Chrome 策略写入核验失败。");
            }
            result.Operations.Add("为单个扩展 ID 设置 installation_mode=removed："+rule.ExtensionId);
            result.Status="等待浏览器重启";
            result.Detail="移除策略已写入，Chrome 刷新策略或重启后生效。未改写 Secure Preferences、未关闭浏览器。可能显示“由贵单位管理”的本地策略提示。";
        }
    }
}
