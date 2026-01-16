using System;
using System.Runtime.InteropServices;

namespace RetroBatMarqueeManager.Infrastructure.Processes
{
    internal static class LibMpvNative
    {
        private const string DllName = "libmpv-2.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mpv_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_initialize(IntPtr ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_terminate_destroy(IntPtr ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_command(IntPtr ctx, IntPtr[] args);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_set_option_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mpv_set_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void mpv_free(IntPtr data);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        [StructLayout(LayoutKind.Sequential)]
        internal struct mpv_event
        {
            public int event_id;
            public int error;
            public ulong reply_userdata;
        }

        /// <summary>
        /// Helper to execute mpv_command with string array. Handles UTF-8 marshalling and NULL termination.
        /// </summary>
        internal static int Command(IntPtr ctx, string[] args)
        {
            if (ctx == IntPtr.Zero || args == null) return -1;

            // Allocate array of pointers + 1 for NULL terminator
            IntPtr[] pointers = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                }
                pointers[args.Length] = IntPtr.Zero; // Terminate

                return mpv_command(ctx, pointers);
            }
            finally
            {
                // Free allocated strings
                for (int i = 0; i < args.Length; i++)
                {
                    if (pointers[i] != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(pointers[i]);
                    }
                }
            }
        }
        
        // Helper to get string property safely
        internal static string? GetPropertyString(IntPtr ctx, string name)
        {
             if (ctx == IntPtr.Zero) return null;
             IntPtr ptr = mpv_get_property_string(ctx, name);
             if (ptr == IntPtr.Zero) return null;
             
             try
             {
                 return Marshal.PtrToStringUTF8(ptr);
             }
             finally
             {
                 mpv_free(ptr);
             }
        }
    }
}
