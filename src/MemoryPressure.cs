using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Arabara
{
    // Memory pressure: Windows' low-memory notification, or physical memory load at or
    // above 90%. Under pressure, Low RAM mode freezes idle background tabs after twenty
    // seconds instead of a minute. Nothing polls: the notification is a wait handle,
    // and the load is read only when the freeze timer is scheduled.
    internal sealed partial class BrowserForm
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        [DllImport("kernel32.dll")] private static extern IntPtr CreateMemoryResourceNotification(int type);
        [DllImport("kernel32.dll")] private static extern bool QueryMemoryResourceNotification(IntPtr handle, out bool state);

        internal static readonly TimeSpan PressureFreezeAfter = TimeSpan.FromSeconds(20);
        private const uint PressureLoad = 90;
        internal bool ForceMemoryPressure; // tests only
        private IntPtr lowMemory;
        private ManualResetEvent lowMemoryEvent;
        private RegisteredWaitHandle lowMemoryWait;
        private System.Threading.Timer lowMemoryRearm;

        private bool UnderMemoryPressure()
        {
            if (ForceMemoryPressure) return true;
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (GlobalMemoryStatusEx(ref status) && status.MemoryLoad >= PressureLoad) return true;
            bool low;
            return lowMemory != IntPtr.Zero && QueryMemoryResourceNotification(lowMemory, out low) && low;
        }

        // Pressure only ever shortens the delay.
        private TimeSpan FreezeDelay
        { get { return UnderMemoryPressure() && PressureFreezeAfter < FreezeAfter ? PressureFreezeAfter : FreezeAfter; } }

        private void WatchMemoryPressure()
        {
            lowMemory = CreateMemoryResourceNotification(0); // LowMemoryResourceNotification
            if (lowMemory == IntPtr.Zero) return;
            lowMemoryEvent = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(lowMemory, false) };
            ArmLowMemory();
        }

        // One-shot wait; re-armed a minute after each signal so a lasting low-memory
        // state does not fire continuously.
        private void ArmLowMemory()
        {
            if (closing || lowMemoryEvent == null) return;
            lowMemoryWait = ThreadPool.RegisterWaitForSingleObject(lowMemoryEvent, delegate
            {
                if (closing) return;
                try { BeginInvoke(new Action(delegate { if (!closing) { SetStatus("Low memory: idle background tabs are being frozen."); FreezeDueTabs(); } })); }
                catch (InvalidOperationException) { return; }
                lowMemoryRearm = new System.Threading.Timer(delegate { ArmLowMemory(); }, null, 60000, Timeout.Infinite);
            }, null, Timeout.Infinite, true);
        }

        private void StopWatchingMemoryPressure()
        {
            if (lowMemoryWait != null) lowMemoryWait.Unregister(null);
            if (lowMemoryRearm != null) lowMemoryRearm.Dispose();
            if (lowMemoryEvent != null) lowMemoryEvent.Dispose();
            if (lowMemory != IntPtr.Zero) CloseHandle(lowMemory);
            lowMemoryWait = null; lowMemoryRearm = null; lowMemoryEvent = null; lowMemory = IntPtr.Zero;
        }

        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
