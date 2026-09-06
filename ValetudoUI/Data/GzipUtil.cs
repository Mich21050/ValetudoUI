using System.IO.Compression;
using System.Text;

namespace ValetudoUI.Data;

public static class GzipUtil
{
    public static byte[] Compress(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return Compress(Encoding.UTF8.GetBytes(text));
    }

    public static byte[] Compress(ReadOnlySpan<byte> bytes)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(bytes);
        }
        return ms.ToArray();
    }

    public static byte[] Decompress(byte[] gzip)
    {
        if (gzip.Length == 0) return [];
        using var input = new MemoryStream(gzip);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    public static string DecompressToString(byte[] gzip) => Encoding.UTF8.GetString(Decompress(gzip));
}
