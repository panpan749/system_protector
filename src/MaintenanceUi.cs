using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PopupGuard
{
    public sealed class MaintenancePanel : UserControl
    {
        readonly MaintenanceController controller; readonly ListView findings=new ListView(),rules=new ListView(),results=new ListView();
        readonly Label summary=new Label(); readonly TextBox details=Theme.TextArea(); readonly FlowLayoutPanel buttons=new FlowLayoutPanel();
        readonly CheckBox daily=new CheckBox { Text="每日后台检查",AutoSize=true }; readonly DateTimePicker time=new DateTimePicker { Format=DateTimePickerFormat.Custom,CustomFormat="HH:mm",ShowUpDown=true,Width=85 };
        readonly Label schedule=new Label { AutoSize=false,AutoEllipsis=true,Width=310,Height=37,Padding=new Padding(4,8,4,0) };
        readonly List<Control> mutable=new List<Control>(); bool refreshing; string taskState="";
        public MaintenancePanel(MaintenanceController controller)
        {
            this.controller=controller; Dock=DockStyle.Fill; BackColor=Color.White;
            var root=new TableLayoutPanel { Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Padding=new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,30)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); Controls.Add(root);
            var scheduling=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false };
            scheduling.Controls.Add(daily); scheduling.Controls.Add(time); var save=CompactButton("保存每日设置",Configure,false); scheduling.Controls.Add(save); scheduling.Controls.Add(schedule); mutable.Add(save); root.Controls.Add(scheduling,0,0);
            summary.Dock=DockStyle.Fill; summary.AutoEllipsis=true; summary.Padding=new Padding(2,4,2,4); root.Controls.Add(summary,0,1);
            var tabs=new ModernTabs { Dock=DockStyle.Fill }; root.Controls.Add(tabs,0,2);
            var pending=new TabPage("扫描结果") { BackColor=Color.White }; var catalog=new TabPage("清理名单") { BackColor=Color.White }; var history=new TabPage("清理记录") { BackColor=Color.White }; tabs.TabPages.AddRange(new[]{pending,catalog,history});
            List(findings,new[]{"状态","软件 / 组件","分类","安装目录 / 配置"},new[]{125,190,140,440}); findings.CheckBoxes=true; findings.MultiSelect=true;
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill,RowCount=3,ColumnCount=1 }; layout.RowStyles.Add(new RowStyle(SizeType.Percent,64)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,36)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            layout.Controls.Add(findings,0,0); layout.Controls.Add(details,0,1); pending.Controls.Add(layout);
            findings.SelectedIndexChanged+=delegate { if(!refreshing) ShowFinding(); };
            buttons.Dock=DockStyle.Fill; buttons.AutoScroll=true; buttons.WrapContents=false; buttons.Padding=new Padding(0,5,0,0); layout.Controls.Add(buttons,0,2);
            Add(buttons,"立即扫描",()=>controller.Scan(true),true);
            Add(buttons,"勾选可处理项",()=>{ foreach(ListViewItem item in findings.Items) { var f=(SoftwareFinding)item.Tag; item.Checked=f.Actionable&&controller.Database.Pending(f); } },false);
            var cleanup=CompactButton("清理勾选项…",()=>BeginCleanup(),true); buttons.Controls.Add(cleanup); mutable.Add(cleanup);
            Add(buttons,"忽略此版本",()=>Mark(false),false); Add(buttons,"永久保留",()=>Mark(true),false);
            Add(buttons,"扫描异常",()=>MessageBox.Show(this,controller.Database.State.Latest.Errors.Count==0?"最近一次扫描没有报告读取异常。":string.Join("\r\n",controller.Database.State.Latest.Errors),"扫描范围与异常"),false);
            List(rules,new[]{"状态","软件","规则 ID","类型","版本"},new[]{140,220,260,140,60}); rules.MultiSelect=false; catalog.Controls.Add(rules);
            var ruleActions=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=84,Padding=new Padding(6),AutoScroll=true }; catalog.Controls.Add(ruleActions);
            Add(ruleActions,"添加软件…",AddInstalled,true); Add(ruleActions,"添加扩展…",AddExtension,false);
            Add(ruleActions,"导入名单…",Import,false); Add(ruleActions,"导出名单…",Export,false);
            Add(ruleActions,"启用 / 停用规则",Toggle,false); Add(ruleActions,"撤销保留",Unallow,false); Add(ruleActions,"查看规则",ShowRule,false);
            rules.DoubleClick+=delegate { ShowRule(); };
            List(results,new[]{"时间","软件","结果","说明"},new[]{145,190,130,440}); history.Controls.Add(results);
            var historyActions=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=48,Padding=new Padding(6) }; history.Controls.Add(historyActions);
            Add(historyActions,"打开清理记录",()=>{ string p=Path.Combine(controller.Database.DirectoryPath,"cleanup-jobs"); Directory.CreateDirectory(p); Process.Start(new ProcessStartInfo(p) { UseShellExecute=true }); },false);
            results.DoubleClick+=delegate { if(results.SelectedItems.Count>0) { var r=(CleanupResult)results.SelectedItems[0].Tag; MessageBox.Show(this,r.Detail+"\r\n\r\n"+string.Join("\r\n",r.Operations),r.Name+" · "+r.Status); } };
            daily.Checked=controller.Database.State.DailyEnabled; time.Value=DateTime.Today.AddHours(controller.Database.State.Hour).AddMinutes(controller.Database.State.Minute);
            ReadTaskState(); controller.Changed+=RefreshData; RefreshData();
            VisibleChanged+=delegate { if(Visible&&IsHandleCreated) controller.RefreshView(); };
            Load+=delegate { if(Visible) controller.RefreshView(); };
            Load+=delegate { using(var g=CreateGraphics()) foreach(var list in new[]{findings,rules,results}) foreach(ColumnHeader column in list.Columns) column.Width=(int)(column.Width*g.DpiX/96f); };
        }
        static void List(ListView list,string[] columns,int[] widths)
        {
            Theme.StyleList(list);
            for(int i=0;i<columns.Length;i++) list.Columns.Add(columns[i],widths[i]);
        }
        void Add(FlowLayoutPanel host,string text,Action action,bool primary)
        { var b=CompactButton(text,action,primary); host.Controls.Add(b); mutable.Add(b); }
        static Button CompactButton(string text,Action action,bool primary)
        {
            var button=Theme.Button(text,action,primary); button.AutoSize=false; button.MinimumSize=Size.Empty; button.Size=new Size(112,34); button.Padding=Padding.Empty; button.Margin=new Padding(0,0,8,6); return button;
        }
        void ReadTaskState()
        { try { taskState=DailySoftwareTask.Exists(controller.Database.DirectoryPath)?"每日任务已启用":"保存后启用每日任务"; } catch(Exception e) { taskState="任务状态不可读："+e.Message; } }
        void Configure()
        {
            controller.Configure(daily.Checked,time.Value.Hour,time.Value.Minute); ReadTaskState(); RefreshData();
        }
        void ShowFinding()
        {
            if(findings.SelectedItems.Count==0) { details.Text="选择项目查看详情"; return; }
            var f=(SoftwareFinding)findings.SelectedItems[0].Tag;
            details.Text=f.Name+" · "+f.Category+"\r\n"+f.Evidence+"\r\n\r\n处理方式："+f.Detail+"\r\n规则："+f.RuleId;
        }
        List<SoftwareFinding> Selected()
        { return findings.CheckedItems.Cast<ListViewItem>().Select(x=>(SoftwareFinding)x.Tag).ToList(); }
        async void BeginCleanup()
        {
            try
            {
                var selected=Selected(); if(selected.Count==0) throw new InvalidOperationException("请先勾选需要清理的项目。");
                if(selected.Any(f=>!f.Actionable||!controller.Database.Pending(f))) throw new InvalidOperationException("勾选项中包含保留、忽略或不可自动处理的项目。");
                using(var confirmation=new CleanupConfirmation(selected)) if(confirmation.ShowDialog(this)!=DialogResult.OK) return;
                await controller.Cleanup(selected);
            } catch(Exception e) { MessageBox.Show(this,e.Message,"清理未开始"); }
        }
        void Mark(bool allow)
        {
            var selected=Selected(); if(selected.Count==0&&findings.SelectedItems.Count>0) selected.Add((SoftwareFinding)findings.SelectedItems[0].Tag);
            if(selected.Count==0) return;
            foreach(var f in selected)
            {
                if(allow) { if(!controller.Database.State.AllowedRules.Contains(f.RuleId)) controller.Database.State.AllowedRules.Add(f.RuleId); }
                else controller.Database.State.Ignored[f.Key]=f.Fingerprint;
            }
            controller.Database.Save(); RefreshData();
        }
        SoftwareRule ChosenRule() { return rules.SelectedItems.Count>0?(SoftwareRule)rules.SelectedItems[0].Tag:null; }
        void Toggle()
        {
            var r=ChosenRule(); if(r==null) return; var disabled=controller.Database.State.DisabledRules;
            if(disabled.Contains(r.Id)) disabled.Remove(r.Id); else disabled.Add(r.Id); controller.Database.Save(); RefreshData();
        }
        void Unallow()
        {
            var r=ChosenRule(); if(r==null) return; controller.Database.State.AllowedRules.Remove(r.Id);
            foreach(var f in controller.Database.State.Latest.Findings.Where(f=>f.RuleId==r.Id)) controller.Database.State.Ignored.Remove(f.Key);
            controller.Database.Save(); RefreshData();
        }
        void ShowRule()
        {
            var r=ChosenRule(); if(r==null) return;
            MessageBox.Show(this,r.Name+"\r\n规则 ID："+r.Id+"\r\n版本："+r.Revision+"\r\n类别："+r.Category+"\r\n原因："+r.Reason+"\r\n来源："+r.Source+"\r\n\r\n"+
                (r.Kind=="ChromeExtension"?"扩展 ID："+r.ExtensionId:r.Kind=="Installed"?"安装项 ID："+r.InstallId+"\r\n软件名称："+r.ProductName+"\r\n发布者："+r.Publisher:"目录："+r.Base+"\\"+r.RelativeRoot+"\r\n组件标记："+string.Join("、",r.Markers)),"软件名单详情");
        }
        void Commit(SoftwareCatalog incoming)
        { controller.Database.Commit(controller.Database.MergePreview(incoming)); RefreshData(); controller.Scan(true); }
        void AddInstalled()
        {
            using(var picker=new InstalledSoftwarePicker())
            {
                if(picker.ShowDialog(this)!=DialogResult.OK||picker.SelectedApp==null) return;
                controller.AddInstalled(picker.SelectedApp,"用户从本机已安装软件列表添加；仅表示不需要该软件。");
            }
        }
        void AddExtension()
        {
            using(var form=new Form { Text="添加 Chrome 扩展 ID",Size=new Size(590,260) })
            {
                Theme.Form(form); var flow=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,Padding=new Padding(18),WrapContents=false }; form.Controls.Add(flow);
                flow.Controls.Add(new Label { Text="从 chrome://extensions 的开发者模式复制扩展 ID。",AutoSize=true });
                var name=new TextBox { Width=500,Text="不需要的扩展" }; var id=new TextBox { Width=500 }; flow.Controls.Add(name); flow.Controls.Add(id);
                flow.Controls.Add(Theme.Button("加入软件名单",()=>
                {
                    var rule=new SoftwareRule { Id="local.chrome."+id.Text.Trim(),Name=name.Text.Trim(),Kind="ChromeExtension",ExtensionId=id.Text.Trim(),Reason="用户按扩展 ID 手动添加。",Source="本机用户选择 "+DateTime.Now.ToString("yyyy-MM-dd") };
                    var old=controller.Database.Catalog.Rules.FirstOrDefault(r=>r.Id==rule.Id); if(old!=null) rule.Revision=old.Revision+1;
                    Commit(new SoftwareCatalog { Rules=new List<SoftwareRule> { rule } }); form.Close();
                },true)); form.ResumeLayout(true); form.ShowDialog(this);
            }
        }
        void Import()
        {
            using(var dialog=new OpenFileDialog { Filter="软件名单 JSON|*.json",Title="导入审核过的软件名单" })
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK) return;
                var incoming=MaintenanceJson.Read<SoftwareCatalog>(dialog.FileName); var merged=controller.Database.MergePreview(incoming);
                string text="将增加或更新以下规则：\r\n\r\n"+string.Join("\r\n",incoming.Rules.Select(r=>r.Name+" · "+r.Id+" · v"+r.Revision))+"\r\n\r\n导入只更新名单，清理仍需另行确认。个人保留设置优先。";
                using(var preview=new Form { Text="核对名单变更",Size=new Size(760,560) })
                {
                    Theme.Form(preview); var area=Theme.TextArea(); area.Text=text; preview.Controls.Add(area); var accept=Theme.Button("确认导入",()=>preview.DialogResult=DialogResult.OK,true); accept.Dock=DockStyle.Bottom; preview.Controls.Add(accept);
                    preview.ResumeLayout(true); if(preview.ShowDialog(this)==DialogResult.OK) { controller.Database.Commit(merged); RefreshData(); controller.Scan(true); }
                }
            }
        }
        void Export()
        {
            using(var dialog=new SaveFileDialog { Filter="软件名单 JSON|*.json",FileName="PopupGuard-software-rules.json" })
                if(dialog.ShowDialog(this)==DialogResult.OK) File.WriteAllText(dialog.FileName,MaintenanceJson.Encode(controller.Database.Catalog),System.Text.Encoding.UTF8);
        }
        public void RefreshData()
        {
            if(IsDisposed) return; var db=controller.Database; refreshing=true;
            foreach(var c in mutable) c.Enabled=!controller.Busy; daily.Enabled=time.Enabled=!controller.Busy;
            schedule.Text=taskState;
            summary.ForeColor=Theme.Muted; summary.Text=(db.Warning.Length>0?db.Warning:controller.Progress.Length>0?controller.Progress:"名单 "+db.Catalog.Rules.Count+" 条")+(controller.Busy?"":db.State.LastAttempt==DateTime.MinValue?"　尚未扫描":"　更新于 "+db.State.Latest.Time.ToString("MM-dd HH:mm:ss"));
            string selected=findings.SelectedItems.Count>0?((SoftwareFinding)findings.SelectedItems[0].Tag).Key:"";
            var checkedIds=new HashSet<string>(findings.CheckedItems.Cast<ListViewItem>().Select(i=>((SoftwareFinding)i.Tag).Key));
            findings.BeginUpdate(); findings.Items.Clear(); foreach(var f in db.State.Latest.Findings)
            {
                var item=new ListViewItem(new[]{db.Disposition(f),f.Name,f.Category,f.Root}) { Tag=f,Checked=checkedIds.Contains(f.Key) }; findings.Items.Add(item); if(f.Key==selected) item.Selected=true;
            } findings.EndUpdate();
            string selectedRule=ChosenRule()==null?"":ChosenRule().Id;
            rules.BeginUpdate(); rules.Items.Clear(); foreach(var r in db.Catalog.Rules)
            {
                var item=new ListViewItem(new[]{db.State.AllowedRules.Contains(r.Id)?"永久保留":db.State.DisabledRules.Contains(r.Id)?"停用":"启用",r.Name,r.Id,r.Kind=="Component"?"组件清理":r.Kind=="Installed"?"软件卸载":"Chrome 扩展",r.Revision.ToString()}) { Tag=r }; rules.Items.Add(item); if(r.Id==selectedRule) item.Selected=true;
            } rules.EndUpdate();
            results.BeginUpdate(); results.Items.Clear(); foreach(var r in db.State.Results) results.Items.Add(new ListViewItem(new[]{r.Time.ToString("MM-dd HH:mm:ss"),r.Name,r.Status,r.Detail}) { Tag=r }); results.EndUpdate();
            refreshing=false; ShowFinding();
        }
        protected override void Dispose(bool disposing) { if(disposing) controller.Changed-=RefreshData; base.Dispose(disposing); }
    }
    public sealed class CleanupConfirmation : Form
    {
        public CleanupConfirmation(List<SoftwareFinding> selected)
        {
            Theme.Form(this); Text="确认批量卸载与清理"; Size=new Size(850,650); MinimumSize=new Size(720,500);
            var area=Theme.TextArea(); area.Padding=new Padding(15);
            area.Text="即将处理 "+selected.Count+" 项。请核对以下名称、目录和处理范围：\r\n\r\n"+
                string.Join("\r\n\r\n",selected.Select((f,i)=>(i+1)+". "+f.Name+"\r\n目录："+f.Root+"\r\n依据："+f.Evidence+"\r\n操作："+f.Detail))+
                "\r\n\r\n组件清理会停止其进程并删除专属目录中的设置与缓存；仅保留注册信息备份和操作记录，不提供程序文件或用户数据恢复。请先保存相关工作。\r\n需要管理员授权；部分卸载器仍会显示自身界面。不会自动重启电脑或关闭 Chrome。";
            Controls.Add(area); var buttons=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=57,Padding=new Padding(10) }; Controls.Add(buttons);
            buttons.Controls.Add(Theme.Button("确认清理这些项目",()=>DialogResult=DialogResult.OK,true)); var cancel=Theme.Button("取消",()=>DialogResult=DialogResult.Cancel,false); buttons.Controls.Add(cancel); CancelButton=cancel; ActiveControl=cancel; ResumeLayout(true);
        }
    }
    public sealed class InstalledSoftwarePicker : Form
    {
        public InstalledApp SelectedApp; readonly ListView list=new ListView(); readonly TextBox filter=new TextBox(); readonly List<InstalledApp> apps;
        public InstalledSoftwarePicker()
        {
            Theme.Form(this); Text="从已安装软件添加到名单"; Size=new Size(900,590); var errors=new List<string>(); apps=SoftwareScanner.Inventory(errors);
            var header=new Label { Dock=DockStyle.Top,Height=56,Text="添加名单只表示你不需要该软件，不会立即卸载。\r\n缺少名称、发布者或安装项 ID 的项目不能通过此方式添加。" }; Controls.Add(header);
            filter.Dock=DockStyle.Top; Controls.Add(filter); filter.BringToFront();
            list.Dock=DockStyle.Fill; list.View=View.Details; list.FullRowSelect=true; list.MultiSelect=false; list.HideSelection=false;
            list.Columns.Add("软件",310); list.Columns.Add("发布者",240); list.Columns.Add("安装项 ID",300); Controls.Add(list); list.BringToFront();
            var bottom=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=55,Padding=new Padding(8) }; Controls.Add(bottom);
            bottom.Controls.Add(Theme.Button("将选中软件加入名单",()=>
            {
                if(list.SelectedItems.Count==0) return; SelectedApp=(InstalledApp)list.SelectedItems[0].Tag;
                if(string.IsNullOrWhiteSpace(SelectedApp.Publisher)) throw new InvalidOperationException("该安装项没有发布者，不能建立可靠的精确规则。");
                DialogResult=DialogResult.OK;
            },true));
            filter.TextChanged+=delegate { Render(); }; Render(); ResumeLayout(true);
        }
        void Render()
        { list.BeginUpdate(); list.Items.Clear(); foreach(var app in apps.Where(a=>(a.Name+" "+a.Publisher).IndexOf(filter.Text,StringComparison.OrdinalIgnoreCase)>=0).OrderBy(a=>a.Name)) list.Items.Add(new ListViewItem(new[]{app.Name,app.Publisher,app.Id}) { Tag=app }); list.EndUpdate(); }
    }
}

