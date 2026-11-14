using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace PopupGuard
{
    public class CaptureResult { public string File="",Status="",Method=""; public DateTime CapturedAt; }
    public sealed class EvidenceStore
    {
        public const int MaxHistory=500,MaxImages=200,RetentionDays=30;
        public const string DesktopUnavailableStatus="桌面锁定、不可访问或正显示安全桌面，未截图";
        public const string CapturingStatus="正在采集窗口画面…",WindowCaptureMethod="WindowsGraphicsCapture";
        public const long MaxImageBytes=100L*1024*1024;
        readonly Store store; readonly JavaScriptSerializer json=new JavaScriptSerializer { MaxJsonLength=16*1024*1024 };
        public string ImageDirectory { get { return Path.Combine(store.DirectoryPath,"screenshots"); } }
        string Index { get { return Path.Combine(store.DirectoryPath,"history.json"); } }
        public EvidenceStore(Store store) { this.store=store; Directory.CreateDirectory(ImageDirectory); }
        public List<Activity> Load()
        {
            if(!System.IO.File.Exists(Index)) return new List<Activity>();
            try
            {
                if(new FileInfo(Index).Length>16*1024*1024) throw new InvalidDataException("历史记录过大");
                var items=json.Deserialize<List<Activity>>(System.IO.File.ReadAllText(Index))??new List<Activity>();
                foreach(var item in items) if(item!=null) {
                    item.Time=item.Time.ToLocalTime();
                    if(item.ScreenshotTime!=DateTime.MinValue) item.ScreenshotTime=item.ScreenshotTime.ToLocalTime();
                    if(string.IsNullOrEmpty(item.ScreenshotFile)&&item.ScreenshotStatus==CapturingStatus) item.ScreenshotStatus="上次退出前未完成截图";
                }
                return items.Where(x=>x!=null && x.Window!=null && x.Window.Process!=null && x.Window.Title!=null && x.Window.Class!=null && x.Window.Hash!=null && x.Window.Process.Path!=null)
                    .OrderByDescending(x=>x.Time).Take(MaxHistory).ToList();
            }
            catch(Exception e) { store.Warning="历史记录读取失败，守护继续运行："+e.Message; return new List<Activity>(); }
        }
        public void Save(List<Activity> items)
        {
            try
            {
                items.RemoveAll(x=>x.Time.ToUniversalTime()<DateTime.UtcNow.AddDays(-RetentionDays));
                if(items.Count>MaxHistory) items.RemoveRange(MaxHistory,items.Count-MaxHistory);
                string temp=Index+".tmp"; System.IO.File.WriteAllText(temp,json.Serialize(items));
                if(System.IO.File.Exists(Index)) System.IO.File.Replace(temp,Index,null); else System.IO.File.Move(temp,Index);
            }
            catch(Exception e) { store.Warning="历史记录保存失败："+e.Message; }
        }
        public string ImagePath(string file)
        {
            if(string.IsNullOrEmpty(file) || file.Length!=36 || !file.EndsWith(".png",StringComparison.OrdinalIgnoreCase)) return "";
            Guid id; if(!Guid.TryParseExact(file.Substring(0,32),"N",out id)) return "";
            return Path.Combine(ImageDirectory,file);
        }
        public Image LoadImage(string file)
        {
            string path=ImagePath(file); if(path==""||!System.IO.File.Exists(path)) return null;
            try { using(var source=Image.FromFile(path)) return new Bitmap(source); } catch { return null; }
        }
        public bool HasImage(string file)
        { string path=ImagePath(file); return path!=""&&System.IO.File.Exists(path); }
        public void DiscardImage(string file)
        { string path=ImagePath(file); if(path!="") try { System.IO.File.Delete(path); } catch(Exception e) { store.Warning="取消截图时清理失败："+e.Message; } }
        public void PruneImages()
        {
            try
            {
                long used=0; int count=0;
                foreach(var f in new DirectoryInfo(ImageDirectory).GetFiles("*.png").OrderByDescending(x=>x.LastWriteTimeUtc))
                {
                    if(ImagePath(f.Name)=="") continue;
                    if(f.LastWriteTimeUtc<DateTime.UtcNow.AddDays(-RetentionDays) || count>=MaxImages || used+f.Length>MaxImageBytes) f.Delete();
                    else { used+=f.Length; count++; }
                }
            } catch(Exception e) { store.Warning="截图清理失败："+e.Message; }
        }
        public void Clear(List<Activity> items)
        {
            foreach(var f in new DirectoryInfo(ImageDirectory).GetFiles("*.png")) if(ImagePath(f.Name)!="") f.Delete();
            items.Clear(); Save(items);
        }
        public static bool DesktopAvailable()
        {
            // A modern lock-screen surface can be visible while the input desktop is still named Default.
            // WTSSessionInfoEx (25), level 1: active (0) and unlocked (1) on supported Windows 10/11.
            IntPtr session=IntPtr.Zero;
            try
            {
                int bytes;
                if(!Native.WTSQuerySessionInformationW(IntPtr.Zero,Process.GetCurrentProcess().SessionId,25,out session,out bytes)||session==IntPtr.Zero||bytes<Marshal.SizeOf(typeof(Native.SessionInfoEx))) return false;
                var info=(Native.SessionInfoEx)Marshal.PtrToStructure(session,typeof(Native.SessionInfoEx));
                if(info.Level!=1||info.Data.SessionState!=0||info.Data.SessionFlags!=1) return false;
            }
            catch { return false; }
            finally { if(session!=IntPtr.Zero) Native.WTSFreeMemory(session); }
            IntPtr desktop=Native.OpenInputDesktop(0,false,1); if(desktop==IntPtr.Zero) return false;
            try { int needed; var name=new StringBuilder(256); return Native.GetUserObjectInformation(desktop,2,name,name.Capacity*2,out needed)&&name.ToString()=="Default"; }
            finally { Native.CloseDesktop(desktop); }
        }
        public CaptureResult Capture(WindowInfo w,Inspector inspector)
        { return CaptureAsync(w,inspector).GetAwaiter().GetResult(); }
        public async Task<CaptureResult> CaptureAsync(WindowInfo w,Inspector inspector)
        {
            var result=new CaptureResult();
            if(!store.Settings.CaptureScreenshots) { result.Status="自动截图已关闭"; return result; }
            string temp="";
            try
            {
                if(!DesktopAvailable()) { result.Status=DesktopUnavailableStatus; return result; }
                if(!inspector.StillSame(w)||!Native.IsWindowVisible(w.Handle)||Native.IsIconic(w.Handle)||Native.Title(w.Handle)!=w.Title)
                { result.Status="窗口已消失、最小化或身份变化，未截图"; return result; }
                Native.Rect rect; if(!Native.GetWindowRect(w.Handle,out rect)) { result.Status="窗口区域不可读取"; return result; }
                Rectangle area=Rectangle.Intersect(rect.Rectangle,SystemInformation.VirtualScreen);
                if(area.Width<20||area.Height<20||(long)area.Width*area.Height>12000000) { result.Status="窗口区域不可见或过大，未截图"; return result; }
                string helper=Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location),"PopupGuard.Capture.exe");
                if(!System.IO.File.Exists(helper)) { result.Status="窗口截图组件缺失，请安装完整版本"; return result; }
                string file=Guid.NewGuid().ToString("N")+".png",path=ImagePath(file); temp=path+".capture.tmp";
                var reply=await CaptureBroker.CaptureAsync(w,temp).ConfigureAwait(false);
                if(reply.Code!=0 || !System.IO.File.Exists(temp)) {
                    result.Status=reply.Code==5?DesktopUnavailableStatus:reply.Code==3?"弹窗在取得有效画面前已消失或身份变化，漏截":
                        reply.Code==4?"未取得有效窗口画面（透明、空白或尚未绘制），未保存背景图片":reply.Code==8?"窗口截图超时，未保存背景图片":reply.Code==7?"同时出现的窗口超过采集上限，本次漏截":"当前窗口无法通过图形接口捕获（可能受保护或系统不支持），未保存背景图片";
                    return result;
                }
                if(!store.Settings.CaptureScreenshots) { result.Status="自动截图已关闭，已取消本次采集"; return result; }
                // Identity and desktop were checked when this frame was frozen. A later close must not discard it.
                result.CapturedAt=DateTime.FromFileTimeUtc(reply.CapturedAt).ToLocalTime();
                // This image comes from the target's own compositor surface, never from a desktop crop.
                using(var bitmap=Image.FromFile(temp))
                {
                    double scale=Math.Min(1,2048.0/Math.Max(bitmap.Width,bitmap.Height));
                    if(scale<1)
                    {
                        using(var resized=new Bitmap(Math.Max(1,(int)(bitmap.Width*scale)),Math.Max(1,(int)(bitmap.Height*scale))))
                        { using(var g=Graphics.FromImage(resized)) { g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.DrawImage(bitmap,0,0,resized.Width,resized.Height); } resized.Save(path,ImageFormat.Png); }
                    }
                    else bitmap.Save(path,ImageFormat.Png);
                    result.File=file; result.Method=WindowCaptureMethod; result.Status="窗口内容截图";
                }
                PruneImages();
            }
            catch(Exception e) { result.Status="截图失败："+e.Message; }
            finally { if(temp!="") try { if(System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch { } }
            return result;
        }
        public static WindowInfo Copy(WindowInfo w)
        {
            if(w==null) return null;
            return new WindowInfo { Handle=w.Handle,ObservedAt=w.ObservedAt,Title=w.Title,Class=w.Class,Bounds=w.Bounds,Style=w.Style,ExStyle=w.ExStyle,Hash=w.Hash,Score=w.Score,Reason=w.Reason,Dpi=w.Dpi,
                Process=new ProcessInfo { Pid=w.Process.Pid,ParentPid=w.Process.ParentPid,Created=w.Process.Created,Path=w.Process.Path,Name=w.Process.Name } };
        }
    }
}
