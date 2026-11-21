using System.Text.Json.Serialization;

namespace Lodestone.App;

internal sealed class PreviewCommands
{
    public PreviewLocation? PreparePreview(int characterId) => characterId == 1
        ? new PreviewLocation(PreviewResources.Root + "scene.json", PreviewResources.Root + "textures/")
        : null;
}

internal sealed record PreviewLocation(
    [property: JsonPropertyName("scene")] string Scene,
    [property: JsonPropertyName("textureRoot")] string TextureRoot);

internal static class PreviewResources
{
    public const string Root = "/_preview/";

    internal static bool IsLocal(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "0.0.0.1" && uri.IsDefaultPort;

    internal static void Respond(object? sender, WebViewWebResourceRequestedEventArgs request)
    {
        var uri = request.Uri;
        if (!IsLocal(uri) || !uri.AbsolutePath.StartsWith(Root, StringComparison.Ordinal))
            return;

        var (name, mimeType) = uri.AbsolutePath switch
        {
            Root + "scene.json" => ("Lodestone.Preview.Scene", "application/json"),
            Root + "textures/coat.png" => ("Lodestone.Preview.Coat", "image/png"),
            _ => (null, null)
        };
        request.Handled = true;
        if (name is null)
        {
            Stream response = new MemoryStream("""{"error":"预览资源不存在"}"""u8.ToArray());
            request.SetResponse(404, "Not Found", "application/json", Task.FromResult<Stream?>(response));
            return;
        }

        var content = typeof(PreviewResources).Assembly.GetManifestResourceStream(name);
        if (content is null)
        {
            Stream response = new MemoryStream("""{"error":"预览资源缺失"}"""u8.ToArray());
            request.SetResponse(500, "Internal Server Error", "application/json", Task.FromResult<Stream?>(response));
            return;
        }
        request.SetResponse(200, "OK", mimeType!, Task.FromResult<Stream?>(content));
    }
}
