using System.Diagnostics;
using System.Runtime;

using EvaEngine;

//var output2 = midiAccess.OpenOutputAsync(midiAccess.Outputs.First().Id).Result;
//GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
var s = new RenderSettings();
s.Width = 1920;
s.Height = 1080;
s.fps = 60;
//s.ffRender = true;
//s.loadAll = true;
//s.blackNotesAbove = true;
s.init = "-vaapi_device /dev/dri/renderD128";
s.codec = "h264_vaapi";
s.filter = "format=nv12,hwupload";
s.extra = "-qp 22";
//s.audioPath = "/home/ikki/midis/BA Rare ASDF Mode rev 1.1.ogg";
//s.audioPath = "/home/ikki/midis/BA Rare ASDF Mode rev 1.1.ogg";
s.audioPath = "/home/ikki/midis/FREEDOM DIE (audio) - 2025-05-17 223445.flac";
s.includeAudio = false;
if (args.Length != 1)
{/*
    Console.WriteLine("Usage: ./EvaEngine <midi>");
    return;*/
    Console.Write("MIDI: ");
    string s2 = Console.ReadLine();
    Player game = new Player(s, s2);
    game.Run();
}
else
{
    Player game = new Player(s, args[0]);
    game.Run();
}