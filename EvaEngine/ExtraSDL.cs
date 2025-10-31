using System.Runtime.InteropServices;

using NativeLibraryLoader;

using Veldrid.Sdl2;

namespace EvaEngine;

/// <summary>
/// A public class that exposes some Exported Methods of the SDL2 library that <see cref="Sdl2Native"/> does not expose
/// </summary>
public static class ExtraSDL
{
    /// <summary>
    /// A Readonly field that gets you the NativeLibrary Class from NativeLibraryLoader
    /// </summary>
    public static NativeLibraryLoader.NativeLibrary LibraryHandle
    {
        get { return _lib; }
    }

    private static readonly NativeLibraryLoader.NativeLibrary _lib;

    static ExtraSDL()
    {
        string[] names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "SDL2.dll" }
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? new[] { "libSDL2-2.0.so", "libSDL2-2.0.so.0", "libSDL2-2.0.so.1", "libSDL2.so", "libSDL2.so.0", "libSDL2.so.1" }
                : new[] { "libsdl2.dylib" };
        _lib = new(names);

        if (_lib.Handle == IntPtr.Zero)
        {
            throw new DllNotFoundException("Cant load SDL2.");
        }

        // Cargar las funciones como delegates
        SDL_RWFromFile = GetDelegate<SDL_RWFromFileDelegate>("SDL_RWFromFile");
        SDL_LoadBMP_RW = GetDelegate<SDL_LoadBMP_RWDelegate>("SDL_LoadBMP_RW");
        SDL_FreeSurface = GetDelegate<SDL_FreeSurfaceDelegate>("SDL_FreeSurface");
        SDL_SetWindowIcon = GetDelegate<SDL_SetWindowIconDelegate>("SDL_SetWindowIcon");
        SDL_GetError = GetDelegate<SDL_GetErrorDelegate>("SDL_GetError");
        SDL_RWFromMem = GetDelegate<SDL_RWFromMemDelegate>("SDL_RWFromMem");
        SDL_ShowSimpleMessageBox = GetDelegate<SDL_ShowSimpleMessageBoxDelegate>("SDL_ShowSimpleMessageBox");
    }
    public static T GetDelegate<T>(string name) where T : Delegate
    {
        var val = _lib.LoadFunction(name);
        if (val == IntPtr.Zero)
            throw new EntryPointNotFoundException(
                $"Failed to get Pointer for '{name}' is SDL2 really on the executable/installed?");
        var Dval = Marshal.GetDelegateForFunctionPointer<T>(val);
        return Dval;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr SDL_RWFromMemDelegate(IntPtr mem, int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr SDL_GetErrorDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr SDL_LoadBMP_RWDelegate(IntPtr src, int freesrc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr SDL_RWFromFileDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string file,
        [MarshalAs(UnmanagedType.LPStr)] string mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SDL_FreeSurfaceDelegate(IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SDL_SetWindowIconDelegate(IntPtr window, IntPtr icon);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SDL_ShowSimpleMessageBoxDelegate(
        uint flags,
        [MarshalAs(UnmanagedType.LPStr)] string title,
        [MarshalAs(UnmanagedType.LPStr)] string message,
        IntPtr window);

    public static SDL_LoadBMP_RWDelegate SDL_LoadBMP_RW;
    public static SDL_RWFromFileDelegate SDL_RWFromFile;
    public static SDL_FreeSurfaceDelegate SDL_FreeSurface;
    public static SDL_SetWindowIconDelegate SDL_SetWindowIcon;
    public static SDL_GetErrorDelegate SDL_GetError;
    public static SDL_RWFromMemDelegate SDL_RWFromMem;
    public static SDL_ShowSimpleMessageBoxDelegate SDL_ShowSimpleMessageBox;
    /// <summary>
    /// Little wrapper around SDL_GetError
    /// </summary>
    /// <returns>The error code</returns>
    public static string SdlGetError()
    {
        IntPtr errPtr = SDL_GetError();
        return Marshal.PtrToStringAnsi(errPtr)!;
    }
}