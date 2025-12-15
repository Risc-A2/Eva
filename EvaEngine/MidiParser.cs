#define NOTES_FASTLIST
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
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
[StructLayout(LayoutKind.Sequential/*, Pack = 1*/)]
public class MidiNote : IComparable<MidiNote>
{
    private byte flags = 0;
    public bool Delete { get => (flags & 1) != 0; set => flags = (byte)((flags & ~1) | (value ? 1 : 0)); }
    public bool hasEnd { get => (flags & 2) != 0; set => flags = (byte)((flags & ~2) | (value ? 2 : 0)); }
    public byte Key = 0;
    public byte Velocity = 0;
    public long StartTime = 0;
    public long EndTime = 0;
    public int Track = 0;
    //public NoteColor Color;
    //public float Duration = 0; Inecesario uso de RAM
    public byte Channel = 0;


    public int CompareTo(MidiNote? other)
    {
        if (other == null) return 1;
        return StartTime.CompareTo(other.StartTime);
    }
    public static bool operator <(MidiNote left, MidiNote right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(MidiNote left, MidiNote right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(MidiNote left, MidiNote right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(MidiNote left, MidiNote right)
    {
        return left.CompareTo(right) >= 0;
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
public class PlaybackEvent : IComparable<PlaybackEvent>
{
    public uint pos;
    public int val;

    public int CompareTo(PlaybackEvent? other)
    {
        if (other == null) return 1;
        return pos.CompareTo(other.pos);
    }
    public static bool operator <(PlaybackEvent left, PlaybackEvent right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(PlaybackEvent left, PlaybackEvent right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(PlaybackEvent left, PlaybackEvent right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(PlaybackEvent left, PlaybackEvent right)
    {
        return left.CompareTo(right) >= 0;
    }
}


public class MidiTrack(MemoryMappedFile input, long len, long position, MidiFile files) : IDisposable, IAsyncDisposable
{
    public List<PlaybackEvent> Events = new();
    private delegate void MidiHandler(byte status);

    private readonly MidiHandler[] _handlers = new MidiHandler[16];
    private readonly MidiHandler[] _Fasthandlers = new MidiHandler[16];
    private readonly bool[] _read2byte = {
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
    public byte MaxNote = 0;
    public long stoppoint;
    public byte MinNote = 255;
    uint ReadValue()
    {
        uint value = 0;
        byte byteRead;

        // Unroll the loop for up to 4 bytes (common case for MIDI VLQs)
        // Standard is up to 4 bytes for MIDI VLQs
        // but in reality is up to 5 or 10 maybe bytes because it gets us 28 bits only
        // And some MIDIS use more than that for some reason
        // And we're gonna Unroll
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

    private Stack<MidiNote>[] ActiveNotes;
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
            var n = v.Pop();
            n.EndTime = wallTime;
            n.hasEnd = true;
#if KDMAPI_ENABLED
            if (KDMAPI.KDMAPI_Supported && n.Velocity > 10)
            {
                Events.Add(new()
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
            var n = v.Pop();
            n.EndTime = wallTime;
            n.hasEnd = true;
#if KDMAPI_ENABLED
            if (KDMAPI.KDMAPI_Supported && n.Velocity > 10)
            {
                Events.Add(new()
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
            Track = TrkID
            //Color = NoteColors[channel]
        };
        v.Push(n);
        Notes.Add(n);
#if KDMAPI_ENABLED
        if (KDMAPI.KDMAPI_Supported && d2 > 10)
        {
            Events.Add(new()
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
    /*private void ReadNextEvent_impl(byte status)
    {
        byte point = (byte)(status >> 4);
        if (point < 0x8)
        {
            // Si no hay status anterior, esto es un error en el stream
            if (previousStatus == 0)
                throw new InvalidDataException("Running status without previous status");
            reader.Pushback = status;
            status = previousStatus;
            point = (byte)(status >> 4);
        }
        else
        {
            previousStatus = status;
        }
        switch (point)
        {
            /+*case 0x0:
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
                break;*+/
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
                switch (status & 0x0F)
                {
                    case 0x0:
                    case 0x7:
                        reader.Skip((int)ReadValue());
                        break;
                    case 0x1:
                        reader.Skip(1);
                        break;
                    case 0x3:
                        reader.Skip(1);
                        break;
                    case 0x2:
                        reader.Skip(2);
                        break;
                    case 0xF: // System Reset
                              // Leer tipo de meta event
                        byte data1 = reader.Read();
                        uint data2 = ReadValue();
                        //reader.Skip(1);
                        if (data1 == 0x2F)
                        {
                            endOfTrack = true;
                            End();
                            reader.Skip((int)data2);
                        }
                        else
                            reader.Skip((int)data2);
                        break;
                    default:
                        break;
                }
                //HandleUselessEvent2(status);
                break;
            default:
                break;
        }
    }*/
    private void ReadNextEvent_impl(byte status)
    {
        // Resumen: manejar running status, eventos de canal, SysEx y MetaEvents de forma correcta.
        // status viene ya leido por el caller.

        // Handle running status
        byte point = (byte)(status >> 4);
        if (point < 0x8)
        {
            if (previousStatus == 0)
                throw new InvalidDataException("Running status without previous status");

            // pushback the byte we just read so subsequent reads see it as data byte
            reader.Pushback = status;
            status = previousStatus;
            point = (byte)(status >> 4);
        }
        else
        {
            previousStatus = status;
        }

        // Canal events (0x8..0xE)
        if (point >= 0x8 && point <= 0xE)
        {
            switch (point)
            {
                case 0x8: // Note Off: 2 bytes
                    ProcessNoteOff(status);
                    break;
                case 0x9: // Note On: 2 bytes (velocity 0 -> Note Off)
                    ProcessNoteOn(status);
                    break;
                case 0xA: // Polyphonic Key Pressure: 2 bytes
                case 0xB: // Control Change: 2 bytes
                    HandleUselessEvent2(status);
                    break;
                case 0xC: // Program Change: 1 byte
                case 0xD: // Channel Pressure: 1 byte
                    HandleUselessEvent1(status);
                    break;
                case 0xE: // Pitch Bend: 2 bytes
                    HandleUselessEvent2(status);
                    break;
                default:
                    break;
            }
            return;
        }

        // System Common / SysEx / Meta events (0xF)
        if ((status & 0xF0) == 0xF0)
        {
            // Distinguish SysEx (F0, F7) and Meta (FF)
            if (status == 0xFF)
            {
                // Meta Event: next byte = type, then length as VLQ, then payload
                byte metaType = reader.Read(); // data1
                uint length = ReadValue();     // VLQ length
                if (metaType == 0x2F) // End of track
                {
                    // Typically length == 0
                    endOfTrack = true;
                    End();
                    // consume payload (if any)
                    reader.Skip((int)length);
                }
                else if (metaType == 0x51) // Tempo (3 bytes)
                {
                    /*if (length >= 3)
                    {
                        uint btempo = 0;
                        for (int i = 0; i < 3; i++)
                            btempo = (btempo << 8) | reader.ReadFast();
                        TempoChanges.Add(new MidiTempoChange { Tempo = btempo, Ticks = wallTime });
                        // if payload longer than 3, skip remaining
                        if (length > 3)
                            reader.Skip((int)(length - 3));
                    }
                    else
                    {*/
                    // read whatever is there
                    reader.Skip((int)length);
                    //}
                }
                else
                {
                    // Unknown meta - skip its payload
                    reader.Skip((int)length);
                }
            }
            else
            {
                // SysEx or System Common messages
                // F0 and F7 are SysEx start/continue with a VLQ length
                if (status == 0xF0 || status == 0xF7)
                {
                    uint length = ReadValue();
                    reader.Skip((int)length);
                }
                else
                {
                    // System common messages (F1-F6) and real-time (F8-FF but FF handled above)
                    // Handle known lengths:
                    switch (status)
                    {
                        case 0xF1: // MTC Quarter Frame: 1 data byte
                        case 0xF3: // Song Select: 1 data byte
                            reader.Skip(1);
                            break;
                        case 0xF2: // Song Position Pointer: 2 data bytes
                            reader.Skip(2);
                            break;
                        case 0xF6: // Tune Request: 0 data bytes - nothing to do
                            break;
                        case 0xF8: // Timing Clock etc. (real-time messages) - 0 data bytes
                        case 0xFA:
                        case 0xFB:
                        case 0xFC:
                        case 0xFE:
                            // These have no data bytes
                            break;
                        default:
                            // F4, F5 are undefined in many specs; safest: do nothing or attempt to skip 0
                            break;
                    }
                }
            }
            return;
        }

        // If we reach here, it's unexpected — be conservative: no-op
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
        /*if (status == 255)
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
        {*/
        ReadNextEvent_impl(status);
        //_handlers[point](status);
        //}
    }
    private void ReadNextEventFast_impl(byte status)
    {
        /*byte point = (byte)(status >> 4);
        if (point < 0x8)
        {
            // Si no hay status anterior, esto es un error en el stream
            if (previousStatus == 0)
                throw new InvalidDataException("Running status without previous status");
            reader.Pushback = status;
            status = previousStatus;
            point = (byte)(status >> 4);
        }
        else
        {
            previousStatus = status;
        }
        switch (point)
        {
            /+*case 0x0:
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
                break;*+/
            case 0x8:
            case 0x9:
            case 0xA:
            case 0xB:
                reader.Skip(2);
                break;
            case 0xC:
            case 0xD:
                reader.Skip(1);
                break;
            case 0xE:
            case 0xF:
                switch (status & 0x0F)
                {
                    case 0x0:
                    case 0x7:
                        reader.Skip((int)ReadValue());
                        break;
                    case 0x1:
                        reader.Skip(1);
                        break;
                    case 0x3:
                        reader.Skip(1);
                        break;
                    case 0x2:
                        reader.Skip(2);
                        break;
                    case 0xF: // System Reset
                              // Leer tipo de meta event
                        byte data1 = reader.Read();
                        uint data2 = ReadValue();
                        //reader.Skip(1);
                        if (data1 == 0x51)
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
                        else if (data1 == 0x2F)
                        {
                            endOfTrack = true;
                            reader.Skip((int)data2);
                        }
                        else
                        {
                            reader.Skip((int)data2);
                        }

                        // Leer longitud variable (MIDI VLQ)
                        //reader.Skip((int)ReadValue());
                        break;
                    default:
                        break;
                }
                //reader.Skip(2);
                break;
            default:
                break;
        }*/
        // Resumen: manejar running status, eventos de canal, SysEx y MetaEvents de forma correcta.
        // status viene ya leido por el caller.

        // Handle running status
        byte point = (byte)(status >> 4);
        if (point < 0x8)
        {
            if (previousStatus == 0)
                throw new InvalidDataException("Running status without previous status");

            // pushback the byte we just read so subsequent reads see it as data byte
            reader.Pushback = status;
            status = previousStatus;
            point = (byte)(status >> 4);
        }
        else
        {
            previousStatus = status;
        }

        // Canal events (0x8..0xE)
        if (point >= 0x8 && point <= 0xE)
        {
            switch (point)
            {
                case 0x8: // Note Off: 2 bytes
                    reader.Skip(2);
                    //ProcessNoteOff(status);
                    break;
                case 0x9: // Note On: 2 bytes (velocity 0 -> Note Off)
                    reader.Skip(2);
                    //ProcessNoteOn(status);
                    break;
                case 0xA: // Polyphonic Key Pressure: 2 bytes
                case 0xB: // Control Change: 2 bytes
                    reader.Skip(2);
                    //HandleUselessEvent2(status);
                    break;
                case 0xC: // Program Change: 1 byte
                case 0xD: // Channel Pressure: 1 byte
                    reader.Skip(1);
                    //HandleUselessEvent1(status);
                    break;
                case 0xE: // Pitch Bend: 2 bytes
                    reader.Skip(2);
                    //HandleUselessEvent2(status);
                    break;
                default:
                    break;
            }
            return;
        }

        // System Common / SysEx / Meta events (0xF)
        if ((status & 0xF0) == 0xF0)
        {
            // Distinguish SysEx (F0, F7) and Meta (FF)
            if (status == 0xFF)
            {
                // Meta Event: next byte = type, then length as VLQ, then payload
                byte metaType = reader.Read(); // data1
                uint length = ReadValue();     // VLQ length
                if (metaType == 0x2F) // End of track
                {
                    // Typically length == 0
                    endOfTrack = true;
                    //End();
                    // consume payload (if any)
                    reader.Skip((int)length);
                }
                else if (metaType == 0x51) // Tempo (3 bytes)
                {
                    if (length >= 3)
                    {
                        uint btempo = 0;
                        for (int i = 0; i < 3; i++)
                            btempo = (btempo << 8) | reader.ReadFast();
                        TempoChanges.Add(new MidiTempoChange { Tempo = btempo, Ticks = wallTime });
                        // if payload longer than 3, skip remaining
                        if (length > 3)
                            reader.Skip((int)(length - 3));
                    }
                    else
                    {
                        // read whatever is there
                        reader.Skip((int)length);
                    }
                }
                else
                {
                    // Unknown meta - skip its payload
                    reader.Skip((int)length);
                }
            }
            else
            {
                // SysEx or System Common messages
                // F0 and F7 are SysEx start/continue with a VLQ length
                if (status == 0xF0 || status == 0xF7)
                {
                    uint length = ReadValue();
                    reader.Skip((int)length);
                }
                else
                {
                    // System common messages (F1-F6) and real-time (F8-FF but FF handled above)
                    // Handle known lengths:
                    switch (status)
                    {
                        case 0xF1: // MTC Quarter Frame: 1 data byte
                        case 0xF3: // Song Select: 1 data byte
                            reader.Skip(1);
                            break;
                        case 0xF2: // Song Position Pointer: 2 data bytes
                            reader.Skip(2);
                            break;
                        case 0xF6: // Tune Request: 0 data bytes - nothing to do
                            break;
                        case 0xF8: // Timing Clock etc. (real-time messages) - 0 data bytes
                        case 0xFA:
                        case 0xFB:
                        case 0xFC:
                        case 0xFE:
                            // These have no data bytes
                            break;
                        default:
                            // F4, F5 are undefined in many specs; safest: do nothing or attempt to skip 0
                            break;
                    }
                }
            }
            return;
        }
    }
    public void ReadNextEventFast()
    {
        uint statusTimeDelta = ReadValue();
        wallTime += statusTimeDelta;
        byte status = reader.ReadFast();

        /*if (status == 255)
        {
            previousStatus = status;
            var data1 = reader.Read();
            var data2 = ReadValue();
            if (data1 == 0x2F)
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
            else if (data1 == 0x51)
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
        {*/
        ReadNextEventFast_impl(status);
        //byte point = (byte)(status >> 4);
        //_Fasthandlers[point](status);
        //}
    }

    public NoteColor[] NoteColors = new NoteColor[16];
    public int TrkID;
    //public uint[] NoteColorsIndexes = new uint[32];
    private void HandleUselessEvent1(byte status)
    {
        previousStatus = status;
        byte d1 = reader.Read();
#if KDMAPI_ENABLED
        if (KDMAPI.KDMAPI_Supported)
        {
            Events.Add(new()
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
            Events.Add(new()
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
        ActiveNotes = new Stack<MidiNote>[256 * 16];
        Array.Clear(NoteColors);
        //NoteColors = new NoteColor[16];
        Notes = new();
        Events = new();
        /*for (int i = 0; i < 256 * 16; i++)
        {
            ActiveNotes[i] =  new();
        }*/
        _reader = new(input, position, len);
        //MemoryMappedMidiReader
        //_reader = new(input, 10000, position, len);
        //stoppoint = position + len;
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
        previousStatus = 0;

        _reader.Reset();
        //_reader = new(input, 512000, position + 8, len);
    }
    private MemoryMappedMidiReader _reader;
    public MemoryMappedMidiReader reader => _reader;

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
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

    public MidiFile() { }

    public MidiFile(RenderSettings s, MemoryMappedFile fileName, Image<Rgba32> DefaultPallete)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    {
        ParseFile(s, fileName, DefaultPallete).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public void Clear()
    {
        Tracks = null!;
        Tempo = 0;
        BPM = 0;
        PPQ = 0;
    }
    //public Pallete.Color[] Pallete;
    public ReadOnlyMemory<Pallete.Color> Palette;

    public async Task<bool> ParseFile(RenderSettings s, MemoryMappedFile fs, Image<Rgba32> DefaultPallete)
    {
        var se = fs.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
        if (!se.CanSeek || !se.CanRead)
        {
            throw new NotSupportedException("Non-Seekable Streams arent supported BUDDY!!!");
        }

        cfg = s;
        ColorChanges = new();
        //FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Read MIDI Header
        //se.
        uint fileID = se.BReadUInt32(Endianness.BigEndian);
        //uint fileID = Swap32(reader.ReadUInt32());
        uint headerLength = se.BReadUInt32(Endianness.BigEndian);
        ushort format = se.BReadUInt16(Endianness.BigEndian);
        ushort trackChunks = se.BReadUInt16(Endianness.BigEndian);
        ushort division = se.BReadUInt16(Endianness.BigEndian);
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
        while (se.Position < se.Length)
        {
            uint trackID = se.BReadUInt32(Endianness.BigEndian);
            if (trackID != 1297379947) // "MTrk"
            {
                Console.WriteLine($"Invalid chunk {tracks}, continuing...?");
                continue; // Saltar a la siguiente iteración
            }

            uint trackLength = se.BReadUInt32(Endianness.BigEndian);
            beg.Add(se.Position);
            len.Add(trackLength);
            se.Position += trackLength;
            tracks++;
        }

        Tracks = new MidiTrack[tracks];
        var trkcc = Pallete.GetColors(tracks, DefaultPallete);
        Palette = trkcc.AsMemory();
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
        Parallel.For((int)0, Tracks.Length, i =>
        {
            var track = new MidiTrack(fs, len[i], beg[i], this);
            Tracks[i] = track;
            track.TrkID = i;
            track.Read();
            var colorsForTrack = trkcc[i].Colors; // Colores asignados al track actual
            for (int j = 0; j < 16; j++)
            {
                var c = new NoteColor();
                //track.NoteColorsIndexes[j] = MakeTrackChannel((uint)i, (uint)j);
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
        Events = new();
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
        Parallel.ForAsync(0, Tracks.Length, (i, sa) =>
        {
            if (!Tracks[i].endOfTrack)
                Tracks[i].ParseUpTo((long)end);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false).GetAwaiter().GetResult();
        /*Task[] ta = new Task[Tracks.Length];
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
        Task.WhenAll(ta).ConfigureAwait(false).GetAwaiter().GetResult();*/
        var TN = Task.Run(() =>
        {
            SemaphoreN.Wait();
            int totalNotes = 0;
            foreach (var t in Tracks)
                totalNotes += t.Notes.Count;
            Notes.EnsureCapacity(Notes.Count + totalNotes);
            foreach (var i in Tracks)
            {
                Notes.AddRange(CollectionsMarshal.AsSpan(i.Notes));
                i.Notes.Clear();
            }
            Redzen.Sorting.TimSort.Sort(CollectionsMarshal.AsSpan(Notes));
            SemaphoreN.Release();
        });
        /*SemaphoreN.Wait();
        foreach (var i in Tracks)
        {
            Notes.AddRange(CollectionsMarshal.AsSpan(i.Notes));
            i.Notes.Clear();
        }
        Redzen.Sorting.TimSort.Sort(CollectionsMarshal.AsSpan(Notes));
        SemaphoreN.Release();*/
        var TE = Task.Run(() =>
        {
            //SemaphoreE.Wait();
            //PriorityQueue<PlaybackEvent, uint> tempQueue = new();
            int totalCount = 0;
            foreach (var tr in Tracks)
                totalCount += tr.Events.Count;
            List<PlaybackEvent> tempEvents = new(totalCount);
            foreach (var tr in Tracks)
            {
                tempEvents.AddRange(tr.Events);
                tr.Events.Clear();
            }
            tempEvents.Sort(static (a, b) => a.pos.CompareTo(b.pos));
            //Redzen.Sorting.TimSort.Sort(CollectionsMarshal.AsSpan(tempEvents));
            SemaphoreE.Wait();
            foreach (var t in tempEvents)
            {
                Events.Enqueue(t);
            }
            SemaphoreE.Release();
            tempEvents.Clear();
            //tempEvents.TrimExcess();
            //Events.SortTim();
            //SemaphoreE.Release();
        });
        /*SemaphoreE.Wait();
        foreach (var i in Tracks)
        {
            Events.EnqueueRange(i.Events);
            i.Events.Clear();
        }
        Events.SortTim();
        SemaphoreE.Release();*/
        Task.WaitAll(TN, TE);
        currentSyncTime = (long)end;
        //Notes.Sort(static (a, b) => a.StartTime.CompareTo(b.StartTime));
        //SortInPlace(Notes);
        //Redzen.Sorting.TimSort.Sort(CollectionsMarshal.AsSpan(Events.));
        //Events.Sort(static (a, b) => a.pos.CompareTo(b.pos));
    }
    public SemaphoreSlim SemaphoreN = new SemaphoreSlim(1, 1);
    public SemaphoreSlim SemaphoreE = new SemaphoreSlim(1, 1);
    private void SortInPlace(List<MidiNote> list)
    {
        if (list.Count < 10000)
        {
            var span = CollectionsMarshal.AsSpan(list);
            ExtSorts.QuickSortLR(span, 0, span.Length - 1, static (a, b) => a.StartTime.CompareTo(b.StartTime));
            //list.Sort(static (a, b) => a.StartTime.CompareTo(b.StartTime));
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
        //return;
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
    //public FastCircularQueue<PlaybackEvent> Events = new();
    //public ConcurrentPriorityQueue<uint, PlaybackEvent> Events = new(PriorityType.Min);
    public ConcurrentQueue<PlaybackEvent> Events = new();
    //public List<PlaybackEvent> Events = new();
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
    private readonly double lastParsedEnd;
    public long currentSyncTime;
    private RenderSettings cfg;
}