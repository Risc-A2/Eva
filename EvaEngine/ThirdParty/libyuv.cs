using System.Runtime.InteropServices;

namespace EvaEngine.ThirdParty;

public static class libyuv
{
    private static readonly NativeLibraryLoader.NativeLibrary _lib;
    static libyuv()
    {
        string[] names = new[] { "yuv.dll", "libyuv.dll", "libyuv.so", "libyuv.so.1", "libyuv.so.2", "yuv.so", "yuv.so.1", 
            "yuv.so.2", "yuv.dylib", "libyuv.dylib" };
        _lib = new(names);

        if (_lib.Handle == IntPtr.Zero)
        {
            throw new DllNotFoundException("Cant load YUV.");
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate IntPtr ARGBToNV12Delegate(byte* srcARGB, int srcStrideARGB,
        byte* dstY, int dstStrideY,
        byte* dstUV, int dstStrideUV,
        int width, int height);

    private static ARGBToNV12Delegate? s_argbToNv12 = null!;

    public static ARGBToNV12Delegate ARGBToNV12
    {
        get
        {
            if (s_argbToNv12 == null)
                s_argbToNv12 = _lib.LoadFunction<ARGBToNV12Delegate>("ARGBToNV12");
            return s_argbToNv12;
        }
    }
    
    /*[LibraryImport("yuv"), UnmanagedCallConv(, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int ARGBToNV12(
        byte* srcARGB, int srcStrideARGB,
        byte* dstY, int dstStrideY,
        byte* dstUV, int dstStrideUV,
        int width, int height);*/
}