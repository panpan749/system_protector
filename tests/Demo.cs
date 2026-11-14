using System;
using System.Drawing;
using System.Windows.Forms;
public class Demo
{
    [STAThread] public static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        bool automated=Array.IndexOf(args,"--automated-test")>=0;
        var f=new Form { Text=args.Length>0&&args[0]!="--automated-test"?args[0]:"广告测试 · PopupGuard Demo",Size=new Size(420,255),FormBorderStyle=FormBorderStyle.FixedToolWindow,MaximizeBox=false,ControlBox=!automated,TopMost=true,StartPosition=FormStartPosition.Manual,BackColor=Color.FromArgb(22,45,63) };
        f.SuspendLayout(); f.AutoScaleDimensions=new SizeF(96,96); f.AutoScaleMode=AutoScaleMode.Dpi;
        var area=Screen.PrimaryScreen.WorkingArea; f.Location=new Point(area.Right-f.Width-18,area.Bottom-f.Height-18);
        f.Controls.Add(new Label { Text=automated?"自动测试：模拟广告":"这是一条模拟广告",ForeColor=Color.White,Font=new Font("Microsoft YaHei UI",20,FontStyle.Bold),AutoSize=true,Location=new Point(24,25) });
        f.Controls.Add(new Label { Text=automated?"弹窗卫士正在验证截图、隐藏和恢复。\r\n无需点击，测试结束后会自动关闭。":"用于测试点选、进程溯源、隐藏和恢复。\r\n不会安装软件或联网。",ForeColor=Color.LightGray,Font=new Font("Microsoft YaHei UI",10),AutoSize=true,Location=new Point(25,85) });
        var b=new Button { Text=automated?"测试中，无需操作":"关闭测试弹窗",Enabled=!automated,Location=new Point(25,150),Size=new Size(180,38) }; b.Click+=delegate { f.Close(); }; f.Controls.Add(b);
        f.Shown+=delegate { f.Location=new Point(area.Right-f.Width-18,area.Bottom-f.Height-18); }; f.ResumeLayout(true);
        if(automated)
        {
            string log=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"demo-"+System.Diagnostics.Process.GetCurrentProcess().Id+".log");
            Action<string> write=message=>System.IO.File.AppendAllText(log,DateTime.Now.ToString("o")+" "+message+Environment.NewLine);
            write("START "+f.Text); f.Shown+=delegate { write("SHOWN"); };
            f.FormClosing+=delegate(object sender,FormClosingEventArgs e) { write("CLOSING "+e.CloseReason); };
            f.FormClosed+=delegate { write("CLOSED"); };
        }
        var timeout=new Timer { Interval=120000 }; timeout.Tick+=delegate { f.Close(); }; timeout.Start(); Application.Run(f); timeout.Dispose();
    }
}
