using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PopupGuard
{
    public static class Motion
    {
        public static bool Preference=true;
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action,uint parameter,out bool value,uint flags);
        public static bool Enabled { get { bool enabled; return Preference&&!SystemInformation.HighContrast&&SystemParametersInfo(0x1042,0,out enabled,0)&&enabled; } }
    }
    public sealed class MotionButton : Button
    {
        readonly Timer animation=new Timer { Interval=16 }; readonly Stopwatch elapsed=new Stopwatch(); float amount,target,from; bool over,pressed,selected;
        public bool Primary; public bool Navigation;
        public bool Selected { get { return selected; } set { selected=value; Animate(); } }
        public override Size GetPreferredSize(Size proposedSize) {
            if(!AutoSize) return base.GetPreferredSize(proposedSize);
            var text=TextRenderer.MeasureText(Text,Font); float dpi=DeviceDpi/96f;
            return new Size(Math.Max(MinimumSize.Width,text.Width+Padding.Horizontal+(int)(12*dpi)),Math.Max(MinimumSize.Height,text.Height+Padding.Vertical+(int)(4*dpi)));
        }
        public MotionButton() {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; Cursor=Cursors.Hand;
            animation.Tick+=delegate { float t=Motion.Enabled?Math.Min(1,elapsed.ElapsedMilliseconds/150f):1; amount=from+(target-from)*(1-(float)Math.Pow(1-t,3)); if(t>=1) animation.Stop(); Invalidate(); };
        }
        void Animate() { target=Enabled&&(over||selected)?1:0; from=amount; elapsed.Restart(); if(Motion.Enabled&&Visible&&target!=amount) animation.Start(); else { animation.Stop(); amount=target; Invalidate(); } }
        protected override void OnMouseEnter(EventArgs e) { over=true; Animate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over=false; pressed=false; Animate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed=true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed=false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Animate(); base.OnEnabledChanged(e); }
        protected override void OnVisibleChanged(EventArgs e) { if(!Visible) { animation.Stop(); amount=target; } base.OnVisibleChanged(e); }
        static Color Mix(Color a,Color b,float t) { return Color.FromArgb((int)(a.R+(b.R-a.R)*t),(int)(a.G+(b.G-a.G)*t),(int)(a.B+(b.B-a.B)*t)); }
        protected override void OnPaint(PaintEventArgs e) {
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Parent==null?Theme.Paper:Parent.BackColor);
            float dpi=g.DpiX/96f; var bounds=new RectangleF(1,1,Width-3,Height-3);
            Color bg=Navigation?Mix(Theme.Rail,Color.FromArgb(48,69,77),amount):Primary?Mix(Theme.Accent,Color.FromArgb(17,116,110),amount):Mix(Color.White,Color.FromArgb(231,242,240),amount);
            Color ink=Navigation?(selected?Color.White:Color.FromArgb(194,210,214)):Primary?Color.White:Theme.Ink;
            if(!Enabled) { bg=Color.FromArgb(234,238,240); ink=Theme.Muted; }
            using(var path=Round(bounds,8*dpi)) using(var brush=new SolidBrush(bg)) { g.FillPath(brush,path); if(!Primary&&!Navigation) using(var pen=new Pen(Theme.Line)) g.DrawPath(pen,path); }
            var textBounds=new Rectangle((int)(Navigation?16*dpi:4),pressed?2:0,Width-(int)(Navigation?24*dpi:8),Height);
            TextRenderer.DrawText(g,Text,Font,textBounds,ink,(Navigation?TextFormatFlags.Left:TextFormatFlags.HorizontalCenter)|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if(Focused&&ShowFocusCues) ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-6,-6),ink,bg);
        }
        internal static GraphicsPath Round(RectangleF r,float radius) { var path=new GraphicsPath(); float d=Math.Min(radius*2,Math.Min(r.Width,r.Height)); path.AddArc(r.X,r.Y,d,d,180,90); path.AddArc(r.Right-d,r.Y,d,d,270,90); path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); path.AddArc(r.X,r.Bottom-d,d,d,90,90); path.CloseFigure(); return path; }
        protected override void Dispose(bool disposing) { if(disposing) animation.Dispose(); base.Dispose(disposing); }
    }
    public sealed class PreviewPicture : PictureBox
    {
        readonly Timer animation=new Timer { Interval=16 }; readonly Stopwatch elapsed=new Stopwatch(); float opacity=1;
        public PreviewPicture() { animation.Tick+=delegate { opacity=Motion.Enabled?Math.Min(1,elapsed.ElapsedMilliseconds/150f):1; Invalidate(); if(opacity>=1) animation.Stop(); }; }
        public void Reveal() { animation.Stop(); opacity=1; if(Motion.Enabled&&Visible&&Image!=null) { opacity=0; elapsed.Restart(); animation.Start(); } Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); if(opacity<1) using(var brush=new SolidBrush(Color.FromArgb((int)(255*(1-opacity)),BackColor))) e.Graphics.FillRectangle(brush,ClientRectangle); }
        protected override void OnVisibleChanged(EventArgs e) { if(!Visible) { animation.Stop(); opacity=1; } base.OnVisibleChanged(e); }
        protected override void Dispose(bool disposing) { if(disposing) animation.Dispose(); base.Dispose(disposing); }
    }
    public sealed class PageStack : TabControl
    {
        public override Rectangle DisplayRectangle { get { return new Rectangle(0,0,Width,Height); } }
        public PageStack() { Appearance=TabAppearance.FlatButtons; ItemSize=new Size(1,1); SizeMode=TabSizeMode.Fixed; TabStop=false; }
    }
    public sealed class NavigationRail : Panel
    {
        readonly Timer animation=new Timer { Interval=16 }; readonly Stopwatch elapsed=new Stopwatch(); readonly MotionButton[] links=new MotionButton[5]; readonly Panel marker=new Panel { BackColor=Color.FromArgb(92,209,185),Size=new Size(3,24),TabStop=false }; float position,target,from;
        public event Action<int> Selected;
        public NavigationRail() {
            BackColor=Theme.Rail; DoubleBuffered=true;
            var brand=new Label { Text="弹窗卫士",ForeColor=Color.White,Font=new Font("Microsoft YaHei UI",18,FontStyle.Bold),Dock=DockStyle.Top,Height=88,Padding=new Padding(24,30,0,0) }; Controls.Add(brand);
            var menu=new TableLayoutPanel { Dock=DockStyle.Top,ColumnCount=1,RowCount=5,Height=270,Padding=new Padding(18,5,18,5),BackColor=Theme.Rail };
            string[] names={"弹窗记录","黑白名单","广告规则","软件巡检","设置"};
            for(int i=0;i<links.Length;i++) { int index=i; menu.RowStyles.Add(new RowStyle(SizeType.Percent,20)); links[i]=new MotionButton { Text=names[i],Navigation=true,Dock=DockStyle.Fill,Margin=new Padding(0,4,0,4),AccessibleName=names[i] }; links[i].Click+=delegate { if(Selected!=null) Selected(index); }; menu.Controls.Add(links[i],0,i); }
            Controls.Add(menu); menu.BringToFront();
            var footer=new Label { Dock=DockStyle.Bottom,Height=86,Padding=new Padding(24,12,0,0),Text="Ctrl + ↓  点选广告\r\nv0.5.1",ForeColor=Color.FromArgb(156,180,185) }; Controls.Add(footer);
            Controls.Add(marker); marker.BringToFront();
            animation.Tick+=delegate { float t=Motion.Enabled?Math.Min(1,elapsed.ElapsedMilliseconds/180f):1; position=from+(target-from)*(1-(float)Math.Pow(1-t,3)); if(t>=1) animation.Stop(); PlaceMarker(); };
            menu.Layout+=delegate { target=links[Array.FindIndex(links,b=>b.Selected)<0?0:Array.FindIndex(links,b=>b.Selected)].Top+menu.Top; position=target; PlaceMarker(); };
        }
        void PlaceMarker() { float dpi=DeviceDpi/96f; marker.SetBounds((int)(10*dpi),(int)(position+13*dpi),Math.Max(3,(int)(3*dpi)),(int)(24*dpi)); marker.BringToFront(); }
        public void SelectPage(int index) { for(int i=0;i<links.Length;i++) links[i].Selected=i==index; target=links[index].Top+links[index].Parent.Top; from=position; elapsed.Restart(); if(Motion.Enabled&&Visible) animation.Start(); else { animation.Stop(); position=target; PlaceMarker(); } }
        protected override void OnVisibleChanged(EventArgs e) { if(!Visible) { animation.Stop(); position=target; PlaceMarker(); } base.OnVisibleChanged(e); }
        protected override void Dispose(bool disposing) { if(disposing) animation.Dispose(); base.Dispose(disposing); }
    }
    public sealed class ModernTabs : Panel
    {
        readonly PageStack pages=new PageStack { Dock=DockStyle.Fill };
        readonly FlowLayoutPanel navigation=new FlowLayoutPanel { Dock=DockStyle.Top,Height=48,Padding=new Padding(0,3,0,0),WrapContents=false,AutoScroll=true,BackColor=Color.White };
        public TabControl.TabPageCollection TabPages { get { return pages.TabPages; } }
        public int SelectedIndex { get { return pages.SelectedIndex; } set { pages.SelectedIndex=value; } }
        public TabPage SelectedTab { get { return pages.SelectedTab; } set { pages.SelectedTab=value; } }
        public ModernTabs() {
            BackColor=Color.White; Controls.Add(pages); Controls.Add(navigation);
            pages.ControlAdded+=delegate { Rebuild(); }; pages.SelectedIndexChanged+=delegate { foreach(MotionButton b in navigation.Controls) { b.Primary=(int)b.Tag==pages.SelectedIndex; b.Invalidate(); } };
        }
        void Rebuild() {
            while(navigation.Controls.Count>0) { var old=navigation.Controls[0]; navigation.Controls.Remove(old); old.Dispose(); }
            for(int i=0;i<pages.TabCount;i++) { int index=i; var b=(MotionButton)Theme.Button(pages.TabPages[i].Text,()=>pages.SelectedIndex=index,i==(pages.SelectedIndex<0?0:pages.SelectedIndex)); b.Tag=i; b.MinimumSize=new Size(110,36); navigation.Controls.Add(b); }
        }
    }
}

