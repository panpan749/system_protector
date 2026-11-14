using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PopupGuard
{
    public static class DailySoftwareTask
    {
        public static string Name(string data) { return "PopupGuard.DailyScan."+SoftwareCleanup.UserSid+"."+MaintenanceJson.Digest(Path.GetFullPath(data).ToUpperInvariant()).Substring(0,12); }
        public static string Xml(string executable,string data,int hour,int minute)
        {
            if(hour<0||hour>23||minute<0||minute>59) throw new ArgumentOutOfRangeException("time");
            string sid=SoftwareCleanup.UserSid, start=DateTime.Today.AddHours(hour).AddMinutes(minute).ToString("yyyy-MM-dd'T'HH:mm:ss");
            string args="--daily-scan --tray --data-dir "+SoftwareCleanup.Quote(Path.GetFullPath(data));
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"+
                "<RegistrationInfo><Description>弹窗卫士每日软件名单检查；只读扫描，清理需用户确认。</Description></RegistrationInfo>"+
                "<Triggers><CalendarTrigger><StartBoundary>"+start+"</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>"+
                "<LogonTrigger><Enabled>true</Enabled><UserId>"+sid+"</UserId><Delay>PT30S</Delay></LogonTrigger></Triggers>"+
                "<Principals><Principal id=\"Author\"><UserId>"+sid+"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"+
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"+
                "<StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><Enabled>true</Enabled><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>"+
                "<Actions Context=\"Author\"><Exec><Command>"+SecurityElement.Escape(executable)+"</Command><Arguments>"+SecurityElement.Escape(args)+"</Arguments><WorkingDirectory>"+SecurityElement.Escape(Path.GetDirectoryName(executable))+"</WorkingDirectory></Exec></Actions></Task>";
        }
        public static void Set(string data,bool enabled,int hour,int minute)
        {
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); dynamic folder=null;
            try
            {
                service.Connect(); folder=service.GetFolder("\\");
                if(enabled)
                {
                    string exe=typeof(Program).Assembly.Location;
                    dynamic task=folder.RegisterTask(Name(data),Xml(exe,data,hour,minute),6,SoftwareCleanup.UserSid,null,3,null);
                    Marshal.FinalReleaseComObject(task);
                }
                else
                {
                    try { folder.DeleteTask(Name(data),0); } catch(FileNotFoundException) { } catch(COMException e) { if((e.ErrorCode&0xffff)!=2) throw; }
                }
            }
            finally { if(folder!=null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
        }
        public static bool Exists(string data)
        {
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); dynamic folder=null;
            try { service.Connect(); folder=service.GetFolder("\\"); dynamic task=folder.GetTask(Name(data)); bool enabled=task.Enabled; Marshal.FinalReleaseComObject(task); return enabled; }
            catch(COMException e) { if((e.ErrorCode&0xffff)==2) return false; throw; }
            catch(FileNotFoundException) { return false; }
            finally { if(folder!=null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
        }
    }
    public sealed class MaintenanceController : IDisposable
    {
        public readonly SoftwareDatabase Database; readonly Timer timer=new Timer { Interval=60000 };
        readonly Control dispatcher; readonly bool ownsDispatcher;
        public bool Busy { get; private set; } public string Progress=""; bool disposed;
        public event Action Changed; public event Action<int> Found;
        public MaintenanceController(string data,Control dispatcher)
        {
            ownsDispatcher=dispatcher==null; this.dispatcher=dispatcher??new Control();
            // Keep a UI-owned dispatch handle even when no dashboard or review form is visible.
            var handle=this.dispatcher.Handle;
            Database=new SoftwareDatabase(data);
            timer.Tick+=delegate { if(Database.Due(DateTime.Now)) Scan(false); }; timer.Start();
        }
        void Dispatch(Action action)
        {
            if(disposed||dispatcher.IsDisposed) return;
            if(dispatcher.InvokeRequired)
            {
                try { dispatcher.BeginInvoke(new Action(()=>{ if(!disposed&&!dispatcher.IsDisposed) action(); })); }
                catch(InvalidOperationException) { if(!disposed&&!dispatcher.IsDisposed) throw; }
            }
            else action();
        }
        public void Notify() { Dispatch(()=>{ if(Changed!=null) Changed(); }); }
        public void ScanOnStartup() { Scan(Database.StartupRecheckNeeded); }
        public void RefreshView() { if(Database.State.LastAttempt==DateTime.MinValue||(DateTime.Now-Database.State.LastAttempt).TotalSeconds>=30) Scan(true); }
        public SoftwareRule AddInstalled(InstalledApp selected,string reason)
        {
            if(disposed||Busy) throw new InvalidOperationException("请等待当前软件扫描或清理完成后再添加。");
            if(Database.Warning.Length>0) throw new InvalidOperationException(Database.Warning);
            var errors=new List<string>();
            var current=SoftwareScanner.Inventory(errors).FirstOrDefault(a=>SoftwareScanner.SameRegistration(a,selected));
            if(current==null) throw new InvalidOperationException("安装信息已经变化或无法读取，请重新核对来源。");
            var rule=Database.AddInstalled(current,reason); Notify(); Scan(true); return rule;
        }
        public async void Scan(bool force)
        {
            if(disposed||Busy||(!force&&!Database.Due(DateTime.Now))) return;
            Busy=true; Progress="正在检查安装项、组件目录和 Chrome 扩展…"; Notify();
            try
            {
                Database.State.LastAttempt=DateTime.Now; Database.Save();
                string catalogText=MaintenanceJson.Encode(Database.Catalog); var old=Database.State.Latest;
                var catalog=MaintenanceJson.Serializer().Deserialize<SoftwareCatalog>(catalogText);
                var scan=await Task.Run(()=>SoftwareScanner.Scan(catalog)); if(disposed) return;
                var previous=new HashSet<string>(old.Findings.Select(f=>f.Key+"|"+f.Fingerprint));
                Database.ApplyScan(scan,DateTime.Now);
                int added=scan.Findings.Count(f=>Database.Pending(f)&&!previous.Contains(f.Key+"|"+f.Fingerprint));
                Progress="检查完成："+scan.Findings.Count+" 项命中"+(scan.Errors.Count>0?"；"+scan.Errors.Count+" 项读取异常":""); Database.Save();
                if(added>0) Dispatch(()=>{ if(Found!=null) Found(added); });
            }
            catch(Exception e) { Progress="扫描未完成："+e.Message; }
            finally { Busy=false; Notify(); }
        }
        public void Configure(bool enabled,int hour,int minute)
        {
            if(Busy) throw new InvalidOperationException("请等待当前操作完成。");
            DailySoftwareTask.Set(Database.DirectoryPath,enabled,hour,minute);
            Database.State.DailyEnabled=enabled; Database.State.Hour=hour; Database.State.Minute=minute; Database.Save(); Notify();
        }
        public async Task Cleanup(List<SoftwareFinding> selected)
        {
            if(Busy||disposed) return;
            if(selected.Count==0||selected.Any(f=>!f.Actionable||!Database.Pending(f))) throw new InvalidOperationException("请选择可清理且未保留的命中项。");
            Busy=true; Progress="等待清理授权，随后逐项处理…"; Notify();
            try
            {
                string job=Path.Combine(Database.DirectoryPath,"cleanup-jobs",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
                string requestPath=Path.Combine(job,"request.json");
                var request=new CleanupRequest { UserSid=SoftwareCleanup.UserSid,Catalog=Database.Catalog,CatalogHash=Database.CatalogHash,Selected=selected };
                MaintenanceJson.Save(requestPath,request); string hash=Safety.HashFile(requestPath);
                using(var process=Process.Start(new ProcessStartInfo(typeof(Program).Assembly.Location,"--cleanup-request "+SoftwareCleanup.Quote(requestPath)+" --request-sha256 "+hash+" --data-dir "+SoftwareCleanup.Quote(Database.DirectoryPath)) { UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden }))
                {
                    if(process==null) throw new IOException("无法启动清理助手。");
                    await Task.Run(()=>process.WaitForExit());
                    string output=Path.Combine(job,"results.json");
                    if(!File.Exists(output)) throw new IOException("清理助手未返回结果，退出码 "+process.ExitCode+"。请检查 "+job);
                    var results=MaintenanceJson.Read<List<CleanupResult>>(output);
                    if(results==null) throw new IOException("清理结果格式无效。");
                    foreach(var result in results) result.Time=SoftwareDatabase.Local(result.Time);
                    Database.State.Results.InsertRange(0,results); Database.State.Results=Database.State.Results.Take(500).ToList(); Database.Save();
                    Progress="清理已返回；正在复查…";
                }
            }
            catch(System.ComponentModel.Win32Exception e) { Progress=e.NativeErrorCode==1223?"用户取消了管理员授权，未执行清理。":"无法启动清理："+e.Message; }
            catch(Exception e) { Progress="清理未完成："+e.Message; }
            finally { Busy=false; Notify(); }
            // Keep the explicit cleanup outcome visible; inventory is refreshed without overwriting error feedback.
            if(!disposed)
            {
                string message=Progress; Busy=true; Notify();
                try { var scan=await Task.Run(()=>SoftwareScanner.Scan(Database.Catalog)); Database.State.Latest=scan; Database.Save(); }
                catch(Exception e) { Progress=message+"；复查失败："+e.Message; }
                finally { Busy=false; if(Progress=="清理已返回；正在复查…") Progress="清理完成；逐项结果见“清理记录”。"; Notify(); }
            }
        }
        public void Dispose() { disposed=true; timer.Stop(); timer.Dispose(); if(ownsDispatcher) dispatcher.Dispose(); }
    }
}
