using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PopupGuard
{
    public sealed class HotkeyWindow : NativeWindow,IDisposable
    {
        public const uint Modifiers=0x4002,Key=(uint)Keys.Down;
        readonly Action pick; readonly Action<int> command; public bool Registered;
        public HotkeyWindow(Action pick,Action<int> command)
        { this.pick=pick; this.command=command; CreateHandle(new CreateParams { Caption="PopupGuard.Control.v1" }); Registered=Native.RegisterHotKey(Handle,101,Modifiers,Key); }
        protected override void WndProc(ref Message m) { if(m.Msg==0x0312 && m.WParam.ToInt32()==101) pick(); if(m.Msg==0x8001) command(m.WParam.ToInt32()); base.WndProc(ref m); }
        public void Dispose() { Native.UnregisterHotKey(Handle,101); DestroyHandle(); }
    }
    public sealed class GuardContext : ApplicationContext
    {
        public readonly Monitor Monitor; public readonly MaintenanceController Maintenance; readonly NotifyIcon tray; readonly Dashboard dashboard; readonly HotkeyWindow hotkey;
        readonly ToolStripMenuItem pause; bool uiBusy; Picker picker; ReviewForm review; bool exiting,softwareNotice;
        public bool StartupAvailable=true;
        public bool HotkeyAvailable { get { return hotkey!=null && hotkey.Registered; } }
        public GuardContext(string data,bool showDashboard)
        {
            Monitor=new Monitor(new Store(data)); Maintenance=new MaintenanceController(data,null);
            hotkey=new HotkeyWindow(()=>Pick(null),id=>{ if(id==1) ShowDashboard(); if(id==2) Monitor.RestoreAll(); if(id==3) ExitThread(); if(id==4) Maintenance.Scan(false); if(id==5) ShowSoftwareDashboard(); }); dashboard=new Dashboard(this);
            // Keep a UI dispatcher alive even while the dashboard is hidden.
            var handle=dashboard.Handle;
            var menu=new ContextMenuStrip(); menu.Items.Add("打开守护面板",null,delegate { ShowDashboard(); });
            menu.Items.Add("点选广告  Ctrl+↓",null,delegate { Pick(null); });
            menu.Items.Add("恢复所有隐藏窗口",null,delegate { Monitor.RestoreAll(); });
            menu.Items.Add("扫描软件名单",null,delegate { Maintenance.Scan(true); });
            menu.Items.Add("软件巡检与批量清理",null,delegate { ShowSoftwareDashboard(); });
            pause=new ToolStripMenuItem("暂停守护"); pause.Click+=delegate { Monitor.Paused=!Monitor.Paused; pause.Text=Monitor.Paused?"恢复守护":"暂停守护"; dashboard.RefreshData(); }; menu.Items.Add(pause);
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出并恢复窗口",null,delegate { ExitThread(); });
            tray=new NotifyIcon { Icon=SystemIcons.Shield,Text="弹窗卫士 · 预置规则与已确认来源",ContextMenuStrip=menu,Visible=true };
            tray.DoubleClick+=delegate { ShowDashboard(); }; tray.BalloonTipClicked+=delegate { if(softwareNotice) ShowSoftwareDashboard(); else ShowDashboard(); };
            Maintenance.Found+=count=>{ softwareNotice=true; tray.ShowBalloonTip(6000,"软件名单发现 "+count+" 个新项目","打开“软件巡检”核对并勾选清理；未自动卸载。",ToolTipIcon.Info); };
            Monitor.Suspicious+=delegate(WindowInfo w)
            {
                if(uiBusy||exiting) return;
                if(IsFullScreen()) { softwareNotice=false; tray.ShowBalloonTip(4000,"发现待确认窗口","已记录来源，空闲时可在守护面板核对。",ToolTipIcon.Info); return; }
                dashboard.BeginInvoke(new Action(()=>Pick(w)));
            };
            if(showDashboard) ShowDashboard();
            else tray.ShowBalloonTip(3500,"弹窗卫士已在后台运行","Ctrl+↓ 点选广告；弹窗历史会保存局部截图。",ToolTipIcon.Info);
        }
        static bool IsFullScreen()
        {
            IntPtr h=Native.GetForegroundWindow(); Native.Rect r;
            if(!Native.GetWindowRect(h,out r)) return false; Rectangle screen=Screen.FromHandle(h).Bounds;
            return r.Left<=screen.Left&&r.Top<=screen.Top&&r.Right>=screen.Right&&r.Bottom>=screen.Bottom;
        }
        public void ShowDashboard() { dashboard.Show(); dashboard.WindowState=FormWindowState.Normal; dashboard.Activate(); dashboard.RefreshData(); }
        public void ShowSoftwareDashboard() { ShowDashboard(); dashboard.ShowSoftware(); }
        public void Pick(WindowInfo candidate)
        {
            if(uiBusy||exiting) return; uiBusy=true; Monitor.Busy=true; dashboard.Hide();
            picker=new Picker(Monitor.Inspector,delegate(WindowInfo w)
            {
                picker=null; uiBusy=false; Monitor.Busy=false; Monitor.Dismiss(candidate);
                if(w!=null) Review(w);
            },candidate!=null); picker.Show();
        }
        public async void Review(WindowInfo window)
        {
            if(window==null||uiBusy||exiting) return; uiBusy=true; Monitor.Busy=true;
            try { var evidence=await Monitor.ObserveAsync(window,"手动定位","用户点选窗口"); uiBusy=false; Monitor.Busy=false; if(!exiting) Review(evidence); }
            catch(Exception e) { uiBusy=false; Monitor.Busy=false; MessageBox.Show(e.Message,"读取来源失败"); }
        }
        public void Review(Activity evidence)
        {
            if(evidence==null||evidence.Window==null||uiBusy||exiting) return; uiBusy=true; Monitor.Busy=true;
            review=new ReviewForm(this,evidence.Window,evidence); review.FormClosed+=delegate { uiBusy=false; Monitor.Busy=false; review.Dispose(); review=null; dashboard.RefreshData(); }; review.Show();
        }
        public bool IsStartup()
        {
            try { using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return key!=null&&Convert.ToString(key.GetValue("PopupGuard",""))==StartupCommand; }
            catch(System.Security.SecurityException) { StartupAvailable=false; return false; }
            catch(UnauthorizedAccessException) { StartupAvailable=false; return false; }
            catch(IOException) { StartupAvailable=false; return false; }
        }
        string StartupCommand { get { return "\""+Application.ExecutablePath+"\" --tray"; } }
        public void SetStartup(bool enabled)
        { using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if(enabled) key.SetValue("PopupGuard",StartupCommand,RegistryValueKind.String); else key.DeleteValue("PopupGuard",false); } }
        protected override void ExitThreadCore()
        {
            if(Maintenance.Busy) { MessageBox.Show("软件扫描或清理正在执行，请完成后再退出。","弹窗卫士"); return; }
            if(exiting) return; exiting=true;
            if(picker!=null) picker.Close(); if(review!=null) review.Close();
            Maintenance.Dispose(); Monitor.Dispose(); CaptureBroker.Shutdown(); hotkey.Dispose(); tray.Visible=false; tray.Dispose(); dashboard.Quit(); dashboard.Dispose(); base.ExitThreadCore();
        }
        public void CaptureDashboard(string path)
        { ShowDashboard(); Application.DoEvents(); using(var bitmap=new Bitmap(dashboard.Width,dashboard.Height)) { dashboard.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(path); } }
    }
    public static class Program
    {
        [STAThread] public static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root=AppDomain.CurrentDomain.BaseDirectory;
            string data=Path.Combine(root,"data"); int pos=Array.IndexOf(args,"--data-dir"); if(pos>=0&&pos+1<args.Length) data=Path.GetFullPath(args[pos+1]);
            int cleanup=Array.IndexOf(args,"--cleanup-request");
            if(cleanup>=0)
            {
                try
                {
                    int sha=Array.IndexOf(args,"--request-sha256"); if(cleanup+1>=args.Length||sha<0||sha+1>=args.Length) return 2;
                    string request=Path.GetFullPath(args[cleanup+1]);
                    if(!Safety.IsWithin(request,Path.Combine(data,"cleanup-jobs"))||Path.GetFileName(request)!="request.json") return 2;
                    SafeTree.CheckPath(data,request);
                    var batch=MaintenanceJson.ReadVerified<CleanupRequest>(request,args[sha+1]); SoftwareCleanup.ValidateRequest(batch);
                    SoftwareCleanup.Execute(batch,Path.GetDirectoryName(request),null); return 0;
                }
                catch { return 1; }
            }
            if(args.Contains("--configure-daily"))
            {
                try { var db=new SoftwareDatabase(data); DailySoftwareTask.Set(data,true,db.State.Hour,db.State.Minute); db.State.DailyEnabled=true; db.Save(); return 0; }
                catch(Exception e) { File.WriteAllText(Path.Combine(data,"daily-task-error.txt"),e.ToString()); return 1; }
            }
            int report=Array.IndexOf(args,"--software-report");
            if(report>=0&&report+1<args.Length)
            {
                try { var db=new SoftwareDatabase(data); var scan=SoftwareScanner.Scan(db.Catalog); MaintenanceJson.Save(Path.GetFullPath(args[report+1]),scan); return scan.Errors.Count==0?0:1; }
                catch { return 2; }
            }
            bool created; string mutexId="Local\\PopupGuard.v1";
            using(var mutex=new Mutex(true,mutexId,out created))
            {
                if(!created)
                {
                    if(args.Contains("--tray")&&!args.Contains("--daily-scan")) return 0;
                    var control=Native.FindWindow(null,"PopupGuard.Control.v1");
                    if(control!=IntPtr.Zero) Native.PostMessage(control,0x8001,new IntPtr(args.Contains("--recover")?2:args.Contains("--stop")?3:args.Contains("--daily-scan")?4:args.Contains("--software-panel")?5:1),IntPtr.Zero);
                    return 0;
                }
                try
                {
                    if(args.Contains("--stop")) return 0;
                    if(args.Contains("--recover")) { using(var recovery=new Monitor(new Store(data))) recovery.Paused=true; return 0; }
                    Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e) { try { File.AppendAllText(Path.Combine(data,"errors.log"),DateTime.Now+" "+e.Exception+Environment.NewLine); } catch { } MessageBox.Show("操作未完成："+e.Exception.Message,"弹窗卫士"); };
                    using(var context=new GuardContext(data,args.Contains("--dashboard")))
                    {
                        if(!args.Contains("--ui-smoke")) context.Maintenance.ScanOnStartup();
                        int capture=Array.IndexOf(args,"--ui-smoke");
                        if(capture>=0&&capture+1<args.Length)
                        {
                            var t=new System.Windows.Forms.Timer { Interval=1200 }; t.Tick+=delegate { t.Stop(); if(args.Contains("--software-panel")) context.ShowSoftwareDashboard(); context.CaptureDashboard(args[capture+1]); context.ExitThread(); t.Dispose(); }; t.Start();
                        }
                        Application.Run(context);
                    }
                    return 0;
                }
                catch(Exception ex) { MessageBox.Show("无法启动弹窗卫士："+ex.Message,"弹窗卫士",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
