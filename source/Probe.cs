using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClashGuardian
{
    internal static class ProbeWorker
    {
        [DllImport("wininet.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr InternetOpen(string userAgent, int access, string proxy, string bypass, int flags);
        [DllImport("wininet.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr InternetOpenUrl(IntPtr internet, string url, string headers, int headersLength, uint flags, IntPtr context);
        [DllImport("wininet.dll", SetLastError=true)]
        private static extern bool InternetSetOption(IntPtr internet, int option, ref int value, int length);
        [DllImport("wininet.dll", SetLastError=true)]
        private static extern bool InternetCloseHandle(IntPtr internet);
        [DllImport("wininet.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool HttpQueryInfo(IntPtr request, int info, ref int buffer, ref int length, IntPtr index);

        internal static int Run()
        {
            try
            {
                Console.OutputEncoding=System.Text.Encoding.UTF8;
                Console.WriteLine("READY");
                string line;
                while ((line=Console.ReadLine())!=null)
                {
                    if (line=="QUIT") return 0;
                    if (line!="PROBE") { Console.WriteLine("ERROR"); continue; }
                    Console.WriteLine(Check() ? "OK" : "FAIL");
                }
                return 0;
            }
            catch { return 2; }
        }

        private static bool Check()
        {
            IntPtr root=IntPtr.Zero, request=IntPtr.Zero;
            try
            {
                root=InternetOpen("ClashGuardian/1.0",0,null,null,0);
                if (root==IntPtr.Zero) return false;
                int timeout=1000, retries=1;
                InternetSetOption(root,2,ref timeout,4);
                InternetSetOption(root,5,ref timeout,4);
                InternetSetOption(root,6,ref timeout,4);
                InternetSetOption(root,3,ref retries,4);
                request=InternetOpenUrl(root,"https://www.google.com/generate_204",null,0,0x84200200u,IntPtr.Zero);
                if (request==IntPtr.Zero) return false;
                int status=0, length=4;
                return HttpQueryInfo(request,0x20000013,ref status,ref length,IntPtr.Zero) && status==204;
            }
            catch { return false; }
            finally
            {
                if (request!=IntPtr.Zero) InternetCloseHandle(request);
                if (root!=IntPtr.Zero) InternetCloseHandle(root);
            }
        }
    }

    internal sealed class ProbeClient : IDisposable
    {
        private Process worker;
        private Task<Process> readyTask;
        private readonly object gate=new object();
        private readonly string executable;
        internal ProbeClient(string executable) { this.executable=executable; }
        internal Task WarmAsync() { return EnsureWorkerAsync(); }

        internal bool IsAlive
        {
            get { lock(gate) { try { return worker!=null && !worker.HasExited; } catch { return false; } } }
        }

        internal async Task<bool> CheckAsync()
        {
            Process p=await EnsureWorkerAsync();
            try
            {
                await p.StandardInput.WriteLineAsync("PROBE");
                await p.StandardInput.FlushAsync();
                Task<string> response=p.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(response,Task.Delay(1000))!=response)
                {
                    StopProcess(p);
                    return false;
                }
                string value=await response;
                if (value=="OK") return true;
                if (value=="FAIL") return false;
                StopProcess(p);
                throw new InvalidOperationException("检测进程意外结束。");
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { StopProcess(p); throw new InvalidOperationException("检测进程异常：" + ex.Message); }
        }

        private Task<Process> EnsureWorkerAsync()
        {
            lock(gate)
            {
                try { if (worker!=null && !worker.HasExited && readyTask!=null) return readyTask; } catch { }
                if (worker!=null) { try { worker.Dispose(); } catch { } worker=null; }
                var info=new ProcessStartInfo {
                    FileName=executable, Arguments="--probe-worker", WorkingDirectory=Path.GetDirectoryName(executable),
                    UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden,
                    RedirectStandardInput=true, RedirectStandardOutput=true
                };
                worker=Process.Start(info);
                if (worker==null) throw new InvalidOperationException("无法启动检测进程。");
                readyTask=WaitReadyAsync(worker);
                return readyTask;
            }
        }

        private async Task<Process> WaitReadyAsync(Process p)
        {
            Task<string> ready=p.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(ready,Task.Delay(2000))!=ready)
            {
                StopProcess(p);
                throw new InvalidOperationException("检测进程启动超时。");
            }
            string result=await ready;
            if (result!=null) result=result.TrimStart('\uFEFF');
            if (result!="READY") { StopProcess(p); throw new InvalidOperationException("检测进程未能启动。"); }
            return p;
        }

        private void StopProcess(Process p)
        {
            lock(gate)
            {
                try { if (p!=null && !p.HasExited) p.Kill(); } catch { }
                try { if (p!=null) p.Dispose(); } catch { }
                if (Object.ReferenceEquals(worker,p)) { worker=null; readyTask=null; }
            }
        }

        internal void Stop() { lock(gate) { if (worker!=null) StopProcess(worker); } }
        public void Dispose() { Stop(); }
    }

    internal sealed class ProbePool : IDisposable
    {
        private readonly ProbeClient[] probes;
        private int active;
        private bool stopped;
        private readonly object gate=new object();

        internal ProbePool(string executable)
        {
            probes=new [] {new ProbeClient(executable),new ProbeClient(executable)};
        }

        internal async Task WarmAsync()
        {
            await Task.WhenAll(probes[0].WarmAsync(),probes[1].WarmAsync());
        }

        internal async Task<bool> CheckAsync()
        {
            ProbeClient chosen;
            lock(gate) { if(stopped) throw new InvalidOperationException("监控已暂停。"); chosen=probes[active]; }
            bool result=await chosen.CheckAsync();
            if (!chosen.IsAlive)
            {
                lock(gate)
                {
                    if (!stopped)
                    {
                        active=1-active;
                        Task warm=chosen.WarmAsync();
                        warm.ContinueWith(t=>{ var ignored=t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
            }
            return result;
        }

        public void Dispose()
        {
            lock(gate)
            {
                if(stopped)return;
                stopped=true;
                probes[0].Dispose(); probes[1].Dispose();
            }
        }
    }
}
