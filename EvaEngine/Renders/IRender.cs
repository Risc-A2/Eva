using EvaEngine;

using Veldrid;

namespace EvaEngine.Renders;

public interface IRender
{
    bool Initialized { get; set; }
    void Render(MidiFile f, double midiTime, int deltaTimeOnScreen, RenderSettings settings, CommandList CL);
    void Initialize(MidiFile file, RenderSettings settings, GraphicsDevice GD, ResourceFactory RF, Framebuffer FB);
    void Dispose();
}