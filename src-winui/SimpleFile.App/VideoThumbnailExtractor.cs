using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleFile.Core;
using Windows.Media.Editing;
using Windows.Storage;

namespace SimpleFile.App;

internal static class VideoThumbnailExtractor
{
    public static bool CanUseVideoThumbnail(string? pathOrExtension) =>
        MediaFolder.IsVideo(pathOrExtension);

    public static async Task<ImageSource?> LoadAsync(string path, int requestSize, VideoThumbnailFrame frame)
    {
        if (!CanUseVideoThumbnail(path) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var scaledSize = Math.Clamp(requestSize, 64, UiSettings.IconSizeMax * 2);
            using var thumbnail = await composition.GetThumbnailAsync(
                frame.PositionFor(composition.Duration),
                scaledSize,
                scaledSize,
                VideoFramePrecision.NearestFrame);
            if (thumbnail.Size == 0)
            {
                return null;
            }

            thumbnail.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
