using System.Buffers;
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
            stream.ReadExactly(bufferNext, 0, buffersize);
            //stream.Read(bufferNext.Span);
        }
    }

    void update()
    {
        lock (stream)
        {
            stream.Position = pos + streamstart + buffersize;
            stream.ReadExactly(bufferNext, 0, buffersize);
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
    /*
        public void Skip(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if(Pushback != -1)
                {
                    Pushback = -1;
                    continue;
                }
                bufferpos++;
                if (bufferpos < maxbufferpos) continue;
                if (bufferpos >= buffersize)
                {
                    pos += bufferpos;
                    bufferpos = 0;
                    UpdateBuffer(pos);
                }
                else throw new IndexOutOfRangeException();
            }
        }
    */
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
}