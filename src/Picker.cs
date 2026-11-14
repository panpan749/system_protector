using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PopupGuard
{
    public sealed class Picker : Form
    {
        readonly Form guide=new Form(); readonly Timer timer=new Timer { Interval=100 };
        readonly Label caption=new Label(); readonly Action<WindowInfo> picked; readonly Inspector inspector;
        readonly DateTime started=DateTime.UtcNow; Rectangle highlight; bool finished;
        public Picker(Inspector inspector,Action<WindowInfo> picked, bool automatic)
        {
            this.inspector=inspector; this.picked=picked;
            FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual; Bounds=SystemInformation.VirtualScreen;
            BackColor=Color.FromArgb(8,22,31); Opacity=.25; TopMost=true; ShowInTaskbar=false; Cursor=Cursors.Cross;
            DoubleBuffered=true; KeyPreview=true;
            guide.Text="弹窗卫士 · 定位"; guide.FormBorderStyle=FormBorderStyle.None; guide.StartPosition=FormStartPosition.Manual;
            guide.Size=new Size(650,145); var area=Screen.FromPoint(Cursor.Position).WorkingArea;
            guide.Location=new Point(area.Left+(area.Width-guide.Width)/2,area.Top+28);
            guide.BackColor=Color.FromArgb(18,40,52); guide.ForeColor=Color.White; guide.TopMost=true; guide.ShowInTaskbar=false;
            guide.SuspendLayout(); guide.AutoScaleDimensions=new SizeF(96,96); guide.AutoScaleMode=AutoScaleMode.Dpi;
            guide.Font=new Font("Microsoft YaHei UI",10); guide.KeyPreview=true;
            caption.SetBounds(22,16,610,63); caption.ForeColor=Color.White;
            caption.Text=(automatic?"检测到可疑窗口，请确认屏幕上是否有广告。":"请用鼠标点击广告所在的位置。")+"\r\n点击后会显示来源，确认前不会加入黑名单。";
            guide.Controls.Add(caption);
            var cancel=new Button { Text="没有广告 / Esc 取消",Location=new Point(22,91),Size=new Size(230,34),FlatStyle=FlatStyle.Flat,ForeColor=Color.White };
            cancel.Click+=delegate { Finish(null); }; guide.Controls.Add(cancel);
            var hint=new Label { Text="20 秒后自动退出 · 不会点击底层广告",Location=new Point(270,99),Size=new Size(360,25),ForeColor=Color.LightGray };
            guide.Controls.Add(hint);
            guide.ResumeLayout(true);
            KeyDown+=Escape; guide.KeyDown+=Escape;
            Shown+=delegate { guide.Show(this); guide.Location=new Point(area.Left+(area.Width-guide.Width)/2,area.Top+28); Activate(); timer.Start(); };
            timer.Tick+=delegate
            {
                if((DateTime.UtcNow-started).TotalSeconds>=20) { Finish(null); return; }
                hint.Text=(20-(int)(DateTime.UtcNow-started).TotalSeconds)+" 秒后退出 · 不会点击底层广告";
                var point=Cursor.Position; Rectangle next=Rectangle.Empty;
                foreach(var h in Native.Windows())
                {
                    uint pid; Native.GetWindowThreadProcessId(h,out pid); if(pid==Process.GetCurrentProcess().Id) continue;
                    Native.Rect rect; int cloaked;
                    if(Native.IsWindowVisible(h)&&!Native.IsIconic(h)&&Native.GetWindowRect(h,out rect)&&rect.Rectangle.Contains(point)
                        && !(Native.DwmGetWindowAttribute(h,14,out cloaked,4)==0&&cloaked!=0)) { next=rect.Rectangle; next.Offset(-Left,-Top); break; }
                }
                if(next!=highlight) { highlight=next; Invalidate(); }
            };
            MouseUp+=delegate(object s,MouseEventArgs e)
            {
                if(e.Button!=MouseButtons.Left) { Finish(null); return; }
                var point=PointToScreen(e.Location); timer.Stop(); guide.Hide(); Hide();
                // Consume mouse-up before removing the overlay. The click never reaches the ad.
                var resolve=new Timer { Interval=80 }; resolve.Tick+=delegate
                {
                    resolve.Stop(); resolve.Dispose(); IntPtr h=Native.GetAncestor(Native.WindowFromPoint(new Native.Point(point.X,point.Y)),2);
                    Finish(inspector.ReadWindow(h,false));
                }; resolve.Start();
            };
        }
        void Escape(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Escape) { e.Handled=true; Finish(null); } }
        protected override void OnPaint(PaintEventArgs e)
        { base.OnPaint(e); if(!highlight.IsEmpty) using(var pen=new Pen(Color.Cyan,7)) { var r=highlight; r.Inflate(-3,-3); e.Graphics.DrawRectangle(pen,r); } }
        void Finish(WindowInfo w)
        {
            if(finished) return; finished=true; timer.Stop(); guide.Close(); Close(); picked(w);
        }
        protected override void Dispose(bool disposing) { if(disposing) { timer.Dispose(); guide.Dispose(); } base.Dispose(disposing); }
    }
}
