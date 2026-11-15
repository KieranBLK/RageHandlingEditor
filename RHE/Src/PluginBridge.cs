using System;
using System.Runtime.InteropServices;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // One fragment eviction attempt's outcome. The numbers are the plugin's own, read across its exports by value, so a member is never renumbered
        // Canceled is only ever returned by the cancel export, never by a read
        private enum FragmentEvictionOutcome
        {
            Pending = 0,
            Removed = 1,
            NotLoaded = 2,
            NotReady = 3,
            Refused = 4,
            UnknownModel = 5,
            NoSlot = 6,
            Faulted = 7,
            Unavailable = 8,
            Canceled = 9
        }

        // One live SubHandlingData request's outcome. The numbers are the plugin's own, read across its exports by value, so a member is never renumbered
        // Canceled is only ever returned by a cancel export, never by a read
        private enum LiveSubDataOutcome
        {
            Pending = 0,
            Created = 1,
            AlreadyPresent = 2,
            PoolFull = 3,
            Faulted = 4,
            Unavailable = 5,
            Deleted = 6,
            NotPresent = 7,
            Canceled = 8
        }

        // The category numbers the plugin's exports take. The numbers are the plugin's own
        // A member is never renumbered or reordered, since the plugin reads the number by value
        private enum PluginCategory
        {
            Car = 0,
            Bike = 1,
            Flying = 2,
            VerticalFlying = 3,
            SpecialFlight = 4,
            Boat = 5,
            SeaPlane = 6,
            Submarine = 7,
            Trailer = 8,
            Weapon = 9
        }

        // The bundled plugins are found by their already loaded modules and never loaded here, since loading an .asi again would run its attach code a second time
        // Static like RawMemory, since the gear ratio patch state is read from a static initializer
        private static class PluginBridge
        {
            // Bound once every named export resolved, otherwise which of the 2 failures stopped the binding
            public enum BindResult
            {
                Bound,
                ModuleNotLoaded,
                ExportMissing
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr GetModuleHandle(string moduleName);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
            private static extern IntPtr GetProcAddress(IntPtr module, string procName);

            // Binds each named export to the delegate type, in the order given. The functions are only set when every export resolved
            public static BindResult BindExports(string moduleName, string[] exportNames, Type delegateType, out Delegate[] functions)
            {
                functions = null;
                IntPtr module = GetModuleHandle(moduleName);
                if (module == IntPtr.Zero) return BindResult.ModuleNotLoaded;
                var bound = new Delegate[exportNames.Length];
                for (int i = 0; i < exportNames.Length; i++)
                {
                    IntPtr address = GetProcAddress(module, exportNames[i]);
                    if (address == IntPtr.Zero) return BindResult.ExportMissing;
                    bound[i] = Marshal.GetDelegateForFunctionPointer(address, delegateType);
                }
                functions = bound;
                return BindResult.Bound;
            }

            // True when the module is loaded and every named export resolves, without building a delegate
            public static bool AreExportsPresent(string moduleName, string[] exportNames)
            {
                IntPtr module = GetModuleHandle(moduleName);
                if (module == IntPtr.Zero) return false;
                for (int i = 0; i < exportNames.Length; i++)
                {
                    if (GetProcAddress(module, exportNames[i]) == IntPtr.Zero) return false;
                }
                return true;
            }

            // The one wording per failure, which a caller logs as the reason its feature is unavailable
            public static string DescribeFailure(BindResult result, string pluginName, string exportGroup)
            {
                return result == BindResult.ModuleNotLoaded ? pluginName + " plugin not loaded" : pluginName + " plugin has no " + exportGroup + " exports";
            }
        }
    }
}
