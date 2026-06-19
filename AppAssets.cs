namespace AiUsageCounter;

internal static class AppAssets
{
    private const string LogoResourceName = "AiUsageCounter.Assets.app-logo.png";

    public static Bitmap LoadLogoBitmap()
    {
        using var stream = typeof(AppAssets).Assembly.GetManifestResourceStream(LogoResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {LogoResourceName}");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    public static Icon? LoadAppIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }
}
