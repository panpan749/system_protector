using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;
using PopupGuard;

public class Tests
{
    static int passed,failed,skipped;
    static void Check(string name,bool value) { Console.WriteLine((value?"PASS ":"FAIL ")+name); if(value) passed++; else failed++; }
    static void Skip(string name) { skipped++; Console.WriteLine("SKIP "+name); }
    static void Pump(int ms) { var watch=Stopwatch.StartNew(); while(watch.ElapsedMilliseconds<ms) { Application.DoEvents(); Thread.Sleep(15); } }
    static void WaitFor(Func<bool> condition) { var watch=Stopwatch.StartNew(); while(!condition()&&watch.ElapsedMilliseconds<10000) Pump(50); }
    static bool ContainsDemo(Image image)
    {
        if(image==null) return false;
        using(var bitmap=new Bitmap(image))
        {
            int blue=0,light=0,total=0;
            for(int y=0;y<bitmap.Height;y+=3) for(int x=0;x<bitmap.Width;x+=3)
            { var c=bitmap.GetPixel(x,y); total++; if(Math.Abs(c.R-22)<5&&Math.Abs(c.G-45)<5&&Math.Abs(c.B-63)<5) blue++; if(c.R>200&&c.G>200&&c.B>200) light++; }
            return blue>total*.35&&light>total*.005;
        }
    }
    static T Field<T>(object obj,string name) { return (T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj); }
    static bool ButtonsFitVertically(Control parent) { foreach(Control child in parent.Controls) { if(child is Button&&child.Visible&&child.Bottom>parent.ClientSize.Height) return false; if(!ButtonsFitVertically(child)) return false; } return true; }
    static WindowInfo PresetWindow(string path,string title)
    { return new WindowInfo { Process=new ProcessInfo { Pid=-10,Created=1,Path=path },Title=title,Class="Popup",Bounds=new Rectangle(1300,800,420,250),Dpi=96,Style=0xC00000,ExStyle=0x88 }; }
    static void CheckPresets(string root)
    {
        var catalog=new PresetCatalog(); var settings=new Settings();
        var cases=new[]{
            PresetWindow(@"C:\Program Files\360\360Safe\360tray.exe","卫士精选"),
            PresetWindow(@"C:\Users\Alice\AppData\Roaming\360huabao\360huabao.exe","360壁纸推荐"),
            PresetWindow(@"C:\Program Files\2345Soft\HaoZip\Helper_Haozip.exe","今日热点"),
            PresetWindow(@"C:\Apps\Kingsoft\WPS Office\12.0\office6\wpsnotify.exe","WPS热点"),
            PresetWindow(@"C:\Apps\FlashCenter\MiniPage.exe","迷你首页"),
            PresetWindow(@"C:\Apps\FlashCenter\FlashHelperService.exe","Flash精选"),
            PresetWindow(@"C:\Apps\LuDaShi\5.0\MiniNews.exe","迷你新闻页"),
            PresetWindow(@"C:\Apps\KuaiZip\KuaizipUpdate.exe","快压推荐"),
            PresetWindow(@"C:\Apps\SogouInput\15.7\biz_helper.exe","搜狗推荐")
        };
        foreach(var w in cases)
        {
            string original=w.Title; string id=Path.GetFileName(w.Process.Path);
            Check("preset positive "+id,catalog.Match(w,settings)!=null);
            w.Style|=0x40000; Check("preset preserves resizable main window "+id,catalog.Match(w,settings)==null); w.Style&=~0x40000;
            w.Title=original+"设置"; Check("preset preserves settings "+id,catalog.Match(w,settings)==null);
            w.Title="文档讨论："+original; Check("preset refuses title keyword alone "+id,catalog.Match(w,settings)==null);
            w.Title=original; w.Process.Path=@"C:\Unrelated\"+id; Check("preset refuses unrelated component location "+id,catalog.Match(w,settings)==null);
        }
        var sample=PresetWindow(@"C:\Apps\FlashCenter\MiniPage.exe","Flash精选");
        settings.EnablePresets=false; Check("global preset switch disables matching",catalog.Match(sample,settings)==null); settings.EnablePresets=true;
        settings.DisabledPresetIds.Add("flash-mini"); Check("individual preset switch disables matching",catalog.Match(sample,settings)==null);
        var persisted=new Store(Path.Combine(root,"preset-settings")); persisted.Settings=settings; persisted.Save();
        Check("individual preset switch survives restart",catalog.Match(sample,new Store(persisted.DirectoryPath).Settings)==null); settings.DisabledPresetIds.Clear();
        sample.Process.Path=@"C:\Apps\FlashCenterBackup\MiniPage.exe"; Check("preset directory requires whole segment",catalog.Match(sample,settings)==null);
        sample.Process.Path=@"C:\Apps\FlashCenter\chrome.exe"; Check("browser cannot match preset",catalog.Match(sample,settings)==null);
        sample.Process.Path=Path.Combine(Environment.SystemDirectory,@"FlashCenter\MiniPage.exe"); Check("system component never matched",catalog.Match(sample,settings)==null);
        sample.Process.Path=@"C:\Apps\FlashCenter\MiniPage.exe"; sample.Style|=0x10000; Check("maximizable main window excluded",catalog.Match(sample,settings)==null); sample.Style&=~0x10000;
        sample.Bounds=new Rectangle(0,0,1920,1080); Check("full screen window excluded",catalog.Match(sample,settings)==null);
        sample.Bounds=new Rectangle(0,0,420,250); sample.ExStyle=0; Check("ordinary titled window excluded",catalog.Match(sample,settings)==null); sample.ExStyle=0x88;
        sample.Title="Flash精选 - 病毒风险提醒"; Check("security reminder excluded even with promotion prefix",catalog.Match(sample,settings)==null);
        sample.Title="Flash精选 - 总结.pdf"; Check("document title excluded even with promotion prefix",catalog.Match(sample,settings)==null);
        var wallpaper=PresetWindow(@"C:\Users\Alice\AppData\Roaming\360huabao\360huabao.exe","360壁纸"); Check("wallpaper main UI preserved",catalog.Match(wallpaper,settings)==null);
        Check("360 ChromeX protected",Safety.ProtectedReason(new ProcessInfo { Pid=-1,Created=1,Path=@"C:\Apps\360ChromeX.exe" })!="");
    }
    static void LivePreset(string root,string demoPath,bool flash=false)
    {
        // MiniPage is independently closed on this desktop; retain it as an explicit diagnostic case.
        string id=flash?"flash-mini":"360-news";
        string folder=Path.Combine(root,flash?"FlashCenter":"360Safe"); Directory.CreateDirectory(folder); string fixture=Path.Combine(folder,flash?"MiniPage.exe":"360tray.exe"); File.Copy(demoPath,fixture);
        using(var demo=Process.Start(fixture,(flash?"Flash精选":"卫士精选")+" --automated-test"))
        {
            try
            {
                IntPtr hwnd=IntPtr.Zero; for(int i=0;i<80&&hwnd==IntPtr.Zero;i++)
                {
                    Pump(100);
                    if(demo.HasExited) { Skip("live preset fixture exited before any guard monitor started; exit code="+demo.ExitCode+" (see demo log)"); return; }
                    demo.Refresh(); hwnd=demo.MainWindowHandle;
                }
                Check("preset fixture exists",hwnd!=IntPtr.Zero); if(hwnd==IntPtr.Zero) throw new Exception("Preset fixture unavailable");
                var store=new Store(Path.Combine(root,"preset-live")); store.Settings.AutoPrompt=false; store.Settings.EnablePresets=false;
                using(var monitor=new PopupGuard.Monitor(store,demo.Id))
                {
                    Pump(2300); Check("live global disable leaves popup visible",Native.IsWindowVisible(hwnd));
                    if(!Native.IsWindowVisible(hwnd)) Console.WriteLine("PRESET DEBUG exited="+demo.HasExited+" code="+(demo.HasExited?demo.ExitCode.ToString():"running")+" exists="+Native.IsWindow(hwnd)+" class="+Native.Class(hwnd));
                    store.Settings.EnablePresets=true; store.Settings.DisabledPresetIds.Add(id); monitor.Rescan(); Pump(2300);
                    Check("live individual disable leaves popup visible",Native.IsWindowVisible(hwnd));
                    var w=monitor.Inspector.ReadWindow(hwnd,false); w.Hash=Safety.HashFile(w.Process.Path); store.Settings.Rules.Add(Rule.From(w,true,false));
                    store.Settings.DisabledPresetIds.Clear(); monitor.Rescan(); Pump(2300); Check("manual whitelist overrides preset",Native.IsWindowVisible(hwnd)&&monitor.BlockCount==0);
                    store.Settings.Rules.Clear(); monitor.Rescan(); WaitFor(()=>!Native.IsWindowVisible(hwnd));
                    Check("preset auto hides without manual blacklist",!Native.IsWindowVisible(hwnd)&&monitor.BlockCount>0&&store.Settings.Rules.Count==0);
                    var blocked=monitor.History.FirstOrDefault(x=>x.Action=="已请求隐藏"&&x.Reason.Contains(id));
                    Check("preset audit retains rule ID hash ancestry",blocked!=null&&blocked.EvidenceReady&&blocked.Window.Hash.Length==64&&blocked.Chain.Count>0);
                    if(EvidenceStore.DesktopAvailable()) Check("preset audit retains screenshot",blocked!=null&&File.Exists(monitor.Evidence.ImagePath(blocked.ScreenshotFile)));
                    else Check("preset on locked desktop records missing-image reason",blocked!=null&&blocked.ScreenshotFile==""&&blocked.ScreenshotStatus.Contains("锁定"));
                    monitor.RestoreAll(); Pump(2600); Check("preset restore and temporary bypass work",Native.IsWindowVisible(hwnd));
                    Check("preset never terminates source process",!demo.HasExited);
                    if(demo.HasExited) Console.WriteLine("PRESET DEBUG early exit code="+demo.ExitCode);
                }
            }
            finally { if(!demo.HasExited) demo.CloseMainWindow(); demo.WaitForExit(3000); }
        }
    }
    [STAThread] public static int Main(string[] args)
    {
        Application.EnableVisualStyles(); string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data",DateTime.Now.ToString("yyyyMMddHHmmssfff")); Directory.CreateDirectory(root);
        if(args.Contains("--preset-only")) { LivePreset(root,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PopupGuard.Demo.exe"),args.Contains("--flash-fixture")); Console.WriteLine("RESULT "+passed+" passed / "+failed+" failed / "+skipped+" skipped"); return failed==0?0:1; }
        CheckPresets(root);
        Check("shortcut is Ctrl Down without Alt",HotkeyWindow.Modifiers==0x4002&&HotkeyWindow.Key==(uint)Keys.Down);
        var fake=new WindowInfo { Process=new ProcessInfo { Pid=123,Created=100,Path=@"C:\Apps\Vendor\ad.exe" },Class="AdClass",Title="广告",Hash=new string('A',64),Bounds=new Rectangle(1520,800,400,240),Style=0,ExStyle=0x88 };
        var rule=Rule.From(fake,false,false);
        Check("exact rule matches",rule.Matches(fake)); fake.Process.Pid=456; Check("rule independent of recycled PID",rule.Matches(fake));
        fake.Hash=new string('B',64); Check("file update invalidates rule",!rule.Matches(fake)); fake.Hash=rule.Hash;
        fake.Title="document"; Check("normal title not blocked",!rule.Matches(fake)); rule.AnyTitle=true; Check("explicit class scope supports rotating title",rule.Matches(fake));
        fake.Class="MainWindow"; Check("window class remains scoped",!rule.Matches(fake)); fake.Class="AdClass";
        rule.Enabled=false; Check("disabled rules ignored",!rule.Matches(fake));
        Check("directory boundary rejects sibling prefix",!Safety.IsWithin(@"C:\Apps\VendorExtra\a.exe",@"C:\Apps\Vendor"));
        Check("directory child accepted",Safety.IsWithin(@"C:\Apps\Vendor\bin\a.exe",@"C:\Apps\Vendor"));
        Check("PID reuse rejected",!Safety.SameInstance(new ProcessInfo { Pid=5,Created=20,Path="x" },new ProcessInfo { Pid=5,Created=21,Path="x" }));
        Check("unknown creation rejected",!Safety.SameInstance(new ProcessInfo { Pid=5 },new ProcessInfo { Pid=5 }));
        Check("browser protected",Safety.ProtectedReason(new ProcessInfo { Pid=-1,Created=1,Path=@"C:\Apps\chrome.exe" })!="");
        Check("Windows protected",Safety.ProtectedReason(new ProcessInfo { Pid=-1,Created=1,Path=Path.Combine(Environment.SystemDirectory,"notepad.exe") })!="");
        fake.Title="广告"; Classifier.Score(fake,new Rectangle(0,0,1920,1040)); Check("ad candidate above confirmation threshold",fake.Score>=55);
        var normal=new WindowInfo { Title="Document",Bounds=new Rectangle(100,100,1000,700),Style=0xC00000 }; Classifier.Score(normal,new Rectangle(0,0,1920,1040)); Check("normal editor below threshold",normal.Score<55);
        var store=new Store(root); store.Settings.Rules.Add(rule); store.Save(); Check("JSON roundtrip",new Store(root).Settings.Rules.Count==1);
        store.Save(); Check("atomic save retains backup",File.Exists(Path.Combine(root,"settings.json.bak")));
        File.WriteAllText(Path.Combine(root,"settings.json"),"broken"); var recovered=new Store(root); Check("corrupt config fails safe",recovered.Settings.Rules.Count==0&&recovered.Warning!="");
        File.WriteAllText(Path.Combine(root,"settings.json"),"{\"Version\":1,\"AutoPrompt\":false,\"Rules\":[]}"); Check("old settings enable new screenshot feature by default",new Store(root).Settings.CaptureScreenshots);
        Check("old settings migrate preset defaults",new Store(root).Settings.EnablePresets&&new Store(root).Settings.DisabledPresetIds.Count==0);
        var diskEvidence=new EvidenceStore(new Store(Path.Combine(root,"disk")));
        Check("screenshot path traversal rejected",diskEvidence.ImagePath("..\\..\\secret.png")==""&&diskEvidence.ImagePath(@"C:\secret.png")=="");
        Check("drive root rejected for attribution",!Uninstall.SafeInstallRoot(@"C:\")); Check("Program Files root rejected",!Uninstall.SafeInstallRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Check("relative uninstall rejected",!Uninstall.Plan(new InstalledApp { Command="cmd.exe /c erase",Location=root }).Valid);
        Check("ambiguous unquoted executable rejected",!Uninstall.Plan(new InstalledApp { Command=@"C:\Program Files\App\uninstall.exe /remove",Location=root }).Valid);
        var msi=Uninstall.Plan(new InstalledApp { Msi=true,Id="{12345678-1234-1234-1234-123456789ABC}",Command="ignored" }); Check("MSI rebuilt without arbitrary command",msi.Valid&&msi.Arguments=="/x {12345678-1234-1234-1234-123456789abc}");
        string demoPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PopupGuard.Demo.exe");
        Check("direct in-folder executable plan",Uninstall.Plan(new InstalledApp { Command="\""+demoPath+"\" /uninstall",Location=AppDomain.CurrentDomain.BaseDirectory }).Valid);
        Check("uninstaller outside install directory rejected",!Uninstall.Plan(new InstalledApp { Command="\""+demoPath+"\"",Location=root }).Valid);
        Process demo=null;
        try
        {
            demo=Process.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PopupGuard.Demo.exe"),"--automated-test"); IntPtr hwnd=IntPtr.Zero;
            for(int i=0;i<80&&hwnd==IntPtr.Zero;i++) { Pump(100); demo.Refresh(); hwnd=demo.MainWindowHandle; }
            Check("native demo window exists",hwnd!=IntPtr.Zero); if(hwnd==IntPtr.Zero) throw new Exception("Demo unavailable");
            var inspector=new Inspector(); var w=inspector.ReadWindow(hwnd,false);
            Check("HWND resolves exact PID",w!=null&&w.Process.Pid==demo.Id); Check("process creation and image readable",w.Process.Created>0&&w.Process.Path.EndsWith("PopupGuard.Demo.exe"));
            var chain=inspector.Trace(w.Process); Check("parent chain includes test runner",chain.Any(p=>p.Pid==Process.GetCurrentProcess().Id));
            var recycled=new ProcessInfo { Pid=w.Process.Pid,Created=w.Process.Created-1,Path=w.Process.Path }; Check("stale leaf cannot acquire new parent chain",inspector.Trace(recycled).Count==1);
            var liveStore=new Store(Path.Combine(root,"live")); liveStore.Settings.AutoPrompt=false;
            using(var monitor=new PopupGuard.Monitor(liveStore,demo.Id))
            {
                Check("native event hooks registered",monitor.HookAvailable); Pump(1200);
                Check("unknown popup remains visible",Native.IsWindowVisible(hwnd));
                var observed=monitor.History.FirstOrDefault(x=>x.Action=="待确认"&&x.Window.Process.Pid==demo.Id);
                if(observed!=null) WaitFor(()=>observed.ScreenshotStatus!=EvidenceStore.CapturingStatus&&observed.EvidenceReady);
                Check("unknown popup evidence captured automatically",observed!=null&&observed.EvidenceReady&&observed.Window.Hash.Length==64&&observed.Chain.Count>0);
                if(EvidenceStore.DesktopAvailable()) Check("popup region screenshot saved",observed!=null&&File.Exists(monitor.Evidence.ImagePath(observed.ScreenshotFile)));
                else { Check("locked desktop cannot create misleading screenshot",observed!=null&&observed.ScreenshotFile==""&&observed.ScreenshotStatus.Contains("锁定")); Skip("unlocked screen capture requires accessible desktop"); }
                if(observed!=null&&!string.IsNullOrEmpty(observed.ScreenshotFile)) using(var photo=monitor.Evidence.LoadImage(observed.ScreenshotFile))
                { Check("screenshot restricted to window bounds",photo!=null&&photo.Width<=w.Bounds.Width&&photo.Height<=w.Bounds.Height&&Math.Max(photo.Width,photo.Height)<=2048); Check("automatic screenshot contains actual demo background and text",ContainsDemo(photo)); }
                var stored=monitor.Evidence.Load().FirstOrDefault(x=>observed!=null&&x.Id==observed.Id);
                Check("history persists image fingerprint ancestry across reload",stored!=null&&stored.Window.Hash==observed.Window.Hash&&stored.ScreenshotFile==observed.ScreenshotFile&&stored.Chain.Count==observed.Chain.Count);
                if(observed.ScreenshotFile!="") Check("window-capture provenance survives reload",stored.ScreenshotMethod==EvidenceStore.WindowCaptureMethod);
                Check("history timestamps stay in local timezone after reload",stored!=null&&Math.Abs((stored.Time-observed.Time).TotalSeconds)<1&&stored.Time.Kind==DateTimeKind.Local);
                Check("loaded history never reuses old HWND",stored!=null&&stored.Window.Handle==IntPtr.Zero);
                if(EvidenceStore.DesktopAvailable())
                {
                    var manual=monitor.Record("手动定位",w,"fresh capture regression");
                    Check("manual selection captures a new image instead of cached prior content",manual.ScreenshotFile!=""&&manual.ScreenshotFile!=observed.ScreenshotFile);
                    // Simulate the pending record left by a locked session without locking the user's desktop.
                    var missing=new Activity { Action="待确认",Window=EvidenceStore.Copy(w),Time=DateTime.Now.AddMinutes(-2),ScreenshotStatus=EvidenceStore.DesktopUnavailableStatus };
                    monitor.History.Insert(0,missing); int before=monitor.History.Count;
                    Field<System.Collections.Generic.HashSet<string>>(monitor,"captureAfterUnlock").Add(w.Key);
                    WaitFor(()=>monitor.History.Count>before);
                    var retried=monitor.History.First();
                    WaitFor(()=>retried.ScreenshotStatus!=EvidenceStore.CapturingStatus);
                    Check("visible window missed during lock is captured on next unlocked scan",retried!=missing&&retried.Time>missing.Time&&monitor.Evidence.HasImage(retried.ScreenshotFile));
                    if(retried==missing||!monitor.Evidence.HasImage(retried.ScreenshotFile)) Console.WriteLine("RETRY DEBUG exited="+demo.HasExited+" hwnd="+Native.IsWindow(hwnd)+" desktop="+EvidenceStore.DesktopAvailable()+" status="+retried.ScreenshotStatus+" count="+monitor.History.Count+" pending="+Field<System.Collections.Generic.HashSet<string>>(monitor,"captureAfterUnlock").Count);
                    Check("unlock retry preserves historical absence instead of inventing an old image",missing.ScreenshotFile==""&&missing.ScreenshotStatus==EvidenceStore.DesktopUnavailableStatus);
                }
                liveStore.Settings.CaptureScreenshots=false; var disabled=monitor.Evidence.Capture(w,inspector); Check("disabling screenshot prevents new image",disabled.File==""&&disabled.Status.Contains("关闭")); liveStore.Settings.CaptureScreenshots=true;
                w.Hash=Safety.HashFile(w.Process.Path); liveStore.Settings.Rules.Add(Rule.From(w,false,false)); liveStore.Save(); monitor.Rescan();
                var allowRule=Rule.From(w,true,false); liveStore.Settings.Rules.Add(allowRule); Pump(2500); Check("allow rule takes precedence over block",Native.IsWindowVisible(hwnd)); liveStore.Settings.Rules.Remove(allowRule); monitor.Rescan();
                WaitFor(()=>!Native.IsWindowVisible(hwnd)); Check("confirmed rule automatically hides popup",!Native.IsWindowVisible(hwnd)); Check("hidden window recorded for recovery",monitor.Hidden.Count==1);
                monitor.RestoreAll(); Pump(400); Check("restore makes popup visible",Native.IsWindowVisible(hwnd)); Pump(2400); Check("restore bypass prevents immediate rehide",Native.IsWindowVisible(hwnd));
                monitor.Paused=true; monitor.Hide(w,"test recovery journal"); Pump(300); Check("explicit hide works",!Native.IsWindowVisible(hwnd));
                // A second monitor recovers the crash journal without sharing in-memory hidden state.
                using(var rescue=new PopupGuard.Monitor(new Store(liveStore.DirectoryPath),demo.Id)) { rescue.Paused=true; Pump(300); Check("restart recovers journaled window",Native.IsWindowVisible(hwnd)); }
                Check("native identity remains stable",inspector.StillSame(w));
            }
            Native.ShowWindowAsync(hwnd,4); Pump(100); Check("disposed monitor restores visibility",Native.IsWindowVisible(hwnd));
            WindowInfo picked=null; Point oldCursor=Cursor.Position;
            if(!EvidenceStore.DesktopAvailable()) Skip("overlay selection requires unlocked desktop");
            else using(var picker=new Picker(inspector,x=>picked=x,false))
            {
                picker.Show(); Pump(250); Native.Rect target; Native.GetWindowRect(hwnd,out target);
                var local=picker.PointToClient(new Point(target.Left+50,target.Top+80));
                typeof(Control).GetMethod("OnMouseUp",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(picker,new object[]{new MouseEventArgs(MouseButtons.Left,1,local.X,local.Y,0)});
                Pump(250); Check("overlay selection resolves underlying demo PID",picked!=null&&picked.Process.Pid==demo.Id);
                if(picked!=null&&picked.Process.Pid==demo.Id)
                {
                    var evidence=new EvidenceStore(liveStore); var capture=evidence.Capture(picked,inspector);
                    using(var photo=evidence.LoadImage(capture.File)) Check("screenshot after overlay closes contains actual demo content",ContainsDemo(photo));
                }
                if(picked==null || picked.Process.Pid!=demo.Id) Console.WriteLine("PICK DEBUG demo="+demo.Id+" target="+target.Rectangle+" local="+local+" got="+(picked==null?"null":picked.Process.Pid+" "+picked.Title));
            }
            Cursor.Position=oldCursor;
            using(var context=new GuardContext(Path.Combine(root,"ui"),false))
            {
                context.Monitor.Paused=true; Check("optional registry access does not break app startup",context.Monitor!=null);
                var saved=new EvidenceStore(liveStore).Load().FirstOrDefault(x=>x.Action=="待确认"&&x.Window.Process.Pid==demo.Id);
                if(saved!=null) { context.Monitor.History.Add(saved); context.Monitor.Evidence.Save(context.Monitor.History); Directory.CreateDirectory(context.Monitor.Evidence.ImageDirectory); if(saved.ScreenshotFile!="") File.Copy(Path.Combine(liveStore.DirectoryPath,"screenshots",saved.ScreenshotFile),context.Monitor.Evidence.ImagePath(saved.ScreenshotFile),true); }
                using(var review=new ReviewForm(context,w,saved))
                {
                    review.Show(); Pump(1800); string screenshot=Path.Combine(root,"review.png");
                    Check("review buttons fit their rows at desktop DPI",ButtonsFitVertically(review));
                    using(var bitmap=new Bitmap(review.Width,review.Height)) { review.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(screenshot); }
                    Check("source review UI renders",new FileInfo(screenshot).Length>1000); review.Close();
                }
                var dashboard=(Dashboard)typeof(GuardContext).GetField("dashboard",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(context);
                var missing=new Activity { Window=EvidenceStore.Copy(w),Action="待确认",Reason="lock regression",ScreenshotStatus=EvidenceStore.DesktopUnavailableStatus };
                context.Monitor.History.Insert(0,missing);
                context.CaptureDashboard(Path.Combine(root,"dashboard-no-image.png"));
                var history=Field<ListView>(dashboard,"activity"); var preview=Field<ScreenshotPreview>(dashboard,"preview");
                var empty=Field<Label>(preview,"empty"); var picture=Field<PictureBox>(preview,"picture"); var filter=Field<CheckBox>(dashboard,"onlyPictures");
                Check("no-image record displays visible reason inside preview",empty.Visible&&empty.Text.Contains("锁定")&&empty.Height>50&&picture.Image==null);
                Check("history explicitly marks records without screenshots",history.Items[0].SubItems[2].Text=="未截图");
                filter.Checked=true; Pump(100);
                Check("image filter excludes missing screenshot records",history.Items.Cast<ListViewItem>().All(x=>context.Monitor.Evidence.HasImage(((Activity)x.Tag).ScreenshotFile)));
                context.CaptureDashboard(Path.Combine(root,"dashboard-history.png"));
                Check("dashboard buttons fit their rows at desktop DPI",ButtonsFitVertically(dashboard));
                if(saved!=null&&context.Monitor.Evidence.HasImage(saved.ScreenshotFile))
                {
                    Check("filtered history displays actual image with usable bounds",history.Items.Count==1&&picture.Image!=null&&!empty.Visible&&picture.Height>80);
                    string path=context.Monitor.Evidence.ImagePath(saved.ScreenshotFile); File.Move(path,path+".removed");
                    try { dashboard.RefreshData(); Check("removed images disappear from filter and do not leave stale preview",history.Items.Count==0&&picture.Image==null&&empty.Visible&&empty.Text.Contains("还没有")); }
                    finally { File.Move(path+".removed",path); dashboard.RefreshData(); }
                }
                var tabs=dashboard.Controls[0].Controls.OfType<TabControl>().First(); tabs.SelectedIndex=2; Pump(300);
                context.CaptureDashboard(Path.Combine(root,"dashboard-presets.png"));
                var list=(ListView)typeof(Dashboard).GetField("presets",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(dashboard); list.Items[0].Selected=true;
                typeof(Dashboard).GetMethod("ChangePreset",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dashboard,null);
                Check("preset panel toggle persists disable",new Store(context.Monitor.Store.DirectoryPath).Settings.DisabledPresetIds.Contains("360-news"));
                context.ExitThread();
            }
            demo.CloseMainWindow(); demo.WaitForExit(3000); Check("source process exited before history confirmation",demo.HasExited);
            using(var historical=new PopupGuard.Monitor(new Store(liveStore.DirectoryPath),-1))
            {
                historical.Paused=true; historical.Store.Settings.Rules.Clear(); historical.Store.Save(); var saved=historical.History.First(x=>x.Action=="待确认"&&x.Window.Process.Pid==demo.Id);
                var added=historical.ConfirmRule(saved.Window,false,false,true); Check("can blacklist persisted evidence after source exit",added.Hash==saved.Window.Hash&&added.Path==saved.Window.Process.Path);
                var tampered=EvidenceStore.Copy(saved.Window); tampered.Hash=new string('0',64); bool rejected=false;
                try { historical.ConfirmRule(tampered,false,false,true); } catch(InvalidOperationException) { rejected=true; }
                Check("changed file fingerprint rejected for historical rules",rejected);
                string sourceImage=Path.Combine(root,"retention-fixture.png");
                using(var tiny=new Bitmap(32,32)) tiny.Save(sourceImage,System.Drawing.Imaging.ImageFormat.Png);
                if(File.Exists(sourceImage))
                {
                    for(int i=0;i<203;i++) File.Copy(sourceImage,diskEvidence.ImagePath(Guid.NewGuid().ToString("N")+".png"));
                    string expired=diskEvidence.ImagePath(Guid.NewGuid().ToString("N")+".png"); File.Copy(sourceImage,expired); File.SetLastWriteTimeUtc(expired,DateTime.UtcNow.AddDays(-31));
                    diskEvidence.PruneImages(); Check("screenshot count capped at 200",Directory.GetFiles(diskEvidence.ImageDirectory,"*.png").Length==200); Check("expired screenshots removed",!File.Exists(expired));
                }
                int rulesBefore=historical.Store.Settings.Rules.Count; historical.ClearHistory(); Check("clear history keeps rules but removes images and index entries",historical.History.Count==0&&historical.Evidence.Load().Count==0&&Directory.GetFiles(historical.Evidence.ImageDirectory,"*.png").Length==0&&historical.Store.Settings.Rules.Count==rulesBefore);
            }
            demo.Dispose(); demo=Process.Start(demoPath,"--automated-test"); IntPtr replayHandle=IntPtr.Zero;
            for(int i=0;i<60&&replayHandle==IntPtr.Zero;i++) { Pump(100); demo.Refresh(); replayHandle=demo.MainWindowHandle; }
            using(var replayMonitor=new PopupGuard.Monitor(new Store(liveStore.DirectoryPath),demo.Id))
            { WaitFor(()=>!Native.IsWindowVisible(replayHandle)); Check("historical rule blocks a new process instance",replayHandle!=IntPtr.Zero&&!Native.IsWindowVisible(replayHandle)); replayMonitor.RestoreAll(); Pump(250); }
            if(args.Contains("--skip-live-preset")) Skip("live preset fixture excluded explicitly: desktop sends TaskManagerClosing before guard monitoring starts");
            else LivePreset(root,demoPath,args.Contains("--flash-fixture"));
        }
        catch(Exception e) { Check("integration exception: "+e,false); }
        finally { if(demo!=null) { demo.Refresh(); if(!demo.HasExited) demo.CloseMainWindow(); demo.WaitForExit(3000); demo.Dispose(); } }
        Console.WriteLine("RESULT "+passed+" passed / "+failed+" failed / "+skipped+" skipped"); return failed==0?0:1;
    }
}
