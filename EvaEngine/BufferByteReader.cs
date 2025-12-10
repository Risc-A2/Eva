/*using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EvaEngine;

public class BufferByteReader
{
    long pos;
    readonly int buffersize;
    int bufferpos;
    int maxbufferpos;
    readonly long streamstart;
    readonly long streamlen;
    readonly Stream stream;
    byte[] buffer;
    byte[] bufferNext;
    //private Memory<byte> buffer;
    //private Memory<byte> bufferNext;
    Task nextReader = null;

    public BufferByteReader(Stream stream, int buffersize, long streamstart, long streamlen)
    {
        if (buffersize > streamlen) buffersize = (int)streamlen;
        this.buffersize = buffersize;
        this.streamstart = streamstart;
        this.streamlen = streamlen;
        this.stream = stream;
        buffer = new byte[buffersize];
        bufferNext = new byte[buffersize];
        UpdateBuffer(pos, true);
    }
    void firstupdate()
    {
        lock (stream)
        {
            stream.Position = pos + streamstart;
            stream.Read(bufferNext, 0, buffersize);
            //stream.Read(bufferNext.Span);
        }
    }

    void update()
    {
        lock (stream)
        {
            stream.Position = pos + streamstart + buffersize;
            stream.Read(bufferNext, 0, buffersize);
            //stream.Read(bufferNext.Span);
        }
    }
    void UpdateBuffer(long pos, bool first = false)
    {
        if (first)
        {
            nextReader = Task.Run(firstupdate);
        }
        nextReader.ConfigureAwait(false).GetAwaiter().GetResult();
        //bufferNext.CopyTo(buffer);
        Buffer.BlockCopy(bufferNext, 0, buffer, 0, buffersize);
        nextReader = Task.Run(update);
        nextReader.ConfigureAwait(false).GetAwaiter().GetResult();
        //lock (stream)
        //{
        //    stream.Position = pos + streamstart;
        //    stream.Read(buffer, 0, buffersize);
        //}
        maxbufferpos = (int)Math.Min(streamlen - pos + 1, buffersize);
    }

    public long Location => pos;

    public int Pushback = -1;
    public byte Read()
    {
        if (Pushback != -1)
        {
            byte _b = (byte)Pushback;
            Pushback = -1;
            return _b;
        }
        return ReadFast();
    }

    public byte ReadFast()
    {
        byte b = buffer[bufferpos++];
        if (bufferpos < maxbufferpos) return b;
        else if (bufferpos >= buffersize)
        {
            pos += bufferpos;
            bufferpos = 0;
            UpdateBuffer(pos);
            return b;
        }
        else throw new IndexOutOfRangeException();
    }

    public void Reset()
    {
        pos = 0;
        bufferpos = 0;
        UpdateBuffer(pos, true);
    }
using System.Buffers;
using System.Runtime.CompilerServices;

public void Skip(int count)
    {
        if (Pushback != -1)
        {
            Pushback = -1;
            count--;
        }

        while (count > 0)
        {
            int remaining = maxbufferpos - bufferpos;
            if (remaining >= count)
            {
                bufferpos += count;
                return;
            }
            else
            {
                bufferpos += remaining;
                pos += bufferpos;
                bufferpos = 0;
                var oc = count;
                count -= remaining;
                if (oc == count)
                    return;
                UpdateBuffer(pos);
            }
        }
    }

    public void Dispose()
    {
        buffer = null;
        bufferNext = null;
    }
}*/
using System.Buffers;
using System.Runtime.CompilerServices;

namespace EvaEngine;

public sealed class BufferByteReader : IDisposable
{
    private long _position;
    private int _bufferPos;
    private int _maxBufferPos;
    private readonly int _bufferSize;
    private readonly long _streamStart;
    private readonly long _streamLength;
    private readonly Stream _stream;

    // Sistema de double-buffering
    private byte[][] _buffers;
    private int _currentBufferIndex; // 0 o 1
    private int _nextBufferIndex;    // 0 o 1
    private Task? _nextReadTask;

    private int _pushback = -1;
    private bool _disposed;

    public BufferByteReader(Stream stream, int bufferSize, long streamStart, long streamLength)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable", nameof(stream));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if (streamStart < 0) throw new ArgumentOutOfRangeException(nameof(streamStart));
        if (streamLength < 0) throw new ArgumentOutOfRangeException(nameof(streamLength));

        _stream = stream;
        _streamStart = streamStart;
        _streamLength = streamLength;
        _bufferSize = Math.Min(bufferSize, (int)Math.Min(streamLength, int.MaxValue));

        // Inicializar sistema de double-buffering
        _buffers = new byte[2][];
        _buffers[0] = ArrayPool<byte>.Shared.Rent(_bufferSize);
        _buffers[1] = ArrayPool<byte>.Shared.Rent(_bufferSize);
        _currentBufferIndex = 0;
        _nextBufferIndex = 1;

        UpdateBuffer(_position, true);
    }

    // Propiedad para acceder al buffer actual
    private byte[] CurrentBuffer => _buffers[_currentBufferIndex];
    private byte[] NextBuffer => _buffers[_nextBufferIndex];

    public long Location => _position + _bufferPos;

    public byte Read()
    {
        if (_pushback != -1)
        {
            byte result = (byte)_pushback;
            _pushback = -1;
            return result;
        }
        return ReadFast();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadFast()
    {
        if (_bufferPos >= _maxBufferPos)
        {
            AdvanceBuffer();
        }

        return CurrentBuffer[_bufferPos++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceBuffer()
    {
        if (_bufferPos >= _bufferSize)
        {
            _position += _bufferPos;
            _bufferPos = 0;
            UpdateBuffer(_position);
        }

        if (_bufferPos >= _maxBufferPos)
        {
            throw new EndOfStreamException();
        }
    }

    public void Skip(int count)
    {
        if (count <= 0) return;

        if (_pushback != -1)
        {
            _pushback = -1;
            count--;
            if (count == 0) return;
        }

        while (count > 0)
        {
            int remaining = _maxBufferPos - _bufferPos;

            if (remaining > count)
            {
                _bufferPos += count;
                return;
            }

            _bufferPos += remaining;
            _position += _bufferPos;
            count -= remaining;

            if (count > 0)
            {
                _bufferPos = 0;
                UpdateBuffer(_position);
            }
        }
    }

    public void Reset()
    {
        _position = 0;
        _bufferPos = 0;
        _pushback = -1;
        _currentBufferIndex = 0;
        _nextBufferIndex = 1;
        UpdateBuffer(_position, true);
    }

    private void UpdateBuffer(long position, bool first = false)
    {
        // Esperar a que termine la lectura anterior si existe
        if (_nextReadTask != null)
        {
            _nextReadTask.GetAwaiter().GetResult();

            // Intercambiar buffers simplemente cambiando los índices
            // Esto es MUCHO más eficiente que copiar datos
            int temp = _currentBufferIndex;
            _currentBufferIndex = _nextBufferIndex;
            _nextBufferIndex = temp;

            _nextReadTask = null;
        }

        // Calcular bytes disponibles para leer
        long remaining = _streamLength - position;
        _maxBufferPos = (int)Math.Min(Math.Min(remaining, _bufferSize), int.MaxValue);

        if (_maxBufferPos <= 0)
        {
            _maxBufferPos = 0;
            return;
        }

        // Programar la próxima lectura asíncrona en el buffer de respaldo
        _nextReadTask = Task.Run(() => ReadIntoNextBuffer(position));
    }

    private void ReadIntoNextBuffer(long position)
    {
        lock (_stream)
        {
            _stream.Position = position + _streamStart;
            int totalRead = 0;

            while (totalRead < _maxBufferPos)
            {
                int read = _stream.Read(NextBuffer, totalRead, _maxBufferPos - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            // Limpiar el resto del buffer si no se llenó completamente
            if (totalRead < _maxBufferPos)
            {
                Array.Clear(NextBuffer, totalRead, _maxBufferPos - totalRead);
            }
        }
    }

    // Método para lectura de múltiples bytes de forma optimizada
    public int ReadBytes(Span<byte> destination)
    {
        int totalRead = 0;

        while (totalRead < destination.Length)
        {
            int available = _maxBufferPos - _bufferPos;

            if (available <= 0)
            {
                AdvanceBuffer();
                available = _maxBufferPos - _bufferPos;

                if (available <= 0)
                    break;
            }

            int toCopy = Math.Min(available, destination.Length - totalRead);

            // Copia eficiente usando Span
            CurrentBuffer.AsSpan(_bufferPos, toCopy)
                .CopyTo(destination.Slice(totalRead));

            _bufferPos += toCopy;
            totalRead += toCopy;
        }

        return totalRead;
    }

    // Método para inspeccionar sin avanzar (peek)
    public bool TryPeek(out byte value)
    {
        if (_bufferPos < _maxBufferPos)
        {
            value = CurrentBuffer[_bufferPos];
            return true;
        }

        value = 0;
        return false;
    }

    // Método para inspeccionar n bytes adelante
    public bool TryPeek(int offset, out byte value)
    {
        if (_bufferPos + offset < _maxBufferPos)
        {
            value = CurrentBuffer[_bufferPos + offset];
            return true;
        }

        // Si está en el siguiente buffer, intentamos cargarlo
        if (offset < _bufferSize && _position + _bufferPos + offset < _streamLength)
        {
            // Para simplificar, en este caso podríamos cargar el siguiente buffer
            // Pero eso complica la implementación, así que devolvemos false
            value = 0;
            return false;
        }

        value = 0;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Esperar a que termine la tarea de lectura pendiente
        _nextReadTask?.GetAwaiter().GetResult();

        // Devolver buffers al pool
        if (_buffers != null)
        {
            for (int i = 0; i < _buffers.Length; i++)
            {
                if (_buffers[i] != null)
                {
                    ArrayPool<byte>.Shared.Return(_buffers[i]);
                    _buffers[i] = null!;
                }
            }
            _buffers = null!;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    // Versión con Memory<T> para .NET Core/5+ (aún más eficiente)
    /*
    private Memory<byte>[] _buffersMemory;
    
    private void InitializeWithMemory()
    {
        _buffersMemory = new Memory<byte>[2];
        _buffersMemory[0] = new byte[_bufferSize];
        _buffersMemory[1] = new byte[_bufferSize];
    }
    */
}