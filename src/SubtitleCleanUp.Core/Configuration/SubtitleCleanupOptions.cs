namespace SubtitleCleanUp.Core.Configuration;

public sealed class SubtitleCleanupOptions
{
    public const string SectionName = "SubtitleCleanup";

    public List<MediaRootOptions> Roots { get; set; } = [];
    public string ScanCron { get; set; } = "0 3 * * *";
    public string TimeZone { get; set; } = "UTC";
    public bool ScanOnStartup { get; set; } = true;
    public string QuarantineRoot { get; set; } = "/data/quarantine";
    public int PreviewMaxBytes { get; set; } = 2 * 1024 * 1024;
}

public sealed class MediaRootOptions
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
