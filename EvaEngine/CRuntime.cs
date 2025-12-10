using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace std;

public unsafe partial class C
{
    public unsafe struct C_MemRegion
    {
        public C_MemRegion(void* ptr, nint s, nint ts, nint alig, bool align)
        {
            if (ptr == null)
                Environment.FailFast("Tried creating an C_MemRegion but the Pointer is null CRuntime L: 13");
            safepointer = (nint)ptr;
            pointer = ptr;
            size = s;
            typesize = ts;
            alignment = alig;
            aligned = align;
        }
        public nint safepointer;
        public void* pointer;
        public nint size;
        public nint typesize;
        public nint alignment;
        public bool aligned;
    }
    private static C_MemRegion CreateMem(void* ptr, nint s, nint ts, nint alig, bool align)
    {
        var c = new C_MemRegion(ptr, s, ts, alig, align);
        return c;
    }
    public static void memset(void* ptr, byte val, nint size)
    {
        byte* p = (byte*)ptr;

        // Construir el patrón repetido del byte en un nuint
        nuint pattern = 0;
        for (int j = 0; j < sizeof(nuint); j++)
            pattern |= (nuint)val << (j * 8);

        // 1) Alinear hasta el tamaño de nuint
        nint i = 0;
        for (; i < size && ((nuint)p & (nuint)(sizeof(nuint) - 1)) != 0; i++)
            p[i] = val;

        // 2) Escribir por bloques de nuint
        nuint* n = (nuint*)(p + i);
        for (; i + sizeof(nuint) <= size; i += sizeof(nuint))
            *n++ = pattern;

        // 3) Rellenar últimos bytes
        byte* tail = (byte*)n;
        for (; i < size; i++)
            tail[i - size + i] = val;
    }
    public static void memset(C_MemRegion ptr, byte val, nint size)
    {
        memset(ptr.pointer, val, size);
    }
    public static void memcpy(void* a, void* b, nint startSrc, nint startDst, nint size)
    {
        var ap = (byte*)a + startSrc;
        var bp = (byte*)b + startDst;
        var ap2 = (nint*)a + startSrc;
        var bp2 = (nint*)b + startDst;
        nint i = 0;
        for (; i < size; i += nint.Size)
        {
            nint s1 = ap2[i];
            bp2[i] = s1;
        }
        for (; i < size; i++)
        {
            ap[i] = bp[i];
        }
    }

    public static void memcpy(C_MemRegion a, C_MemRegion b, nint startSrc, nint startDst, nint size)
    {
        memcpy(a.pointer, b.pointer, startSrc, startDst, size);
    }
    public static void memcpy(void* a, void* b, long size)
    {
        var ap = (byte*)a;
        var bp = (byte*)b;
        for (long i = 0; i < size; ++i)
            *ap++ = *bp++;
    }

    public static void memcpy(void* a, void* b, ulong size)
    {
        memcpy(a, b, (long)size);
    }
    public static void memcpy(C_MemRegion a, C_MemRegion b, long size)
    {
        memcpy(a.pointer, b.pointer, size);
    }

    public static void memcpy(C_MemRegion a, C_MemRegion b, ulong size)
    {
        memcpy(a.pointer, b.pointer, size);
    }
    public static C_MemRegion malloc(nuint size)
    {
        void* ptr = NativeMemory.Alloc(size);

        return CreateMem(ptr, (nint)size, -1, -1, false);
    }
    public static C_MemRegion malloc<T>(nuint size)
    {
        void* ptr = NativeMemory.Alloc(size * (nuint)Unsafe.SizeOf<T>());

        return CreateMem(ptr, (nint)size, (nint)Unsafe.SizeOf<T>(), -1, false);
    }
    public static C_MemRegion realloc(C_MemRegion ptr, nuint size)
    {
        if (ptr.typesize != -1)
            Environment.FailFast("Tried reallocating memory<T>, with realloc instead of realloc<T>");
        if (ptr.aligned == true || ptr.alignment != -1)
            Environment.FailFast("Tried reallocating aligned memory, with realloc instead of aligned_realloc CRuntime");
        void* nptr = NativeMemory.Realloc(ptr.pointer, size);

        return CreateMem(nptr, (nint)size, -1, -1, false);
    }
    public static C_MemRegion realloc<T>(C_MemRegion ptr, nuint size)
    {
        if (ptr.typesize == -1)
            Environment.FailFast("Tried reallocating memory, with realloc<T> instead of realloc CRuntime");
        if (ptr.aligned == true || ptr.alignment != -1)
            Environment.FailFast("Tried reallocating aligned memory, with realloc instead of aligned_realloc CRuntime");
        void* nptr = NativeMemory.Realloc(ptr.pointer, size * (nuint)Unsafe.SizeOf<T>());

        return CreateMem(nptr, (nint)size, (nint)Unsafe.SizeOf<T>(), -1, false);
    }
    public static C_MemRegion aligned_realloc(C_MemRegion ptr, nuint size, nuint alignment)
    {
        if (!nuint.IsPow2(alignment))
            Environment.FailFast("Tried reallocating aligned memory, but the alignment is not a power of 2 CRuntime");
        if (ptr.typesize != -1)
            Environment.FailFast("Tried reallocating aligned memory<T>, with aligned_realloc instead of aligned_realloc<T>");
        if (ptr.aligned == false || ptr.alignment == -1)
            Environment.FailFast("Tried reallocating non-aligned memory, with aligned_realloc instead of realloc CRuntime");
        void* nptr = NativeMemory.AlignedRealloc(ptr.pointer, size, alignment);

        return CreateMem(nptr, (nint)size, -1, (nint)alignment, true);
    }
    public static C_MemRegion aligned_realloc<T>(C_MemRegion ptr, nuint size, nuint alignment)
    {
        if (!nuint.IsPow2(alignment))
            Environment.FailFast("Tried reallocating aligned memory, but the alignment is not a power of 2 CRuntime");
        if (ptr.typesize != -1)
            Environment.FailFast("Tried reallocating aligned memory, with aligned_realloc<T> instead of aligned_realloc CRuntime");
        if (ptr.aligned == false || ptr.alignment == -1)
            Environment.FailFast("Tried reallocating non-aligned memory, with aligned_realloc instead of realloc CRuntime");
        void* nptr = NativeMemory.AlignedRealloc(ptr.pointer, size * (nuint)Unsafe.SizeOf<T>(), alignment);

        return CreateMem(nptr, (nint)size, (nint)Unsafe.SizeOf<T>(), (nint)alignment, true);
    }
    public static C_MemRegion aligned_alloc(nuint alignment, nuint size)
    {
        if (!nuint.IsPow2(alignment))
            Environment.FailFast("Tried allocating aligned memory, but the alignment is not a power of 2 CRuntime");
        void* ptr = NativeMemory.AlignedAlloc(size, alignment);

        return CreateMem(ptr, (nint)size, -1, (nint)alignment, true);
    }
    public static C_MemRegion aligned_alloc<T>(nuint alignment, nuint size)
    {
        if (!nuint.IsPow2(alignment))
            Environment.FailFast("Tried allocating aligned memory, but the alignment is not a power of 2 CRuntime");
        void* ptr = NativeMemory.AlignedAlloc(size * (nuint)Unsafe.SizeOf<T>(), alignment);

        return CreateMem(ptr, (nint)size, (nint)Unsafe.SizeOf<T>(), (nint)alignment, true);
    }
    public static void free(C_MemRegion ptr)
    {
        if (ptr.aligned)
            NativeMemory.AlignedFree(ptr.pointer);
        else
            NativeMemory.Free(ptr.pointer);
    }
}