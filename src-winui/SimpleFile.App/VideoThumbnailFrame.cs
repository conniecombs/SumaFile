namespace SimpleFile.App;

internal readonly record struct VideoThumbnailFrame(int Percent)
{
    public const int DefaultPercent = 25;

    public static VideoThumbnailFrame Default => new(DefaultPercent);

    public string CacheToken => Percent.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public TimeSpan PositionFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var clampedPercent = Math.Clamp(Percent, 1, 99);
        var desiredTicks = (long)(duration.Ticks * (clampedPercent / 100d));
        var lastSafeTick = Math.Max(0, duration.Ticks - TimeSpan.FromMilliseconds(250).Ticks);
        return TimeSpan.FromTicks(Math.Clamp(desiredTicks, 0, lastSafeTick));
    }
}
