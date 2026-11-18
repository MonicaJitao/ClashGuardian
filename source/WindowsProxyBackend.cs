using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ClashGuardian
{
    internal sealed class WindowsProxyBackend : IProxyBackend
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct PerConnValue
        {
            [FieldOffset(0)] internal uint Number;
            [FieldOffset(0)] internal IntPtr StringPointer;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PerConnOption
        {
            internal uint Option;
            internal PerConnValue Value;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PerConnOptionList
        {
            internal uint Size;
            internal IntPtr Connection;
            internal uint Count;
            internal uint Error;
            internal IntPtr Options;
        }

        [DllImport("wininet.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool InternetQueryOption(IntPtr internet, int option, ref PerConnOptionList buffer, ref int length);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("iphlpapi.dll", SetLastError=true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct TcpOwnerRow
        {
            internal uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId;
        }

        private readonly string clashPath;
        private readonly string clashDir;
        private readonly string sysproxyPath;
        private readonly int session;

        private WindowsProxyBackend(string clashPath, string sysproxyPath, int session)
        {
            this.clashPath=clashPath;
            clashDir=Path.GetDirectoryName(clashPath);
            this.sysproxyPath=sysproxyPath;
            this.session=session;
        }

        internal static WindowsProxyBackend Find()
        {
            int session=Process.GetCurrentProcess().SessionId;
            foreach (Process p in Process.GetProcessesByName("Clash for Windows"))
            {
                try
                {
                    if (p.SessionId != session) continue;
                    string exe=p.MainModule.FileName;
                    string ver=FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "";
                    if (!ver.StartsWith("0.20.7", StringComparison.Ordinal)) continue;
                    string binary=Path.Combine(Path.GetDirectoryName(exe), @"resources\static\files\win\common\sysproxy.exe");
                    if (File.Exists(binary)) return new WindowsProxyBackend(exe,binary,session);
                }
                catch { }
            }
            return null;
        }

        internal bool IsClashRunning()
        {
            foreach (var p in Process.GetProcessesByName("Clash for Windows"))
            {
                try { if (p.SessionId == session && String.Equals(p.MainModule.FileName,clashPath,StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
            }
            return false;
        }

        public ProxySnapshot Snapshot()
        {
            const int count=4, perConnection=75;
            int unit=Marshal.SizeOf(typeof(PerConnOption));
            IntPtr options=Marshal.AllocHGlobal(unit*count);
            var values=new PerConnOption[count];
            try
            {
                for (int i=0; i<count; i++)
                {
                    values[i].Option=(uint)(i+1);
                    Marshal.StructureToPtr(values[i], IntPtr.Add(options,i*unit), false);
                }
                var list=new PerConnOptionList { Size=(uint)Marshal.SizeOf(typeof(PerConnOptionList)), Count=count, Options=options };
                int size=Marshal.SizeOf(typeof(PerConnOptionList));
                if (!InternetQueryOption(IntPtr.Zero,perConnection,ref list,ref size))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                for (int i=0;i<count;i++) values[i]=(PerConnOption)Marshal.PtrToStructure(IntPtr.Add(options,i*unit),typeof(PerConnOption));
                return new ProxySnapshot {
                    Flags=values[0].Value.Number,
                    Server=ReadString(values[1].Value.StringPointer),
                    Bypass=ReadString(values[2].Value.StringPointer),
                    PacUrl=ReadString(values[3].Value.StringPointer),
                    SysproxyPath=sysproxyPath
                };
            }
            finally
            {
                for (int i=1;i<count;i++) if (values[i].Value.StringPointer!=IntPtr.Zero) GlobalFree(values[i].Value.StringPointer);
                Marshal.FreeHGlobal(options);
            }
        }

        private static string ReadString(IntPtr p) { return p==IntPtr.Zero ? "" : (Marshal.PtrToStringUni(p) ?? ""); }

        internal bool BelongsToClash(ProxySnapshot s)
        {
            return s!=null && s.IsManual() && CanTarget(s);
        }

        internal bool CanTarget(ProxySnapshot s)
        {
            if (s==null || (s.Flags!=1 && s.Flags!=3) || String.IsNullOrWhiteSpace(s.Server) || !IsClashRunning()) return false;
            // A retained endpoint is only safe if a core from this Clash installation owns it.
            var m=Regex.Match(s.Server ?? "", @"^(?:127\.0\.0\.1|localhost):(\d{1,5})$", RegexOptions.IgnoreCase);
            int port;
            if (!m.Success || !Int32.TryParse(m.Groups[1].Value,out port) || port<1 || port>65535) return false;
            return IsClashListening(port);
        }

        private bool IsClashListening(int port)
        {
            int size=0;
            uint code=GetExtendedTcpTable(IntPtr.Zero,ref size,true,2,3,0);
            if (size<=0 || (code!=122 && code!=0)) return false;
            IntPtr buffer=Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer,ref size,true,2,3,0)!=0) return false;
                int count=Marshal.ReadInt32(buffer);
                int step=Marshal.SizeOf(typeof(TcpOwnerRow));
                for(int i=0;i<count;i++)
                {
                    var row=(TcpOwnerRow)Marshal.PtrToStructure(IntPtr.Add(buffer,4+i*step),typeof(TcpOwnerRow));
                    int p=(int)(((row.LocalPort & 255)<<8)|((row.LocalPort>>8)&255));
                    if (p!=port || (row.LocalAddress!=0 && row.LocalAddress!=0x0100007f)) continue;
                    try
                    {
                        Process owner=Process.GetProcessById((int)row.ProcessId);
                        string name=owner.ProcessName;
                        string ownerPath=owner.MainModule.FileName;
                        if (owner.SessionId==session && name.StartsWith("clash",StringComparison.OrdinalIgnoreCase) &&
                            ownerPath.StartsWith(clashDir+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return false;
        }

        public bool CanReset(ProxySnapshot snapshot) { return CanTarget(snapshot); }

        [DllImport("wininet.dll", EntryPoint="InternetSetOptionW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool InternetSetOptionList(IntPtr internet, int option, ref PerConnOptionList buffer, int length);
        [DllImport("wininet.dll", EntryPoint="InternetSetOptionW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool InternetSetOptionRaw(IntPtr internet, int option, IntPtr buffer, int length);

        public void Disable(ProxySnapshot prior) { SetProxy(1, prior, false); }
        public void Enable(ProxySnapshot prior) { SetProxy(prior.Flags, prior, true); }

        private static void SetProxy(uint flags, ProxySnapshot prior, bool restoreDetails)
        {
            int count=restoreDetails ? 3 : 1;
            int unit=Marshal.SizeOf(typeof(PerConnOption));
            IntPtr options=Marshal.AllocHGlobal(unit*count);
            IntPtr server=IntPtr.Zero, bypass=IntPtr.Zero;
            try
            {
                var first=new PerConnOption { Option=1 };
                first.Value.Number=flags;
                Marshal.StructureToPtr(first,options,false);
                if(restoreDetails)
                {
                    server=Marshal.StringToHGlobalUni(prior.Server ?? "");
                    bypass=Marshal.StringToHGlobalUni(prior.Bypass ?? "");
                    var second=new PerConnOption { Option=2 };
                    second.Value.StringPointer=server;
                    var third=new PerConnOption { Option=3 };
                    third.Value.StringPointer=bypass;
                    Marshal.StructureToPtr(second,IntPtr.Add(options,unit),false);
                    Marshal.StructureToPtr(third,IntPtr.Add(options,2*unit),false);
                }
                var list=new PerConnOptionList {
                    Size=(uint)Marshal.SizeOf(typeof(PerConnOptionList)),
                    Count=(uint)count,
                    Options=options
                };
                if(!InternetSetOptionList(IntPtr.Zero,75,ref list,Marshal.SizeOf(typeof(PerConnOptionList))))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                bool changed=InternetSetOptionRaw(IntPtr.Zero,39,IntPtr.Zero,0);
                int changedError=changed ? 0 : Marshal.GetLastWin32Error();
                bool refreshed=InternetSetOptionRaw(IntPtr.Zero,37,IntPtr.Zero,0);
                int refreshError=refreshed ? 0 : Marshal.GetLastWin32Error();
                if(!changed || !refreshed)
                    throw new InvalidOperationException("代理设置已写入，但系统刷新失败（"+changedError+"/"+refreshError+"）。");
            }
            finally
            {
                if(server!=IntPtr.Zero) Marshal.FreeHGlobal(server);
                if(bypass!=IntPtr.Zero) Marshal.FreeHGlobal(bypass);
                Marshal.FreeHGlobal(options);
            }
        }

    }
}
