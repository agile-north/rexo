namespace Rexo.Execution;

using System.Globalization;

internal static class ReportSizeLimits
{
    public const int DefaultMiB = 128;
    public const int MaximumMiB = 512;
    public const long BytesPerMiB = 1024L * 1024;
    public const long DefaultBytes = DefaultMiB * BytesPerMiB;

    public static long ToBytes(int mebibytes) => mebibytes * BytesPerMiB;

    public static string ToMiB(long bytes) =>
        (bytes / BytesPerMiB).ToString(CultureInfo.InvariantCulture);

    public static string Describe(long bytes) => bytes % BytesPerMiB == 0
        ? $"{ToMiB(bytes)} MiB"
        : $"{bytes.ToString(CultureInfo.InvariantCulture)} {(bytes == 1 ? "byte" : "bytes")}";
}

internal sealed class ReportFileTooLargeException(string path, long maximumBytes)
    : IOException($"Report '{path}' exceeds the configured {ReportSizeLimits.Describe(maximumBytes)} input limit.")
{
    public string Path { get; } = path;
}
