using System.Text;
using Android.Content;
using Android.Provider;
using AndroidApplication = Android.App.Application;

namespace Lodestone.App;

internal sealed partial class ClientCommands
{
    private readonly Dictionary<Guid, StringBuilder> portraitTransfers = [];

    public bool AppendPortraitChunk(string id, int offset, string chunk)
    {
        if (!Guid.TryParseExact(id, "D", out var key) || chunk.Length is 0 or > 65536)
            return false;
        lock (gate)
        {
            if (disposed)
                return false;
            if (!portraitTransfers.TryGetValue(key, out var data))
            {
                if (offset != 0 || portraitTransfers.Count >= 4)
                    return false;
                portraitTransfers.Add(key, data = new StringBuilder());
            }
            if (offset != data.Length || data.Length + chunk.Length > PreviewCache.MaxPngDataLength)
                return false;
            data.Append(chunk);
            return true;
        }
    }

    public bool CancelPortraitTransfer(string id)
    {
        lock (gate)
            return Guid.TryParseExact(id, "D", out var key) && portraitTransfers.Remove(key);
    }

    public Task<bool> CompletePortraitTransfer(string id, string requestId, string mode, string encodedFileName)
    {
        StringBuilder? data;
        lock (gate)
        {
            if (!Guid.TryParseExact(id, "D", out var key) || !portraitTransfers.Remove(key, out data))
                return Task.FromResult(false);
        }
        return Task.Run(() => encodedFileName.Length == 0 ? SaveGamePortrait(requestId, mode, data.ToString())
            : ExportPortrait(Uri.UnescapeDataString(encodedFileName), data.ToString()));
    }

    private static bool ExportPortrait(string fileName, string data)
    {
        if (fileName.Length > 180 || !fileName.EndsWith(".png", StringComparison.Ordinal)
            || fileName.IndexOfAny(['/', '\\']) >= 0 || fileName.Any(char.IsControl)
            || PreviewCache.DecodePng(data) is not { } png)
            return false;
        try
        {
            if (AndroidApplication.Context.ContentResolver is not { } resolver)
                return false;

            using var values = new ContentValues();
            values.Put("_display_name", fileName);
            values.Put("mime_type", "image/png");
            values.Put("is_pending", 1);
            using var entry = resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values);
            if (entry is null)
                return false;

            var published = false;
            try
            {
                using (var output = resolver.OpenOutputStream(entry))
                {
                    if (output is null)
                        return false;
                    output.Write(png);
                }
                values.Clear();
                values.Put("is_pending", 0);
                published = resolver.Update(entry, values, null, null) == 1;
                return published;
            }
            finally
            {
                if (!published)
                    resolver.Delete(entry, null, null);
            }
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.RuntimeException or Java.IO.IOException)
        {
            return false;
        }
    }
}
