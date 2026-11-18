using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ClashGuardian
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool first;
            using (var mutex=new Mutex(true,@"Local\ClashGuardian-20261117",out first))
            {
                if (!first) { MessageBox.Show("Clash 自动恢复已经在运行，请在右下角托盘查看。","Clash 自动恢复",MessageBoxButtons.OK,MessageBoxIcon.Information); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new GuardContext(args.Length>0 ? args[0] : ""));
            }
        }
    }

    internal sealed class GuardContext : ApplicationContext
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
        private readonly object gate=new object();
        private readonly Control dispatcher=new Control();
        private readonly NotifyIcon icon=new NotifyIcon();
        private readonly ContextMenuStrip menu=new ContextMenuStrip();
        private readonly ToolStripMenuItem statusItem=new ToolStripMenuItem();
        private readonly ToolStripMenuItem lastItem=new ToolStripMenuItem();
        private readonly ToolStripMenuItem toggleItem=new ToolStripMenuItem();
        private readonly ToolStripMenuItem checkItem=new ToolStripMenuItem();
        private readonly SemaphoreSlim wake=new SemaphoreSlim(0,1);
        private readonly Icon green=MakeIcon(Color.FromArgb(44,165,106));
        private readonly Icon amber=MakeIcon(Color.FromArgb(235,159,45));
        private readonly Icon gray=MakeIcon(Color.FromArgb(134,143,153));
        private readonly string workerPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ClashProbe.exe");
        private CancellationTokenSource cancellation;
        private ProbePool pool;
        private Task runner;
        private bool active,resetting,exiting,closed;
        private int epoch;
        private string status="准备中";
        private string last="暂无";
        private int resetCount;
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private System.Windows.Forms.Timer smokeTimer;
        private Form detailsForm;
        private Label detailsLabel;
        private System.Windows.Forms.Timer detailsTimer;

        internal GuardContext(string testMode)
        {
            var unused=dispatcher.Handle;
            statusItem.Enabled=false;
            lastItem.Enabled=false;
            toggleItem.Click+=delegate { if (IsActive()) Pause(false); else StartMonitoring(); };
            checkItem.Text="立即检测";
            checkItem.Click+=delegate {
                if (!IsActive()) { MessageBox.Show("请先开启监控。","Clash 自动恢复",MessageBoxButtons.OK,MessageBoxIcon.Information); return; }
                if(wake.CurrentCount==0) try { wake.Release(); } catch(SemaphoreFullException) { }
            };
            var details=new ToolStripMenuItem("查看状态",null,delegate { ShowDetails(); });
            var exit=new ToolStripMenuItem("退出",null,delegate { Pause(true); });
            menu.Items.Add(statusItem);
            menu.Items.Add(lastItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(toggleItem);
            menu.Items.Add(checkItem);
            menu.Items.Add(details);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
            menu.Opening+=delegate { RefreshMenu(); };
            icon.ContextMenuStrip=menu;
            icon.Icon=gray;
            icon.Text="Clash 自动恢复";
            icon.Visible=true;
            icon.DoubleClick+=delegate { ShowDetails(); };
            StartMonitoring();
            if(testMode=="--smoke")
            {
                smokeTimer=new System.Windows.Forms.Timer { Interval=12000 };
                smokeTimer.Tick+=delegate { smokeTimer.Stop(); Console.WriteLine("SMOKE healthy="+status.Contains("连接正常")+" resetCount="+resetCount); Pause(true); };
                smokeTimer.Start();
            }
            else if(testMode=="--smoke-exercise")
            {
                int phase=0;
                smokeTimer=new System.Windows.Forms.Timer { Interval=4000 };
                smokeTimer.Tick+=delegate {
                    phase++;
                    if(phase==1) { Pause(false); Console.WriteLine("PAUSE active="+IsActive()); }
                    else if(phase==2) { Console.WriteLine("PAUSE workers="+Process.GetProcessesByName("ClashProbe").Length); StartMonitoring(); Console.WriteLine("RESUME active="+IsActive()); }
                    else { smokeTimer.Stop(); Console.WriteLine("RESUME healthy="+status.Contains("连接正常")); Pause(true); }
                };
                smokeTimer.Start();
            }
        }

        private static Icon MakeIcon(Color color)
        {
            using(var bitmap=new Bitmap(32,32))
            using(var g=Graphics.FromImage(bitmap))
            using(var fill=new SolidBrush(color))
            using(var pen=new Pen(Color.White,2))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(fill,2,2,28,28);
                g.DrawArc(pen,8,8,16,16,50,270);
                g.DrawLine(pen,19,7,23,9);
                g.DrawLine(pen,23,9,21,13);
                IntPtr handle=bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        private bool IsActive() { lock(gate) return active; }

        private void RefreshMenu()
        {
            lock(gate)
            {
                statusItem.Text="状态："+status;
                lastItem.Text="最近："+last;
                toggleItem.Text=active ? "暂停监控" : "开启监控";
                checkItem.Enabled=active;
            }
        }

        private void SetStatus(string text, Icon selected)
        {
            lock(gate) { status=text; }
            try { icon.Icon=selected; icon.Text=("Clash 自动恢复："+text).Length>60 ? "Clash 自动恢复" : "Clash 自动恢复："+text; } catch { }
        }

        private void Post(int runId, Action action)
        {
            try
            {
                if (closed || dispatcher.IsDisposed) return;
                dispatcher.BeginInvoke((Action)delegate {
                    lock(gate) { if (closed || runId!=epoch) return; }
                    action();
                });
            }
            catch { }
        }

        private void StartMonitoring()
        {
            lock(gate)
            {
                if (active || exiting) return;
                if (runner!=null && !runner.IsCompleted)
                {
                    MessageBox.Show("正在完成上一次操作，请稍后再开启。","Clash 自动恢复",MessageBoxButtons.OK,MessageBoxIcon.Information);
                    return;
                }
                if(!File.Exists(workerPath))
                {
                    SetStatus("缺少检测组件",gray);
                    MessageBox.Show("未找到 ClashProbe.exe，请将它与主程序放在同一个文件夹。","Clash 自动恢复",MessageBoxButtons.OK,MessageBoxIcon.Error);
                    return;
                }
                active=true;
                epoch++;
                cancellation=new CancellationTokenSource();
                pool=new ProbePool(workerPath);
                var localPool=pool;
                var token=cancellation.Token;
                int runId=epoch;
                SetStatus("正在启动",amber);
                runner=Task.Run(()=>MonitorLoop(localPool,token,runId));
            }
        }

        private void Pause(bool exit)
        {
            bool finishLater;
            ProbePool localPool;
            lock(gate)
            {
                active=false;
                exiting=exiting || exit;
                epoch++;
                if(cancellation!=null) cancellation.Cancel();
                localPool=pool;
                finishLater=resetting;
            }
            if(localPool!=null) localPool.Dispose();
            if(finishLater) SetStatus("完成代理恢复后暂停",amber);
            else
            {
                SetStatus("已暂停",gray);
                if(exit) CloseApplication();
            }
        }

        private async Task MonitorLoop(ProbePool localPool,CancellationToken token,int runId)
        {
            var policy=new FailurePolicy(5000);
            string blockedStatus=null;
            try
            {
                await localPool.WarmAsync();
                WindowsProxyBackend backend=null;
                while(!token.IsCancellationRequested)
                {
                    long loopStart=clock.ElapsedMilliseconds;
                    if(backend==null || !backend.IsClashRunning()) backend=WindowsProxyBackend.Find();
                    // Always probe Google even when the Windows/Clash settings disagree.
                    bool ok=await localPool.CheckAsync();
                    token.ThrowIfCancellationRequested();
                    ProbeDecision decision=policy.Record(ok,clock.ElapsedMilliseconds);
                    if(ok)
                    {
                        blockedStatus=null;
                        string text=HealthyStatus(backend);
                        Post(runId,delegate {SetStatus(text,green);});
                    }
                    else
                    {
                        string text=blockedStatus ?? (decision==ProbeDecision.RetryNow ? "连接异常 · 正在复查" : "连接异常 · 等待重试");
                        Post(runId,delegate {SetStatus(text,amber);});
                    }
                    if(decision==ProbeDecision.RetryNow)
                    {
                        await Task.Delay(100,token);
                        ok=await localPool.CheckAsync();
                        token.ThrowIfCancellationRequested();
                        decision=policy.Record(ok,clock.ElapsedMilliseconds);
                        if(ok) {
                            blockedStatus=null;
                            string text=HealthyStatus(backend);
                            Post(runId,delegate {SetStatus(text,green);});
                        }
                    }
                    if(decision==ProbeDecision.Reset)
                    {
                        if(backend==null)
                        {
                            lock(gate) { last=DateTime.Now.ToString("HH:mm:ss")+" Clash 未运行，未开关"; }
                            blockedStatus="连接异常 · Clash 未运行";
                            Post(runId,delegate {SetStatus("连接异常 · Clash 未运行",amber);});
                            await WaitUntilNext(loopStart,token);
                            continue;
                        }
                        bool mayReset;
                        lock(gate)
                        {
                            mayReset=active && !token.IsCancellationRequested;
                            if(mayReset) resetting=true;
                        }
                        if(!mayReset) break;
                        Post(runId,delegate {SetStatus("正在重置系统代理",amber);});
                        ResetOutcome result=null;
                        try { result=await Task.Run(()=>Recovery.Reset(backend)); }
                        finally
                        {
                            bool shouldExit, isPaused;
                            lock(gate) { resetting=false; shouldExit=exiting; isPaused=!active; }
                            if(shouldExit) PostUnconditional(delegate {
                                if(result==null) ShowRestoreError(new ResetOutcome { Error="开关结果未知。" });
                                else if(result.Attempted && !result.Restored) ShowRestoreError(result);
                                CloseApplication();
                            });
                            else if(isPaused) PostUnconditional(delegate { SetStatus("已暂停",gray); });
                        }
                        if(result==null) throw new InvalidOperationException("代理重置未返回结果。");
                        if(!result.Attempted)
                        {
                            lock(gate) { last=DateTime.Now.ToString("HH:mm:ss")+" 未开关："+(result.Error??"条件不满足"); }
                            blockedStatus="连接异常 · Clash 代理未确认";
                            string notice=blockedStatus;
                            Post(runId,delegate { SetStatus(notice,amber); });
                            await WaitUntilNext(loopStart,token);
                            continue;
                        }
                        if(result.ConfigurationChanged)
                        {
                            PostUnconditional(delegate { Pause(false); MessageBox.Show("代理设置在操作期间发生变化，监控已暂停。请检查当前设置。"+Environment.NewLine+(result.Error??""),"Clash 自动恢复",MessageBoxButtons.OK,MessageBoxIcon.Warning); });
                            break;
                        }
                        if(!result.Restored)
                        {
                            PostUnconditional(delegate { Pause(false); ShowRestoreError(result); });
                            break;
                        }
                        if(!result.Success)
                        {
                            PostUnconditional(delegate { Pause(false); MessageBox.Show("系统代理重置过程未完全确认，监控已暂停。请检查 Windows 代理和 Clash。"+Environment.NewLine+(result.Error ?? ""),"Clash 自动恢复",MessageBoxButtons.OK,MessageBoxIcon.Warning); });
                            break;
                        }
                        blockedStatus=null;
                        lock(gate) { resetCount++; last=DateTime.Now.ToString("HH:mm:ss")+" 已重置，正在复查"; }
                        if(token.IsCancellationRequested) break;
                        Post(runId,delegate {SetStatus("已重置 · 正在复查",amber);});
                        bool recovered=await localPool.CheckAsync();
                        token.ThrowIfCancellationRequested();
                        policy.Record(recovered,clock.ElapsedMilliseconds);
                        lock(gate) { last=DateTime.Now.ToString("HH:mm:ss")+(recovered?" 已恢复":" 尚未恢复"); }
                        if(recovered) Post(runId,delegate {SetStatus("监控中 · 连接正常",green);});
                        else Post(runId,delegate {SetStatus("仍未恢复 · 将继续尝试",amber);});
                    }
                    await WaitUntilNext(loopStart,token);
                }
            }
            catch(OperationCanceledException) { }
            catch(Exception ex)
            {
                if(!token.IsCancellationRequested)
                {
                    string message=ex.Message;
                    Post(runId,delegate {
                        Pause(false);
                        MessageBox.Show("监控遇到问题，已暂停："+Environment.NewLine+message,"Clash 自动恢复",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    });
                }
            }
            finally { localPool.Dispose(); }
        }

        private static string HealthyStatus(WindowsProxyBackend backend)
        {
            if(backend==null) return "连接正常 · 等待 Clash 启动";
            try { if(!backend.CanTarget(backend.Snapshot())) return "连接正常 · Clash 代理未确认"; }
            catch { return "连接正常 · Clash 代理状态未知"; }
            return "监控中 · 连接正常";
        }

        private async Task WaitUntilNext(long start,CancellationToken token)
        {
            int wait=(int)Math.Max(0,1000-(clock.ElapsedMilliseconds-start));
            await wake.WaitAsync(wait,token);
        }

        private void PostUnconditional(Action action)
        {
            try { if(!closed && !dispatcher.IsDisposed) dispatcher.BeginInvoke(action); } catch { }
        }

        private void ShowRestoreError(ResetOutcome result)
        {
            MessageBox.Show("无法确认系统代理已恢复。请打开 Clash，检查并手动开启 System Proxy。"+Environment.NewLine+(result.Error ?? ""),
                "Clash 自动恢复",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }

        private void UpdateDetails()
        {
            if(detailsLabel==null || detailsLabel.IsDisposed) return;
            string s,l; int count;
            lock(gate) { s=status; l=last; count=resetCount; }
            detailsLabel.Text="状态："+s+Environment.NewLine+"最近处理："+l+Environment.NewLine+
                "本次运行累计开关："+count+" 次"+Environment.NewLine+Environment.NewLine+
                "检测地址：Google generate_204"+Environment.NewLine+"检测间隔：约 1 秒"+Environment.NewLine+
                "持续失败：约每 5 秒尝试一次"+Environment.NewLine+
                "处理方式：重置 Windows 系统代理";
        }

        private void ShowDetails()
        {
            if(detailsForm!=null && !detailsForm.IsDisposed) { detailsForm.Activate(); return; }
            detailsForm=new Form { Text="Clash 自动恢复", ClientSize=new Size(550,245),
                StartPosition=FormStartPosition.CenterScreen, FormBorderStyle=FormBorderStyle.FixedDialog,
                MaximizeBox=false, MinimizeBox=false, ShowInTaskbar=false };
            detailsLabel=new Label { Location=new Point(18,18), Size=new Size(510,185), AutoEllipsis=true };
            var button=new Button { Text="关闭", Location=new Point(440,205), Size=new Size(90,28) };
            button.Click+=delegate { detailsForm.Close(); };
            detailsForm.Controls.Add(detailsLabel); detailsForm.Controls.Add(button);
            detailsTimer=new System.Windows.Forms.Timer { Interval=500 };
            detailsTimer.Tick+=delegate { UpdateDetails(); };
            detailsForm.FormClosed+=delegate { detailsTimer.Stop(); detailsTimer.Dispose(); detailsTimer=null; detailsLabel=null; detailsForm=null; };
            UpdateDetails(); detailsTimer.Start(); detailsForm.Show();
        }

        private void CloseApplication()
        {
            lock(gate) { if(closed)return; closed=true; }
            if(detailsForm!=null && !detailsForm.IsDisposed) detailsForm.Close();
            icon.Visible=false;
            icon.Dispose(); menu.Dispose();
            green.Dispose();amber.Dispose();gray.Dispose();
            dispatcher.Dispose();
            ExitThread();
        }
    }
}
