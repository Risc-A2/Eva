using System.Runtime.InteropServices;

namespace EvaEngine;

public static partial class KDMAPI
{
    [LibraryImport("OmniMIDI")]
    public static partial int ReturnKDMAPIVer(out Int32 Major, out Int32 Minor, out Int32 Build, out Int32 Revision);

    [LibraryImport("OmniMIDI")]
    public static partial int IsKDMAPIAvailable();

    [LibraryImport("OmniMIDI")]
    public static partial int InitializeKDMAPIStream();

    [LibraryImport("OmniMIDI")]
    public static partial int TerminateKDMAPIStream();

    [LibraryImport("OmniMIDI")]
    public static partial void ResetKDMAPIStream();

    [LibraryImport("OmniMIDI")]
    public static partial int SendCustomEvent(uint eventtype, uint chan, uint param);

    [LibraryImport("OmniMIDI")]
    public static partial void SendDirectData(uint dwMsg);

    [LibraryImport("OmniMIDI")]
    public static partial void SendDirectDataNoBuf(uint dwMsg);

    static KDMAPI()
    {
        KDMAPI_Supported = SafeIsKDMAPIAvailable();
    }

    public static bool KDMAPI_Supported;

    private static bool SafeIsKDMAPIAvailable()
    {
        try
        {
            var val = IsKDMAPIAvailable();
            return val != 0;
        }
        catch
        {
            return false;
        }
    }
}