using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Web.Data;

namespace SubtitleCleanUp.Web.Services;

public sealed class SubtitlePreviewService(
    IDbContextFactory<SubtitleCleanupDbContext> dbFactory,
    ISubtitleFileSystem fileSystem,
    IOptions<SubtitleCleanupOptions> options)
{
    static SubtitlePreviewService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<PreviewContent> ReadAsync(int fileId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var file = await db.SubtitleFiles.AsNoTracking()
            .SingleAsync(x => x.Id == fileId, cancellationToken);
        if (!fileSystem.FileExists(file.FullPath))
        {
            throw new FileNotFoundException("The subtitle is no longer available at its scanned path.", file.FullPath);
        }

        var maximum = Math.Max(1024, options.Value.PreviewMaxBytes);
        var bytes = await fileSystem.ReadBytesAsync(file.FullPath, maximum + 1, cancellationToken);
        var truncated = bytes.Length > maximum;
        if (truncated)
        {
            bytes = bytes[..maximum];
        }

        var (encoding, preambleLength) = DetectEncoding(bytes);
        return new PreviewContent(
            encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength),
            encoding.WebName,
            truncated);
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return (Encoding.UTF8, Encoding.UTF8.Preamble.Length);
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return (Encoding.Unicode, Encoding.Unicode.Preamble.Length);
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return (Encoding.BigEndianUnicode, Encoding.BigEndianUnicode.Preamble.Length);
        }

        try
        {
            var utf8 = new UTF8Encoding(false, true);
            _ = utf8.GetString(bytes);
            return (utf8, 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(1252), 0);
        }
    }
}
