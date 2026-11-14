using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using PopupGuard;

public static class CaptureTests
{
    static int passed,failed; static string root; static readonly Inspector inspector=new Inspector();
    static void Check(string label,bool okay) { Console.WriteLine((okay?"PASS ":"FAIL ")+label); if(okay) passed++; else failed++; }
    static void Pump(int ms) { var t=Stopwatch.StartNew(); while(t.ElapsedMilliseconds<ms) { Application.DoEvents(); Thread.Sleep(10); } }
    static void Fixture(string mode,Action<WindowInfo> action)
    {
        using(var process=Process.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PopupGuard.GpuFixture.exe"),mode))
        {
            try {
                IntPtr handle=IntPtr.Zero;
                for(int i=0;i<100&&handle==IntPtr.Zero&&!process.HasExited;i++) { Pump(20); process.Refresh(); handle=process.MainWindowHandle; }
                if(handle==IntPtr.Zero) throw new Exception("GPU fixture unavailable");
                var window=inspector.ReadWindow(handle,false); Check("fixture uses a GPU composition surface",window!=null&&(window.ExStyle&0x200000)!=0);
                action(window);
            } finally { if(!process.HasExited) process.CloseMainWindow(); process.WaitForExit(4000); }
        }
    }
    static bool Pixels(Image image,bool updated=false)
    {
        if(image==null) return false;
        using(var bitmap=new Bitmap(image)) {
            int blue=0,marker=0,white=0,total=0;
            for(int y=0;y<bitmap.Height;y+=2) for(int x=0;x<bitmap.Width;x+=2) {
                var c=bitmap.GetPixel(x,y); total++;
                if(Math.Abs(c.R-22)<8&&Math.Abs(c.G-45)<8&&Math.Abs(c.B-63)<8) blue++;
                if(updated?(c.R>240&&c.B>240&&c.G<15):(c.G>240&&c.R<15&&c.B<15)) marker++;
                if(c.R>230&&c.G>230&&c.B>230) white++;
            }
            return blue>total*.5&&marker>total*.025&&white>total*.003;
        }
    }
    [STAThread] public static int Main()
    {
        Application.EnableVisualStyles(); root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","capture-"+DateTime.Now.ToString("yyyyMMddHHmmss")); Directory.CreateDirectory(root);
        Console.WriteLine("ARTIFACTS "+root);
        try {
            if(!EvidenceStore.DesktopAvailable()) throw new Exception("Actual unlocked desktop required; capture tests must not be counted as verified while locked.");
            var store=new Store(root); store.Settings.AutoPrompt=false; var evidence=new EvidenceStore(store);
            Fixture("--delay",window=>{
                var timer=Stopwatch.StartNew(); var result=evidence.Capture(window,inspector); timer.Stop();
                Console.WriteLine("CAPTURE "+timer.ElapsedMilliseconds+" ms "+result.Status);
                Check("GPU window capture succeeds with explicit provenance",result.Method==EvidenceStore.WindowCaptureMethod&&result.File!="");
                using(var image=evidence.LoadImage(result.File)) { Check("first-show capture waits for actual colored marker AND rendered text",Pixels(image)); Check("capture is target-sized at 200 percent desktop DPI",image!=null&&image.Width==720&&image.Height==160); }
                if(result.File!="") File.Copy(evidence.ImagePath(result.File),Path.Combine(root,"gpu-delayed.png"));
                using(var cover=new Form { FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,Bounds=window.Bounds,BackColor=Color.Red,TopMost=true,ShowInTaskbar=false }) {
                    cover.Show(); Pump(120); var covered=evidence.Capture(window,inspector);
                    using(var image=evidence.LoadImage(covered.File)) Check("occluding red window is excluded from captured target",Pixels(image));
                    if(covered.File!="") File.Copy(evidence.ImagePath(covered.File),Path.Combine(root,"gpu-occluded.png")); cover.Close();
                }
                Native.PostMessage(window.Handle,0x8001,IntPtr.Zero,IntPtr.Zero); Pump(100);
                var fresh=evidence.Capture(window,inspector);
                using(var image=evidence.LoadImage(fresh.File)) Check("later capture records changed window pixels rather than cached prior image",Pixels(image,true));
                var stale=EvidenceStore.Copy(window); stale.Process.Created--;
                Check("stale process identity cannot capture a recycled handle",evidence.Capture(stale,inspector).File=="");
                Native.ShowWindowAsync(window.Handle,0); Pump(80);
                Check("hidden window produces no screenshot",evidence.Capture(window,inspector).File==""); Native.ShowWindowAsync(window.Handle,4);
            });
            Fixture("--blank",window=>{
                var timer=Stopwatch.StartNew(); var result=evidence.Capture(window,inspector);
                Console.WriteLine("BLANK "+result.Status);
                Check("transparent GPU window never saves wallpaper as successful screenshot",result.File==""&&result.Status.Contains("未取得有效窗口画面"));
                Check("blank frame retries have a bounded deadline",timer.ElapsedMilliseconds<3300);
            });
            Fixture("--protected",window=>{
                var result=evidence.Capture(window,inspector); Check("capture-protected window is not bypassed",result.File=="");
            });
            Fixture("--delay",window=>{
                var raceStore=new Store(Path.Combine(root,"rule-change")); raceStore.Settings.AutoPrompt=false;
                window.Hash=Safety.HashFile(window.Process.Path); raceStore.Settings.Rules.Add(Rule.From(window,false,false));
                using(var monitor=new PopupGuard.Monitor(raceStore,window.Process.Pid)) {
                    var timer=Stopwatch.StartNew();
                    while(!monitor.History.Any(a=>a.Action=="准备隐藏")&&timer.ElapsedMilliseconds<4000) Pump(10);
                    Check("automatic capture does not block UI event processing",monitor.History.Any(a=>a.Action=="准备隐藏"&&a.ScreenshotStatus==EvidenceStore.CapturingStatus));
                    raceStore.Settings.Rules.Add(Rule.From(window,true,false)); Pump(3300);
                    Check("whitelist added while capture is pending prevents hiding",Native.IsWindowVisible(window.Handle)&&monitor.BlockCount==0);
                }
            });
            Fixture("--delay",window=>{
                using(var monitor=new PopupGuard.Monitor(new Store(Path.Combine(root,"clear-race")),window.Process.Pid)) {
                    monitor.Paused=true; var task=monitor.ObserveAsync(window,"手动定位","clear while capture pending"); monitor.ClearHistory();
                    var timer=Stopwatch.StartNew(); while(!task.IsCompleted&&timer.ElapsedMilliseconds<4000) Pump(20);
                    Check("clearing history cancels pending image without leaving an orphan",task.IsCompleted&&monitor.History.Count==0&&Directory.GetFiles(monitor.Evidence.ImageDirectory,"*.png").Length==0);
                }
            });
            Check("native capture temp files are removed",Directory.GetFiles(evidence.ImageDirectory,"*.tmp").Length==0);
        } catch(Exception e) { Check("integration exception: "+e,false); }
        Console.WriteLine("RESULT "+passed+" passed / "+failed+" failed"); return failed==0?0:1;
    }
}
