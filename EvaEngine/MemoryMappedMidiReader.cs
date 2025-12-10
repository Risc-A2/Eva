using System.IO.MemoryMappedFiles;

namespace EvaEngine;

public class MemoryMappedMidiReader
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long fileOffset;
    private readonly long length;
    private long currentPosition;
    public int Pushback = -1;
    public long Location => currentPosition;
    private readonly bool _mmfOwned;

    public MemoryMappedMidiReader(string filePath, long offset, long length)
    {
        _mmfOwned = true;
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open);
        _accessor = _mmf.CreateViewAccessor(offset, length);
        this.fileOffset = offset;
        this.length = length;
        currentPosition = 0;
    }

    public MemoryMappedMidiReader(MemoryMappedFile file, long offset, long length)
    {
        _mmfOwned = false;
        _mmf = file;
        //mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open);
        _accessor = file.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);
        this.fileOffset = offset;
        this.length = length;
        currentPosition = 0;
    }

    public byte Read()
    {
        if (Pushback != -1)
        {
            byte _b = (byte)Pushback;
            Pushback = -1;
            return _b;
        }
        if (currentPosition >= length)
            throw new IndexOutOfRangeException();

        byte value = _accessor.ReadByte(currentPosition);
        currentPosition++;
        return value;
    }

    public byte ReadFast()
    {
        // Misma implementación que Read() - acceso directo a memoria
        return Read();
    }

    public void Skip(int count)
    {
        if (Pushback != -1)
        {
            Pushback = -1;
            count--;
        }

        if (currentPosition + count > length)
            throw new IndexOutOfRangeException();

        currentPosition += count;
    }

    public void Reset()
    {
        Pushback = -1;
        currentPosition = 0;
    }

    public void Dispose()
    {
        _accessor?.Dispose();
        if (_mmfOwned)
            _mmf?.Dispose();
    }
}