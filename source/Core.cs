using System;

namespace ClashGuardian
{
    internal enum ProbeDecision { None, RetryNow, Reset }

    internal sealed class FailurePolicy
    {
        private int failures;
        private long? lastAttemptMs;
        private readonly long cooldownMs;

        internal FailurePolicy(long cooldownMs) { this.cooldownMs = cooldownMs; }
        internal int Failures { get { return failures; } }

        internal ProbeDecision Record(bool ok, long nowMs)
        {
            if (ok)
            {
                failures = 0;
                lastAttemptMs = null;
                return ProbeDecision.None;
            }
            failures++;
            if (failures == 1) return ProbeDecision.RetryNow;
            if (lastAttemptMs.HasValue && nowMs - lastAttemptMs.Value < cooldownMs)
                return ProbeDecision.None;
            lastAttemptMs = nowMs;
            return ProbeDecision.Reset;
        }

        internal void Clear()
        {
            failures = 0;
            lastAttemptMs = null;
        }
    }

    internal sealed class ProxySnapshot
    {
        internal uint Flags;
        internal string Server;
        internal string Bypass;
        internal string PacUrl;
        internal string SysproxyPath;

        internal bool IsManual()
        {
            return Flags == 3 && !String.IsNullOrWhiteSpace(Server);
        }

        internal bool EquivalentTo(ProxySnapshot other)
        {
            return other != null && Flags == other.Flags &&
                String.Equals(Server ?? "", other.Server ?? "", StringComparison.OrdinalIgnoreCase) &&
                SameBypassRules(Bypass, other.Bypass) &&
                String.Equals(PacUrl ?? "", other.PacUrl ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameBypassRules(string first, string second)
        {
            string[] a=(first ?? "").Split(';');
            string[] b=(second ?? "").Split(';');
            if(a.Length!=b.Length) return false;
            Array.Sort(a,StringComparer.OrdinalIgnoreCase);
            Array.Sort(b,StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<a.Length;i++)
                if(!String.Equals(a[i],b[i],StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
    }

    internal interface IProxyBackend
    {
        ProxySnapshot Snapshot();
        bool CanReset(ProxySnapshot snapshot);
        void Disable(ProxySnapshot prior);
        void Enable(ProxySnapshot prior);
    }

    internal sealed class ResetOutcome
    {
        internal bool Success;
        internal bool Attempted;
        internal bool OffObserved;
        internal bool Restored;
        internal bool ConfigurationChanged;
        internal string Error;
    }

    internal static class Recovery
    {
        private static ProxySnapshot WithFlags(ProxySnapshot p, uint flags)
        {
            return new ProxySnapshot {Flags=flags,Server=p.Server,Bypass=p.Bypass,PacUrl=p.PacUrl,SysproxyPath=p.SysproxyPath};
        }

        // Once the off step has started, always attempt to restore a known proxy,
        // except when another application has changed the configuration.
        internal static ResetOutcome Reset(IProxyBackend backend)
        {
            var result=new ResetOutcome();
            ProxySnapshot original;
            try
            {
                original=backend.Snapshot();
                if(original==null || (original.Flags!=1 && original.Flags!=3) ||
                    String.IsNullOrWhiteSpace(original.Server) || !backend.CanReset(original))
                {
                    result.Error="当前代理无法确认属于 Clash";
                    return result;
                }
                ProxySnapshot before=backend.Snapshot();
                if(!original.EquivalentTo(before))
                {
                    result.ConfigurationChanged=true;
                    result.Error="代理设置在操作前发生变化";
                    return result;
                }
            }
            catch(Exception ex) { result.Error="检查代理状态失败："+ex.Message; return result; }

            ProxySnapshot wanted=WithFlags(original,3);
            ProxySnapshot expectedOff=WithFlags(original,1);
            try
            {
                result.Attempted=true;
                try { backend.Disable(original); }
                catch(Exception ex) { result.Error="关闭代理时出现异常："+ex.Message; }
                try
                {
                    ProxySnapshot off=backend.Snapshot();
                    result.OffObserved=expectedOff.EquivalentTo(off);
                    if(!result.OffObserved)
                    {
                        if(off!=null && !wanted.EquivalentTo(off))
                        {
                            result.ConfigurationChanged=true;
                            result.Error="代理设置在重置期间发生变化，未覆盖";
                        }
                        else if(String.IsNullOrEmpty(result.Error)) result.Error="未能确认代理关闭";
                    }
                }
                catch(Exception ex) { result.Error=(result.Error??"")+" 读取关闭状态失败："+ex.Message; }
            }
            finally
            {
                if(result.Attempted && !result.ConfigurationChanged)
                {
                    for(int i=0;i<2 && !result.Restored && !result.ConfigurationChanged;i++)
                    {
                        ProxySnapshot current=null;
                        try { current=backend.Snapshot(); }
                        catch(Exception ex) { result.Error=(result.Error??"")+" 读取状态失败："+ex.Message; }
                        if(wanted.EquivalentTo(current)) { result.Restored=true; break; }
                        if(current!=null && !expectedOff.EquivalentTo(current))
                        {
                            result.ConfigurationChanged=true;
                            result.Error=(result.Error??"")+" 当前代理已被其他操作改变，未覆盖";
                            break;
                        }
                        try { backend.Enable(wanted); }
                        catch(Exception ex) { result.Error=(result.Error??"")+" 恢复时出现异常："+ex.Message; }
                        try { result.Restored=wanted.EquivalentTo(backend.Snapshot()); }
                        catch(Exception ex) { result.Error=(result.Error??"")+" 读取恢复状态失败："+ex.Message; }
                    }
                }
            }
            if(!result.Restored && String.IsNullOrEmpty(result.Error)) result.Error="无法确认代理已恢复";
            result.Success=result.OffObserved && result.Restored && !result.ConfigurationChanged;
            if(result.Success) result.Error=null;
            return result;
        }
    }
}
