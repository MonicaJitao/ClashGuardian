using System;

namespace ClashGuardian
{
    internal sealed class FakeBackend : IProxyBackend
    {
        public ProxySnapshot State = Manual();
        public int OffCalls, OnCalls;
        public bool FailOffBefore, FailOffAfter, FailFirstOn, FailAllOn, FailOnAfter;
        public bool AllowOwner = true;
        public bool ChangeBeforeOff, ChangeAfterOff, ChangeBeforeRestore;
        public int SnapshotCalls;
        public static ProxySnapshot Manual()
        {
            return new ProxySnapshot { Flags = 3, Server = "127.0.0.1:7890", Bypass = "localhost;*.local", PacUrl = "", SysproxyPath = "test" };
        }
        public ProxySnapshot Snapshot()
        {
            SnapshotCalls++;
            if((ChangeBeforeOff && SnapshotCalls==2) || (ChangeBeforeRestore && SnapshotCalls==4)) { State.Server="other-proxy:8080"; State.Flags=3; }
            return new ProxySnapshot { Flags=State.Flags, Server=State.Server, Bypass=State.Bypass, PacUrl=State.PacUrl, SysproxyPath=State.SysproxyPath };
        }
        public bool CanReset(ProxySnapshot p) { return AllowOwner; }
        public void Disable(ProxySnapshot p)
        {
            OffCalls++;
            if (FailOffBefore) throw new Exception("off before");
            State.Flags = 1;
            if(ChangeAfterOff) { State.Server="other-proxy:8080"; State.Flags=3; }
            if (FailOffAfter) throw new Exception("off after");
        }
        public void Enable(ProxySnapshot p)
        {
            OnCalls++;
            if (FailAllOn || (FailFirstOn && OnCalls == 1)) throw new Exception("on failed");
            State = new ProxySnapshot {Flags=p.Flags, Server=p.Server, Bypass=p.Bypass, PacUrl=p.PacUrl, SysproxyPath=p.SysproxyPath};
            if (FailOnAfter) throw new Exception("on after");
        }
    }

    internal static class CoreTests
    {
        static int checks;
        static void Eq<T>(T got, T want, string message)
        {
            checks++;
            if (!Object.Equals(got, want)) throw new Exception(message + ": expected " + want + ", got " + got);
        }
        public static int Main()
        {
            try
            {
                var p = new FailurePolicy(5000);
                Eq(p.Record(true, 0), ProbeDecision.None, "healthy");
                Eq(p.Record(false, 1000), ProbeDecision.RetryNow, "first failure");
                Eq(p.Record(true, 1100), ProbeDecision.None, "transient cleared");
                Eq(p.Record(false, 2000), ProbeDecision.RetryNow, "first failure again");
                Eq(p.Record(false, 2100), ProbeDecision.Reset, "second failure resets");
                Eq(p.Record(false, 3000), ProbeDecision.None, "cooldown");
                Eq(p.Record(false, 7099), ProbeDecision.None, "cooldown boundary");
                Eq(p.Record(false, 7100), ProbeDecision.Reset, "persistent outage retry");
                Eq(p.Record(true, 7200), ProbeDecision.None, "recovery");
                Eq(p.Record(false, 7300), ProbeDecision.RetryNow, "new episode first failure");
                Eq(p.Record(false, 7400), ProbeDecision.Reset, "new episode no stale cooldown");
                p.Clear();
                Eq(p.Record(false, 7500), ProbeDecision.RetryNow, "pause clears policy");

                var good = new FakeBackend();
                var a = Recovery.Reset(good);
                Eq(a.Success, true, "normal success");
                Eq(a.Attempted, true, "normal reset attempted");
                Eq(a.OffObserved, true, "confirmed off");
                Eq(a.Restored, true, "confirmed restore");
                Eq(good.OffCalls, 1, "one off");
                Eq(good.OnCalls, 1, "one on");
                Eq(good.State.EquivalentTo(FakeBackend.Manual()), true, "snapshot restored");

                var notManual = new FakeBackend();
                notManual.State.Flags = 1;
                var b = Recovery.Reset(notManual);
                Eq(b.Success, true, "disabled Clash proxy restored");
                Eq(b.Attempted, true, "disabled proxy attempted");
                Eq(notManual.OffCalls, 1, "off command when disabled");
                Eq(notManual.OnCalls, 1, "on command when disabled");
                Eq(notManual.State.Flags, (uint)3, "disabled proxy ends enabled");

                var missing=new FakeBackend(); missing.State.Flags=1; missing.State.Server="";
                var missingResult=Recovery.Reset(missing);
                Eq(missingResult.Attempted,false,"unknown endpoint not modified");
                Eq(missing.OnCalls,0,"unknown endpoint never enabled");

                var pac=new FakeBackend(); pac.State.Flags=5;
                var pacResult=Recovery.Reset(pac);
                Eq(pacResult.Attempted,false,"PAC proxy not modified");

                var beforeChange=new FakeBackend { ChangeBeforeOff=true };
                var beforeResult=Recovery.Reset(beforeChange);
                Eq(beforeResult.ConfigurationChanged,true,"intervening pre-reset change detected");
                Eq(beforeChange.OffCalls,0,"pre-reset change preserved");

                var afterChange=new FakeBackend { ChangeAfterOff=true };
                var afterResult=Recovery.Reset(afterChange);
                Eq(afterResult.ConfigurationChanged,true,"intervening post-off change detected");
                Eq(afterChange.OnCalls,0,"post-off change not overwritten");
                Eq(afterChange.State.Server,"other-proxy:8080","post-off endpoint preserved");

                var restoreChange=new FakeBackend { ChangeBeforeRestore=true };
                var restoreResult=Recovery.Reset(restoreChange);
                Eq(restoreResult.ConfigurationChanged,true,"intervening pre-restore change detected");
                Eq(restoreChange.OnCalls,0,"pre-restore change not overwritten");

                var other = new FakeBackend { AllowOwner = false };
                var ownerResult = Recovery.Reset(other);
                Eq(ownerResult.Success, false, "unowned proxy not touched");
                Eq(ownerResult.Attempted, false, "unowned proxy not attempted");
                Eq(other.OffCalls, 0, "no off on unowned proxy");
                Eq(other.OnCalls, 0, "no on on unowned proxy");

                var partial = new FakeBackend { FailOffAfter = true };
                var c = Recovery.Reset(partial);
                Eq(c.Success, true, "off exception after applying state is verified");
                Eq(c.OffObserved, true, "off state still checked after exception");
                Eq(c.Restored, true, "restored despite off exception");
                Eq(partial.State.Flags, (uint)3, "partial failure does not leave proxy off");

                var offBefore = new FakeBackend { FailOffBefore = true };
                var d = Recovery.Reset(offBefore);
                Eq(d.Restored, true, "restore even if off command error");

                var flakyOn = new FakeBackend { FailFirstOn = true };
                var e = Recovery.Reset(flakyOn);
                Eq(e.Success, true, "retry successful restore");
                Eq(flakyOn.OnCalls, 2, "restore retried once");

                var onApplied = new FakeBackend { FailOnAfter = true };
                var after = Recovery.Reset(onApplied);
                Eq(after.Success, true, "restore verified even after exception");
                Eq(onApplied.OnCalls, 1, "no extra restore when already applied");

                var brokenOn = new FakeBackend { FailAllOn = true };
                var f = Recovery.Reset(brokenOn);
                Eq(f.Success, false, "unrestorable is an error");
                Eq(f.Restored, false, "unrestorable detected");
                Eq(brokenOn.OnCalls, 2, "retry bounded");

                var same = FakeBackend.Manual();
                var reordered = FakeBackend.Manual(); reordered.Bypass="*.LOCAL;localhost";
                Eq(same.EquivalentTo(reordered), true, "equivalent bypass order and case accepted");
                var changed = FakeBackend.Manual(); changed.Bypass += ";another";
                Eq(same.EquivalentTo(changed), false, "bypass change detected");
                var removed = FakeBackend.Manual(); removed.Bypass="localhost";
                Eq(same.EquivalentTo(removed), false, "missing bypass entry detected");
                Console.WriteLine("PASS " + checks + " assertions");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
        }
    }
}
