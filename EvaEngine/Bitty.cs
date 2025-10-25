using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices.Marshalling;

namespace EvaEngine;

public enum Endianness
{
    BigEndian,
    LittleEndian
}
public static class Bitty
{
    private static byte[] shortBuffer = new byte[2];
    private static byte[] intBuffer = new byte[4];
    private static byte[] longBuffer = new byte[8];
    private static byte[] ReadFromStream(Stream stream, byte[] buffer)
    {
        stream.ReadExactly(buffer, 0, buffer.Length);
        return buffer;
    }

    public static void Skip(this Stream stream, int l)
    {
        stream.Position += l;
    }
    public static void ReverseSkip(this Stream stream, int l)
    {
        stream.Position -= l;
    }
    public static ulong BReadUInt64(this Stream stream, Endianness endianness)
    {
        return ToUInt64(ReadFromStream(stream, longBuffer), endianness);
    }
    public static long BReadInt64(this Stream stream, Endianness endianness)
    {
        return ToInt64(ReadFromStream(stream, longBuffer), endianness);
    }
    public static uint BReadUInt32(this Stream stream, Endianness endianness)
    {
        return ToUInt32(ReadFromStream(stream, intBuffer), endianness);
    }
    public static int BReadInt32(this Stream stream, Endianness endianness)
    {
        return ToInt32(ReadFromStream(stream, intBuffer), endianness);
    }
    public static ushort BReadUInt16(this Stream stream, Endianness endianness)
    {
        return ToUInt16(ReadFromStream(stream, shortBuffer), endianness);
    }
    public static short BReadInt16(this Stream stream, Endianness endianness)
    {
        return ToInt16(ReadFromStream(stream, shortBuffer), endianness);
    }
    public static ulong ToUInt64(byte[] bytes, Endianness endianness)
    {
        return (ulong)ToInt64(bytes, endianness);
    }
    public static long ToInt64(byte[] bytes, Endianness endianness)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length < 8)
            throw new ArgumentException("Byte array must be at least 8 bytes long", nameof(bytes));

        // Si el endianness solicitado no coincide con el del sistema, invertimos los bytes
        bool needsReverse = (endianness == Endianness.BigEndian) != !BitConverter.IsLittleEndian;
        
        
        if (needsReverse)
        {
            Array.Reverse(bytes);
        }
        
        return BitConverter.ToInt64(bytes, 0);
    }
    public static uint ToUInt32(byte[] bytes, Endianness endianness)
    {
        return (uint)ToInt32(bytes, endianness);
    }
    public static int ToInt32(byte[] bytes, Endianness endianness)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length < 4)
            throw new ArgumentException("Byte array must be at least 4 bytes long", nameof(bytes));

        // Si el endianness solicitado no coincide con el del sistema, invertimos los bytes
        bool needsReverse = (endianness == Endianness.BigEndian) != !BitConverter.IsLittleEndian;
        
        if (needsReverse)
        {
            Array.Reverse(bytes);
        }
        
        return BitConverter.ToInt32(bytes, 0);
    }
    public static ushort ToUInt16(byte[] bytes, Endianness endianness)
    {
        return (ushort)ToInt16(bytes, endianness);
    }
    public static short ToInt16(byte[] bytes, Endianness endianness)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length < 2)
            throw new ArgumentException("Byte array must be at least 2 bytes long", nameof(bytes));

        // Si el endianness solicitado no coincide con el del sistema, invertimos los bytes
        bool needsReverse = (endianness == Endianness.BigEndian) != !BitConverter.IsLittleEndian;
        
        
        if (needsReverse)
        {
            Array.Reverse(bytes);
        }
        
        return BitConverter.ToInt16(bytes, 0);
    }
}