#define NOTES_FASTLIST
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EvaEngine;

public class MidiTempoChange
{
    public uint Ticks;
    public uint Tempo;
}

public class MidiNote
{
    private byte flags;
    public bool Delete { get => (flags & 1) != 0; set => flags = (byte)((flags & ~1) | (value ? 1 : 0)); }
    public bool hasEnd { get => (flags & 2) != 0; set => flags = (byte)((flags & ~2) | (value ? 2 : 0)); }
    public byte Key = 0;
    public byte Velocity = 0;
    public long StartTime = 0;
    public long EndTime = 0;
    public NoteColor Color;
    //public float Duration = 0; Inecesario uso de RAM
    public byte Channel = 0;

    public void SetDelete(bool val)
    {
        Delete = val;
    }
}
public class ColorChange
{
    public double pos;
    public RgbaFloatEva col1;
    public RgbaFloatEva col2;
    public byte channel;
    public MidiTrack track;
}

public class NoteColor
{
    public RgbaFloatEva left;
    public RgbaFloatEva right;
    public bool isDefault = true;
}

public struct PlaybackEvent
{
    public uint pos;
    public int val;
}


public class MidiTrack(Stream input, long len, long position, MidiFile files) : IDisposable, IAsyncDisposable
{
    private delegate void MidiHandler(byte status);

    private MidiHandler[] _handlers = new MidiHandler[16];
    private MidiHandler[] _Fasthandlers = new MidiHandler[16];
    private bool[] _read2byte = {
        true, // 0
        true, // 1
        true, // 2
        true, // 3
        true, // 4
        true, // 5
        true, // 6
        true, // 7
        true, // 8
        true, // 9
        true, // A
        true, // B
        false, // C
        false, // D
        true, // E
        true // F
    };
    private BufferByteReader _reader;
    public BufferByteReader reader => _reader;
    public byte MaxNote = 0;
    public long stoppoint;
    public byte MinNote = 255;
    uint ReadValue()
    {
        uint value = 0;
        byte byteRead;

        // Unroll the loop for up to 4 bytes (common case for MIDI VLQs)
        for (int i = 0; i < 4; i++)
        {
            byteRead = reader.ReadFast();
            value = (value << 7) | (uint)(byteRead & 0x7F);
            if ((byteRead & 0x80) == 0)
                return value; // Early exit if the last byte is found
        }

        // Handle unexpected cases (e.g., malformed VLQs)
        throw new InvalidOperationException("VLQ exceeds 4 bytes, which is invalid for MIDI.");
    }

    private byte previousStatus;
    private uint wallTime;
    public bool endOfTrack;
    public uint maxwallTime;
    public List<MidiTempoChange> TempoChanges = new();

    private void End()
    {
        if (endOfTrack)
        {
            if (ActiveNotes != null)
            {

                try
                {
                    foreach (var v in ActiveNotes)
                    {
                        if (v == null)
                            continue;
                        foreach (var n in v)
                        {
                            ProcessNoteOff((byte)(0x80 | n.Channel), n.Key, n.Velocity);
                        }
                        v.Clear();
                    }

                    ActiveNotes = null;
                    Dispose();
                }
                catch { }
            }
        }
    }

    public int track;
    public void ParseUpTo(double end)
    {
        End();

        //bool shouldstilldo = false;
        while (wallTime < end)
        {
            if (endOfTrack)
            {
                End();
                return;
            }
            try
            {
                ReadNextEvent();
            }
            catch (IndexOutOfRangeException)
            {
                endOfTrack = true;
                End();
                return;
            }

            //shouldstilldo = ActiveNotes.Length != 0; No necesitado
        }
    }

    public void Step(long time)
    {
        try
        {
            if (time >= wallTime)
            {
                if (readDelta)
                {
                    long d = wallTime;
                    do
                    {
                        ReadNextEvent();
                        if (endOfTrack)
                        {
                            End();
                            return;
                        }
                        wallTime += ReadValue();
                        readDelta = true;
                    }
                    while (wallTime == d);
                }
                else
                {
                    if (endOfTrack)
                    {
                        End();
                        return;
                    }
                    wallTime += ReadValue();
                    readDelta = true;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            endOfTrack = true;
            End();
        }
    }

    private Queue<MidiNote>[] ActiveNotes;
    void ProcessNoteOff(byte status)
    {
        previousStatus = status;
        byte d1 = reader.Read();
        byte d2 = reader.ReadFast();
        byte channel = (byte)(status & 0x0F);
        int param = (d1 << 4) | channel;
        ref var v = ref ActiveNotes[param];
        v ??= new();
        if (v.Count != 0)
        {
            var n = v.Dequeue();
            n.EndTime = wallTime;
            n.hasEnd = true;
#if KDMAPI_ENABLED
            if (KDMAPI.KDMAPI_Supported && n.Velocity > 10)
            {
                files.Events.Add(new()
                {
                    pos = wallTime,
                    val = status | (d1 << 8) | (d2 << 16)
                });
            }
#endif
        }
    }
    void ProcessNoteOff(byte status, byte d1, byte d2)
    {
        previousStatus = status;
        byte channel = (byte)(status & 0x0F);
        int param = (d1 << 4) | channel;
        ref var v = ref ActiveNotes[param];
        v ??= new();
        if (v.Count != 0)
        {
            var n = v.Dequeue();
            n.EndTime = wallTime;
            n.hasEnd = true;
#if KDMAPI_ENABLED
            if (KDMAPI.KDMAPI_Supported && n.Velocity > 10)
            {
                files.Events.Add(new()
                {
                    pos = wallTime,
                    val = status | (d1 << 8) | (d2 << 16)
                });
            }
#endif
        }
    }

    void ProcessNoteOn(byte status)
    {
        previousStatus = status;
        byte d1 = reader.Read();
        byte d2 = reader.ReadFast();
        if (d2 == 0)
        {
            ProcessNoteOff(status, d1, d2);
            return;
        }
        byte channel = (byte)(status & 0x0F);
        int param = (d1 << 4) | channel;
        ref var v = ref ActiveNotes[param];
        v ??= new();
        var n = new MidiNote
        {
            Key = d1,
            Velocity = d2,
            StartTime = wallTime,
            Channel = channel,
            hasEnd = false,
            Color = NoteColors[channel]
        };
        v.Enqueue(n);
        Notes.Add(n);
#if KDMAPI_ENABLED
        if (KDMAPI.KDMAPI_Supported && d2 > 10)
        {
            files.Events.Add(new()
            {
                pos = wallTime,
                val = status | (d1 << 8) | (d2 << 16)
            });
        }
#endif
    }

    private bool readDelta = false;
    // Reads the next MIDI event based on the status byte
    // This method is an Internal Implementation        
    private void ReadNextEvent_impl(byte status)
    {
        byte point = (byte)(status >> 4);
        switch (point)
        {
            case 0x0:
            case 0x1:
            case 0x2:
            case 0x3:
            case 0x4:
            case 0x5:
            case 0x6:
            case 0x7:
                reader.Pushback = status;
                status = previousStatus;
                ReadNextEvent_impl(status); // Worst Case cuz Recursive?
                break;
            case 0x8:
                ProcessNoteOff(status);
                break;
            case 0x9:
                ProcessNoteOn(status);
                break;
            case 0xA:
            case 0xB:
                HandleUselessEvent2(status);
                break;
            case 0xC:
            case 0xD:
                HandleUselessEvent1(status);
                break;
            case 0xE:
            case 0xF:
                HandleUselessEvent2(status);
                break;
            default:
                break;
        }
    }

    public void ReadNextEvent()
    {/*
        if (!readDelta)
        {
            wallTime += ReadValue();
        }*/
        wallTime += ReadValue();
        readDelta = false;
        byte status = reader.ReadFast();
        if (status == 255)
        {
            previousStatus = status;
            var data1 = reader.Read();
            var data2 = ReadValue();
            if (data1 == 47)
            {
                endOfTrack = true;
                End();
            }
            else
                reader.Skip((int)data2);
        }
        else
        {
            ReadNextEvent_impl(status);
            //_handlers[point](status);
        }
    }
    private void ReadNextEventFast_impl(byte status)
    {
        byte point = (byte)(status >> 4);
        switch (point)
        {
            case 0x0:
            case 0x1:
            case 0x2:
            case 0x3:
            case 0x4:
            case 0x5:
            case 0x6:
            case 0x7:
                reader.Pushback = status;
                status = previousStatus;
                ReadNextEventFast_impl(status);
                break;
            case 0x8:
            case 0x9:
            case 0xA:
            case 0xB:
                reader.Skip(2);
                previousStatus = status;
                break;
            case 0xC:
            case 0xD:
                reader.Skip(1);
                previousStatus = status;
                break;
            case 0xE:
            case 0xF:
                reader.Skip(2);
                previousStatus = status;
                break;
            default:
                break;
        }
    }
    public void ReadNextEventFast()
    {
        uint statusTimeDelta = ReadValue();
        wallTime += statusTimeDelta;
        byte status = reader.ReadFast();

        if (status == 255)
        {
            previousStatus = status;
            var data1 = reader.Read();
            var data2 = ReadValue();
            if (data1 == 47)
                endOfTrack = true;
            else if (data1 == 10)
            {
                byte[] data = new byte[data2];
                for (int i = 0; i < data2; i++)
                {
                    data[i] = reader.ReadFast();
                }
                if (data.Length == 8 || data.Length == 12)
                {
                    if (data[0] == 0x00 &&
                        data[1] == 0x0F)
                    {
                        RgbaFloatEva col1 = new(data[4], data[5], data[6], data[7]);
                        RgbaFloatEva col2;
                        if (data.Length == 12)
                            col2 = new(data[8], data[9], data[10], data[11]);
                        else col2 = col1;
                        if (data[2] < 0x10 || data[2] == 0x7F)
                        {
                            var c = new ColorChange() { pos = wallTime, col1 = col1, col2 = col2, channel = data[2], track = this };
                            files.ColorChanges.Add(c);
                        }
                    }
                }
            }
            else if (data1 == 81)
            {
                uint btempo = 0;
                for (int i = 0; i != 3; i++)
                    btempo = (btempo << 8) | reader.ReadFast();
                TempoChanges.Add(new()
                {
                    Tempo = btempo,
                    Ticks = wallTime
                });
            }
            else
                reader.Skip((int)data2);
        }
        else
        {
            ReadNextEventFast_impl(status);
            //byte point = (byte)(status >> 4);
            //_Fasthandlers[point](status);
        }
    }

    public NoteColor[] NoteColors;
    private void HandleUselessEvent1(byte status)
    {
        previousStatus = status;
        byte d1 = reader.Read();
#if KDMAPI_ENABLED
        if (KDMAPI.KDMAPI_Supported)
        {
            files.Events.Add(new()
            {
                pos = wallTime,
                val = status | (d1 << 8)
            });
        }
#endif
    }
    private void HandleUselessEvent2(byte status)
    {
        previousStatus = status;
        byte d1 = reader.Read();
        byte d2 = reader.ReadFast();
#if KDMAPI_ENABLED
        if (KDMAPI.KDMAPI_Supported)
        {
            files.Events.Add(new()
            {
                pos = wallTime,
                val = status | (d1 << 8) | (d2 << 16)
            });
        }
#endif
    }
    void Nothing1(byte status)
    {
        reader.Skip(1);
        previousStatus = status;
    }
    void Nothing2(byte status)
    {
        reader.Skip(2);
        previousStatus = status;
    }
    void FixStatus(byte status)
    {
        reader.Pushback = status;
        status = previousStatus;
        _handlers[(byte)(status >> 4)](status);
    }
    void FastFixStatus(byte status)
    {
        reader.Pushback = status;
        status = previousStatus;
        _Fasthandlers[(byte)(status >> 4)](status);
    }
    public void Read()
    {
        _handlers[0] = FixStatus;
        _handlers[1] = FixStatus;
        _handlers[2] = FixStatus;
        _handlers[3] = FixStatus;
        _handlers[4] = FixStatus;
        _handlers[5] = FixStatus;
        _handlers[6] = FixStatus;
        _handlers[7] = FixStatus;
        _handlers[8] = ProcessNoteOff;
        _handlers[9] = ProcessNoteOn;
        _handlers[10] = HandleUselessEvent2;
        _handlers[11] = HandleUselessEvent2;
        _handlers[12] = HandleUselessEvent1; // 1 Byte
        _handlers[13] = HandleUselessEvent1; // 1 Byte
        _handlers[14] = HandleUselessEvent2;
        _handlers[15] = HandleUselessEvent2;
        _Fasthandlers[0] = FastFixStatus;
        _Fasthandlers[1] = FastFixStatus;
        _Fasthandlers[2] = FastFixStatus;
        _Fasthandlers[3] = FastFixStatus;
        _Fasthandlers[4] = FastFixStatus;
        _Fasthandlers[5] = FastFixStatus;
        _Fasthandlers[6] = FastFixStatus;
        _Fasthandlers[7] = FastFixStatus;
        _Fasthandlers[8] = Nothing2;
        _Fasthandlers[9] = Nothing2;
        _Fasthandlers[10] = Nothing2;
        _Fasthandlers[11] = Nothing2;
        _Fasthandlers[12] = Nothing1;
        _Fasthandlers[13] = Nothing1;
        _Fasthandlers[14] = Nothing2;
        _Fasthandlers[15] = Nothing2;
        ActiveNotes = new Queue<MidiNote>[256 * 16];
        NoteColors = new NoteColor[16];
        Notes = new();
        /*for (int i = 0; i < 256 * 16; i++)
        {
            ActiveNotes[i] =  new();
        }*/
        _reader = new(input, 10000, position, len);
        stoppoint = position + len;
        while (!endOfTrack)
        {
            try
            {
                ReadNextEventFast();
            }
            catch//(Exception ex)
            {
                //Console.WriteLine(ex.ToString());
                break;
            }
        }

        maxwallTime = wallTime;
        wallTime = 0;
        endOfTrack = false;
        _reader.Reset();
        //_reader = new(input, 512000, position + 8, len);
    }

    public void Dispose()
    {
        // TODO release managed resources here
        reader.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        // TODO release managed resources here
        reader.Dispose();
    }
    public List<MidiNote> Notes;
}

public class MidiFile
{
    public MidiFile() { }

    public MidiFile(RenderSettings s, Stream fileName, Image<Rgba32> DefaultPallete)
    {
        ParseFile(s, fileName, DefaultPallete).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public void Clear()
    {
        Tracks = null;
        Tempo = 0;
        BPM = 0;
        PPQ = 0;
    }

    public async Task<bool> ParseFile(RenderSettings s, Stream fs, Image<Rgba32> DefaultPallete)
    {
        if (!fs.CanSeek || !fs.CanRead)
        {
            throw new NotSupportedException("Non-Seekable Streams arent supported BUDDY!!!");
        }
        cfg = s;
        ColorChanges = new();
        //FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Read MIDI Header
        uint fileID = fs.BReadUInt32(Endianness.BigEndian);
        //uint fileID = Swap32(reader.ReadUInt32());
        uint headerLength = fs.BReadUInt32(Endianness.BigEndian);
        ushort format = fs.BReadUInt16(Endianness.BigEndian);
        ushort trackChunks = fs.BReadUInt16(Endianness.BigEndian);
        ushort division = fs.BReadUInt16(Endianness.BigEndian);
        if ((division & 32768) != 0) // Si es negativo, es SMPTE
        {
            sbyte fps = (sbyte)((division >> 8) & 255); // FPS (usualmente -24, -25, -29, -30)
            byte subFrameResolution = (byte)(division & 255); // Resolución de subframes

            PPQ = (uint)(Math.Abs(fps) * subFrameResolution); // Conversión básica
        }
        else
        {
            PPQ = division; // Ya es PPQ
        }

        int tracks = 0;
        List<long> beg = new();
        List<uint> len = new();
        while (fs.Position < fs.Length)
        {
            uint trackID = fs.BReadUInt32(Endianness.BigEndian);
            if (trackID != 1297379947) // "MTrk"
            {
                Console.WriteLine($"Invalid chunk {tracks}, continuing...");
                continue; // Saltar a la siguiente iteración
            }

            uint trackLength = fs.BReadUInt32(Endianness.BigEndian);
            beg.Add(fs.Position);
            len.Add(trackLength);
            fs.Position += trackLength;
            tracks++;
        }

        Tracks = new MidiTrack[tracks];
        var trkcc = Pallete.GetColors(tracks, DefaultPallete);
        Console.WriteLine("Reading tracks...");
        uint maxWallTime = 0; // Tiempo máximo en ticks
        uint tc = 1;
        List<MidiTempoChange> tempos = new();
        /*for (int i = 0; i < Tracks.Length; i++)
        {
            var track = new MidiTrack(fs, len[i], beg[i], this);
            Tracks[i] = track;
            track.Read();
            var colorsForTrack = trkcc[i].Colors; // Colores asignados al track actual
            for (int j = 0; j < 16; j++)
            {
                var c = new NoteColor();
                c.left = colorsForTrack[j * 2];
                c.right = colorsForTrack[j * 2 + 1];
                track.NoteColors[j] = c;
            }
            maxWallTime = Math.Max(track.maxwallTime, maxWallTime);/*
            MaxKey = Math.Max(track.MaxNote, MaxKey);
            MinKey = Math.Min(track.MinNote, MinKey);
            lock (tempos)
            {
                tempos.AddRange(track.TempoChanges);
                Console.WriteLine($"Track: {tc++:N0}/{Tracks.Length:N0}");
            }
        }*/
        Parallel.For(0, Tracks.Length, i =>
        {
            var track = new MidiTrack(fs, len[i], beg[i], this);
            Tracks[i] = track;
            track.Read();
            var colorsForTrack = trkcc[i].Colors; // Colores asignados al track actual
            for (int j = 0; j < 16; j++)
            {
                var c = new NoteColor();
                c.left = colorsForTrack[j * 2];
                c.right = colorsForTrack[j * 2 + 1];
                track.NoteColors[j] = c;
            }
            maxWallTime = Math.Max(track.maxwallTime, maxWallTime);
            lock (tempos)
            {
                tempos.AddRange(track.TempoChanges);
                Console.WriteLine($"Track: {tc++:N0}/{Tracks.Length:N0}");
            }
        });
        TotalTicks = maxWallTime;
        tempos.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
        foreach (var t in tempos)
        {
            TempoChanges.Add(t);
            if (t.Ticks == 0)
                ZerothTempo = t.Tempo;
        }
        double multiplier = ((double)500000 / division) / 1000000;
        long lastt = 0;
        double time = 0;
        long ticks = TotalTicks;
        foreach (var t in TempoChanges)
        {
            var offset = t.Ticks - lastt;
            time += offset * multiplier;
            ticks -= offset;
            lastt = t.Ticks;
            multiplier = ((double)t.Tempo / division) / 1000000;
        }

        time += ticks * multiplier;
        tempoTickMultiplier = (double)division / 500000 * 1000;

        secondsLength = time;

        if (cfg.loadAll)
        {
            ParseUpTo(maxWallTime);
        }

        return true;
    }

    public double secondsLength;
    public void ParseUpTo(double end)
    {
        Task[] ta = new Task[Tracks.Length];
        for (int i = 0; i < Tracks.Length; i++)
        {
            int i1 = i;
            ta[i1] = Task.Run(() =>
            {
                if (Tracks[i1].endOfTrack)
                    return ValueTask.CompletedTask;
                Tracks[i1].ParseUpTo((long)end);
                return ValueTask.CompletedTask;
            });
        }
        Task.WhenAll(ta).ConfigureAwait(false).GetAwaiter().GetResult();
        foreach (var i in Tracks)
        {
            Notes.AddRange(CollectionsMarshal.AsSpan(i.Notes));
            i.Notes.Clear();
        }
        currentSyncTime = (long)end;
        SortInPlace(Notes);
    }
    private void SortInPlace(List<MidiNote> list)
    {
        if (list.Count < 10000)
        {
            list.Sort(static (a, b) => a.StartTime.CompareTo(b.StartTime));
            return;
        }
        long min = long.MaxValue;
        long max = long.MinValue;
        var Span = CollectionsMarshal.AsSpan(list);
        for (int i = 0; i < Span.Length; i++)
        {
            if (Span[i].StartTime < min)
                min = Span[i].StartTime;
            if (Span[i].StartTime > max)
                max = Span[i].StartTime;
        }
        long r = max - min + 1;

        // Array de pigeonholes - tamaño fijo 5700
        var holes = ArrayPool<List<MidiNote>>.Shared.Rent((int)r);

        // Distribuir eventos en los pigeonholes - O(n)
        foreach (var i in list)
        {
            long index = i.StartTime - min;
            if (index >= 0 && index < r)
            {
                ref var v = ref holes[index];
                v ??= new();
                v.Add(i);
            }
        }

        // Reconstruir la lista ordenada - O(n + k)
        list.Clear();
        for (int i = 0; i < r; i++)
        {
            var v = holes[i];
            if (v == null)
                continue;
            if (v.Count > 0)
            {
                list.AddRange(CollectionsMarshal.AsSpan(v));
                v.Clear();
                //v.TrimExcess();
            }
        }
        ArrayPool<List<MidiNote>>.Shared.Return(holes, false);
    }
    public void Update(double start)
    {
#if NOTES_FASTLIST
        try
        {
            // O(N)
            Notes.RemoveAll(n => n.hasEnd && n.EndTime < start);
        }
        catch { }

        /*Parallel.ForEachAsync(Tracks, (MidiTrack t, CancellationToken c) =>
        {
            try{
                var i = t.Notes.Iterate();
                MidiNote n;
                while (i.MoveNext(out n))
                {
                    if (n.hasEnd && n.EndTime < start)
                        i.Remove();
                    if (n.StartTime > start) break;
                }
            } catch{}
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false).GetAwaiter().GetResult();*/
#else
        // TODO: Fix HORRIBLE PERFORMANCE WITH LIST
        for (int i = Notes.Count - 1; i >= 0; i--)
        {
            var n = Notes[i];
            if (n.hasEnd && n.EndTime < start)
                Notes.RemoveAt(i);
        }
#endif
        //GC.Collect();
    }

    //public Memory<MidiNote> Notes;
    //public List<MidiNote> Notes = new();
    public List<MidiNote> Notes = new();
    public FastList<PlaybackEvent> Events = new();
    public FastList<ColorChange> ColorChanges;
    public FastList<MidiTempoChange> TempoChanges = new();
    public MidiTrack[] Tracks;
    public uint Tempo;
    public byte MinKey = 255;
    public byte MaxKey;
    public uint ZerothTempo = 500000;
    public double tempoTickMultiplier = 0;
    public uint BPM;
    public uint TotalTicks;
    public uint PPQ;
    public int notes;
    private double lastParsedEnd;
    public long currentSyncTime;
    private RenderSettings cfg;
}
