using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PopupGuard
{
    public sealed class ScreenshotPreview : UserControl
    {
        readonly PictureBox picture=new PreviewPicture { Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(237,242,243) };
        readonly Label caption=new Label { Dock=DockStyle.Bottom,Height=64,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,4,10,4) };
        readonly Label empty=new Label { Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Padding=new Padding(12),ForeColor=Theme.Muted,BackColor=Color.FromArgb(237,242,243) };
        string current="";
        public ScreenshotPreview() { AutoScaleMode=AutoScaleMode.None; picture.Controls.Add(empty); Controls.Add(picture); Controls.Add(caption); picture.SizeChanged+=delegate { FitImage(); }; ShowEvidence(null,null); picture.DoubleClick+=delegate { if(picture.Image!=null) { var viewer=new Form { Text="历史弹窗截图",Size=new Size(1000,750),StartPosition=FormStartPosition.CenterScreen }; var zoom=new PictureBox { Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Image=new Bitmap(picture.Image),BackColor=Color.FromArgb(32,42,52) }; viewer.Controls.Add(zoom); viewer.FormClosed+=delegate { zoom.Image.Dispose(); viewer.Dispose(); }; viewer.Show(); } }; }
        void FitImage() { picture.SizeMode=picture.Image!=null&&picture.Image.Width<=picture.ClientSize.Width&&picture.Image.Height<=picture.ClientSize.Height?PictureBoxSizeMode.CenterImage:PictureBoxSizeMode.Zoom; }
        public void ShowEvidence(Activity a,EvidenceStore store,string emptyText=null)
        {
            bool available=a!=null&&store!=null&&store.HasImage(a.ScreenshotFile);
            string key=a==null?"none|"+emptyText:a.Id+"|"+a.ScreenshotFile+"|"+a.ScreenshotStatus+"|"+a.ScreenshotMethod+"|"+a.ScreenshotTime.Ticks+"|"+available;
            if(current==key) return; current=key; var old=picture.Image; picture.Image=null; if(old!=null) old.Dispose();
            empty.Visible=true;
            if(a==null) { empty.Text=emptyText??"选择记录查看截图"; caption.Text=""; return; }
            picture.Image=store==null?null:store.LoadImage(a.ScreenshotFile); empty.Visible=picture.Image==null; FitImage();
            string reason=picture.Image!=null?a.ScreenshotStatus:string.IsNullOrEmpty(a.ScreenshotFile)?(string.IsNullOrEmpty(a.ScreenshotStatus)?"当时未保存图片":a.ScreenshotStatus):available?"图片无法读取":"截图已过期或被清理";
            empty.Text="未保存截图\r\n\r\n"+reason;
            bool legacy=picture.Image!=null&&a.ScreenshotMethod!=EvidenceStore.WindowCaptureMethod;
            string when=a.Time.ToString("yyyy-MM-dd HH:mm:ss")+(picture.Image!=null&&a.ScreenshotTime!=DateTime.MinValue?"  ·  截图于 "+a.ScreenshotTime.ToString("HH:mm:ss.fff"):"");
            caption.ForeColor=Theme.Muted; caption.BackColor=Color.White;
            caption.Text=when+"\r\n"+(legacy?"旧版区域图，可能只有背景":picture.Image!=null?"双击查看原图":"可核对来源");
            ((PreviewPicture)picture).Reveal();
        }
        protected override void Dispose(bool disposing) { if(disposing&&picture.Image!=null) { picture.Image.Dispose(); picture.Image=null; } base.Dispose(disposing); }
    }
    public static class Theme
    {
        public static Color Ink=Color.FromArgb(31,49,57),Accent=Color.FromArgb(22,133,119),Paper=Color.FromArgb(245,247,248),Rail=Color.FromArgb(32,51,60),Muted=Color.FromArgb(105,124,132),Line=Color.FromArgb(221,229,232);
        public static void Form(Form form)
        { form.SuspendLayout(); form.Font=new Font("Microsoft YaHei UI",10); form.BackColor=Paper; form.ForeColor=Ink; form.StartPosition=FormStartPosition.CenterScreen; form.Icon=SystemIcons.Shield; form.AutoScaleDimensions=new SizeF(96,96); form.AutoScaleMode=AutoScaleMode.Dpi; }
        public static Button Button(string text,Action action,bool primary)
        {
            var b=new MotionButton { Primary=primary,Text=text,AutoSize=true,MinimumSize=new Size(110,39),Padding=new Padding(10,4,10,4),FlatStyle=FlatStyle.Flat,
                BackColor=primary?Accent:Color.White,ForeColor=primary?Color.White:Ink,Margin=new Padding(0,0,9,8) };
            b.FlatAppearance.BorderColor=primary?Accent:Color.FromArgb(207,218,225);
            b.Click+=delegate { try { action(); } catch(Exception e) { MessageBox.Show(e.Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning); } }; return b;
        }
        public static TextBox TextArea() { return new TextBox { Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,BackColor=Color.White,ForeColor=Ink,Font=new Font("Microsoft YaHei UI",10) }; }
        public static void StyleList(ListView list)
        {
            list.Dock=DockStyle.Fill; list.View=View.Details; list.FullRowSelect=true; list.HideSelection=false; list.BorderStyle=BorderStyle.None; list.BackColor=Color.White; list.ForeColor=Ink;
            typeof(Control).GetProperty("DoubleBuffered",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(list,true,null);
            var rows=new ImageList { ImageSize=new Size(1,34),ColorDepth=ColorDepth.Depth32Bit }; list.SmallImageList=rows; list.Disposed+=delegate { rows.Dispose(); };
            list.OwnerDraw=true;
            list.DrawColumnHeader+=delegate(object sender,DrawListViewColumnHeaderEventArgs e) {
                using(var brush=new SolidBrush(Paper)) e.Graphics.FillRectangle(brush,e.Bounds);
                var text=Rectangle.Inflate(e.Bounds,-8,0); TextRenderer.DrawText(e.Graphics,e.Header.Text,list.Font,text,Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
                using(var pen=new Pen(Line)) e.Graphics.DrawLine(pen,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
            };
            list.DrawItem+=delegate(object sender,DrawListViewItemEventArgs e) { if(list.View!=View.Details) e.DrawDefault=true; };
            list.DrawSubItem+=delegate(object sender,DrawListViewSubItemEventArgs e) { e.DrawDefault=true; };
        }
    }
    public sealed class ReviewForm : Form
    {
        readonly GuardContext context; readonly WindowInfo window; readonly TextBox details=Theme.TextArea();
        readonly ListBox apps=new ListBox(); readonly CheckBox anyTitle=new CheckBox(); readonly Button block,allow,uninstall,addSoftware;
        readonly Label softwareNote=new Label(); List<ProcessInfo> chain; bool ready,addingSoftware; readonly Activity evidence;
        public ReviewForm(GuardContext context,WindowInfo w,Activity evidence=null)
        {
            this.context=context; window=EvidenceStore.Copy(w); this.evidence=evidence; Theme.Form(this); Text="核对弹窗来源"; Size=new Size(900,815); MinimumSize=new Size(800,735);
            var root=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new Padding(24) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,64)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,49)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,90)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,55)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,64)); Controls.Add(root);
            root.Controls.Add(new Label { Text="这个窗口是广告吗？",Font=new Font(Font.FontFamily,20,FontStyle.Bold),Dock=DockStyle.Fill },0,0);
            var evidenceTabs=new ModernTabs { Dock=DockStyle.Fill }; var pictureTab=new TabPage("当时的截图"); var sourceTab=new TabPage("来源证据");
            var photo=new ScreenshotPreview { Dock=DockStyle.Fill }; pictureTab.Controls.Add(photo); sourceTab.Controls.Add(details); evidenceTabs.TabPages.Add(pictureTab); evidenceTabs.TabPages.Add(sourceTab);
            photo.ShowEvidence(evidence,context.Monitor.Evidence); if(evidence==null||string.IsNullOrEmpty(evidence.ScreenshotFile)) evidenceTabs.SelectedTab=sourceTab;
            root.Controls.Add(evidenceTabs,0,1); details.Text="正在读取进程身份、文件指纹与安装信息…";
            anyTitle.Text="扩展为此程序的同类窗口（忽略标题变化，可能包含正常窗口）"; anyTitle.AutoSize=true; root.Controls.Add(anyTitle,0,2);
            var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill };
            block=Theme.Button(evidence==null?"是广告：记住并隐藏":"是广告：加入黑名单",()=>AddRule(false),true); allow=Theme.Button("不是广告：记住放行",()=>AddRule(true),false);
            block.Enabled=false; allow.Enabled=false; buttons.Controls.Add(block); buttons.Controls.Add(allow); buttons.Controls.Add(Theme.Button("暂不处理",()=>Close(),false)); root.Controls.Add(buttons,0,3);
            apps.Dock=DockStyle.Fill; apps.BorderStyle=BorderStyle.FixedSingle; root.Controls.Add(apps,0,4);
            var softwareActions=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(0,8,0,0) };
            addSoftware=Theme.Button("加入软件清理黑名单…",AddSoftware,true); addSoftware.AutoSize=false; addSoftware.MinimumSize=Size.Empty; addSoftware.Size=new Size(220,34); addSoftware.Padding=Padding.Empty; addSoftware.Enabled=false;
            softwareActions.Controls.Add(addSoftware); softwareNote.AutoSize=true; softwareNote.MaximumSize=new Size(400,0); softwareNote.Padding=new Padding(0,4,0,0); softwareNote.Text="只加入每日巡检；卸载仍需确认。"; softwareActions.Controls.Add(softwareNote);
            var openSoftware=new LinkLabel { AutoSize=true,Text="打开软件巡检",Padding=new Padding(0,4,0,0) }; openSoftware.LinkClicked+=delegate { Close(); context.ShowSoftwareDashboard(); }; softwareActions.Controls.Add(openSoftware); root.Controls.Add(softwareActions,0,5);
            var cleanup=new FlowLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(0,10,0,0) };
            uninstall=Theme.Button("卸载选中的来源软件…",StartUninstall,true); uninstall.Enabled=false; cleanup.Controls.Add(uninstall);
            cleanup.Controls.Add(Theme.Button("打开 Windows 已安装应用",()=>Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute=true }),false)); root.Controls.Add(cleanup,0,6);
            apps.SelectedIndexChanged+=delegate { UpdateSoftwareOption(); }; context.Maintenance.Changed+=UpdateSoftwareOption;
            Shown+=LoadEvidence; ResumeLayout(true);
        }
        async void LoadEvidence(object sender,EventArgs e)
        {
            try
            {
                chain=evidence!=null&&evidence.Chain!=null&&evidence.Chain.Count>0?evidence.Chain:context.Monitor.Inspector.Trace(window.Process);
                var result=await Task.Run(()=>new { Hash=Safety.HashFile(window.Process.Path),Apps=Uninstall.Find(chain) });
                if(IsDisposed) return; if(evidence==null) window.Hash=result.Hash;
                string protection=Safety.ProtectedReason(window.Process);
                string lines=string.Join(Environment.NewLine,chain.Select((p,i)=>new string(' ',i*3)+(i==0?"窗口进程":"↳ 父进程")+"  "+p.Name+"  PID "+p.Pid+"\r\n"+new string(' ',i*3)+p.Path));
                details.Text="窗口："+window.Title+"\r\n窗口类："+window.Class+"\r\nPID："+window.Process.Pid+"  /  创建时间："+(window.Process.Created>0?DateTime.FromFileTimeUtc(window.Process.Created).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"不可读取")+
                    "\r\n文件："+window.Process.Path+"\r\n记录的 SHA-256："+(window.Hash==""?"未保存":window.Hash)+
                    "\r\n当前文件核对："+(window.Hash.Length==64&&result.Hash==window.Hash?"与当时一致，可添加规则":"文件缺失、已变化或缺少原始指纹，不能沿用此记录创建规则")+
                    (evidence==null?"":"\r\n历史时间："+evidence.Time.ToString("yyyy-MM-dd HH:mm:ss"))+"\r\n\r\n来源链（已验证创建时间；历史记录可能包含已退出进程）：\r\n"+lines+
                    "\r\n\r\n链末端之外的来源可能已退出或无权读取。父子关系不代表捆绑安装或恶意。\r\n"+
                    (protection!=""?"保护原因："+protection:"匹配安装项只说明文件归属，请核对下面的软件名称后决定是否卸载。")+
                    "\r\n\r\n特征："+window.Reason+"  /  分数 "+window.Score;
                foreach(var app in result.Apps) apps.Items.Add(app);
                if(apps.Items.Count==0) apps.Items.Add("未找到可信的安装项映射。可打开 Windows 已安装应用手动核对。"); else { apps.SelectedIndex=0; uninstall.Enabled=true; }
                ready=protection=="" && window.Hash.Length==64 && result.Hash==window.Hash && (evidence==null||evidence.EvidenceReady); block.Enabled=ready; allow.Enabled=ready;
                UpdateSoftwareOption();
            }
            catch(Exception ex) { if(!IsDisposed) details.Text="来源读取失败："+ex.Message; }
        }
        void UpdateSoftwareOption()
        {
            if(IsDisposed) return; var app=apps.SelectedItem as InstalledApp;
            var rule=context.Maintenance.Database.InstalledRule(app);
            addSoftware.Enabled=ready&&app!=null&&!addingSoftware&&!context.Maintenance.Busy&&rule==null&&!string.IsNullOrWhiteSpace(SoftwareScanner.Clean(app.Publisher));
            addSoftware.Text=rule==null?"加入软件清理黑名单…":"已在软件清理名单";
            softwareNote.Text=app==null?"未匹配到来源软件，可在巡检页按名称添加。":
                rule!=null?(context.Maintenance.Database.State.AllowedRules.Contains(rule.Id)?"该来源设为永久保留，可在巡检页调整。":context.Maintenance.Database.State.DisabledRules.Contains(rule.Id)?"该来源规则已停用，可在巡检页调整。":"已加入每日巡检；清理仍需另行确认。"):
                !ready?"来源文件尚未核实或已变化，请重新核对。":string.IsNullOrWhiteSpace(SoftwareScanner.Clean(app.Publisher))?"安装项缺少发布者，暂不能建立清理规则。":"只加入所选软件；卸载仍需确认。";
        }
        async void AddSoftware()
        {
            var selected=apps.SelectedItem as InstalledApp; if(!ready||selected==null||addingSoftware||context.Maintenance.Busy) return;
            addingSoftware=true; UpdateSoftwareOption();
            try
            {
                var current=await Task.Run(()=>new { Hash=Safety.HashFile(window.Process.Path),Apps=Uninstall.Find(chain) });
                if(IsDisposed) return;
                if(!object.ReferenceEquals(selected,apps.SelectedItem)) return;
                if(current.Hash!=window.Hash||current.Hash.Length!=64||!current.Apps.Any(a=>SoftwareScanner.SameRegistration(a,selected)))
                { ready=false; throw new InvalidOperationException("来源文件或安装项关联已经变化，请重新核对弹窗。"); }
                string note="将所选来源软件加入每日巡检的清理黑名单：\r\n\r\n"+selected.Name+"\r\n发布者："+selected.Publisher+"\r\n安装目录："+selected.Location+"\r\n关联依据："+selected.Match+
                    "\r\n\r\n加入后会扫描并列入软件巡检，卸载仍需另外勾选确认。父进程关系本身不能证明软件恶意。";
                if(MessageBox.Show(this,note,"加入软件清理黑名单",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return;
                var rule=context.Maintenance.AddInstalled(selected,"用户在弹窗来源核对中加入；关联依据："+selected.Match);
                context.Monitor.Store.Log("加入软件清理名单",window,selected.Name+" | "+rule.Id);
            }
            catch(Exception e) { if(!IsDisposed) MessageBox.Show(this,e.Message,"未加入软件清理名单",MessageBoxButtons.OK,MessageBoxIcon.Information); }
            finally { addingSoftware=false; if(!IsDisposed) UpdateSoftwareOption(); }
        }
        protected override void Dispose(bool disposing)
        { if(disposing) context.Maintenance.Changed-=UpdateSoftwareOption; base.Dispose(disposing); }
        void AddRule(bool isAllow)
        {
            if(!ready) return;
            if(anyTitle.Checked && MessageBox.Show("这将匹配此程序的同一窗口类下的所有标题，可能包含正常窗口。确定扩大范围？","确认规则范围",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return;
            context.Monitor.ConfirmRule(window,isAllow,anyTitle.Checked,evidence!=null);
            // Keep the source review open so uninstall remains the next concrete action.
            block.Enabled=false; allow.Enabled=false; block.Text=isAllow?"已放行":"已记住广告";
        }
        async void StartUninstall()
        {
            var app=apps.SelectedItem as InstalledApp; if(app==null) return;
            uninstall.Enabled=false; bool launched=false;
            try
            {
                if(!Uninstall.CommandUnchanged(app)) { MessageBox.Show("安装信息已经变化，请关闭窗口并重新核对。"); return; }
                LaunchPlan plan=Uninstall.Plan(app);
                if(!plan.Valid) { MessageBox.Show(plan.Reason+"。","请手动核对",MessageBoxButtons.OK,MessageBoxIcon.Information); return; }
                string hash=await Task.Run(()=>Safety.HashFile(plan.FileName)); if(IsDisposed) return;
                if(hash.Length!=64) { MessageBox.Show("无法读取卸载程序指纹。"); return; }
                string note="准备调用软件自身的卸载程序：\r\n\r\n"+app.Name+"\r\n发布者（注册表声明）："+app.Publisher+"\r\n关联证据："+app.Match+"\r\n安装目录："+app.Location+
                    "\r\n\r\n程序："+plan.FileName+"\r\n参数："+plan.Arguments+"\r\n\r\n此操作会卸载整个软件，可能影响它的其他功能。来源关系不等于恶意。是否继续？";
                if(MessageBox.Show(this,note,"确认卸载",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return;
                if(!Uninstall.CommandUnchanged(app)||Safety.HashFile(plan.FileName)!=hash) { MessageBox.Show("卸载信息或文件已变化，已取消。"); return; }
                context.Monitor.Store.Log("用户确认卸载",window,app.Name+" | "+plan.FileName+" "+plan.Arguments);
                var process=Process.Start(new ProcessStartInfo(plan.FileName,plan.Arguments) { UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(plan.FileName) });
                launched=true;
                uninstall.Enabled=false; uninstall.Text="卸载程序运行中…";
                if(process==null) { uninstall.Text="已打开卸载程序"; return; }
                int exit=await Task.Run(()=>{ using(process) { process.WaitForExit(); return process.ExitCode; } });
                bool remains=Uninstall.StillRegistered(app); string result="卸载程序退出码："+exit+"；"+(remains?"安装项仍存在，请核对卸载是否完成。":"安装项已移除；未检查残留文件或重启状态。");
                context.Monitor.Store.Log("卸载结果",window,app.Name+" | "+result);
                if(!IsDisposed) { uninstall.Text="重新核对后可再次卸载"; MessageBox.Show(this,result,"卸载结果"); }
            }
            catch(Exception ex) { if(!IsDisposed) { uninstall.Enabled=true; uninstall.Text="卸载选中的来源软件…"; MessageBox.Show(this,"未完成卸载："+ex.Message); } }
            finally { if(!IsDisposed&&!launched) uninstall.Enabled=true; }
        }
    }
}



