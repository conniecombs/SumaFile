using System.Globalization;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// The lines Settings shows under "Use the graphics card for video thumbnails": how many video thumbnails the
/// graphics card decoded, how many the processor did, and why the processor was last used.
/// </summary>
public static class MediaStatusText
{
    /// <summary>
    /// The first line. <paramref name="status"/> is null when the file service could not be asked; the line
    /// then claims nothing.
    /// </summary>
    public static string Counts(MediaStatus? status, CultureInfo? culture = null)
    {
        if (status is null || (status.VideoHardware == 0 && status.VideoSoftware == 0))
        {
            return "No video thumbnail has been decoded since SumaFile started. "
                + "Thumbnails already in the cache are not decoded again.";
        }

        culture ??= CultureInfo.CurrentCulture;
        return string.Create(
            culture,
            $"Since SumaFile started: {status.VideoHardware:N0} decoded by the graphics card, {status.VideoSoftware:N0} by the processor.");
    }

    /// <summary>
    /// The second line: why the processor last decoded a video. Null when it never has, and for a reason this
    /// version does not know.
    /// </summary>
    public static string? LastProcessorDecode(MediaStatus? status)
    {
        var why = status?.LastSoftwareReason switch
        {
            "turned_off" => "the graphics card is turned off above.",
            "no_device" => "Windows found no graphics card that can decode video.",
            "no_hardware_decoder" => "the graphics card has no decoder for that video's format.",
            "hardware_failed" => "hardware decoding failed for that video.",
            _ => null,
        };
        return why is null ? null : "Last processor decode: " + why;
    }
}
