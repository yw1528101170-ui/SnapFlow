using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jietu;

internal static class AiVisionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<string> AskVisionAsync(byte[] png, string prompt, AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.AiApiKey))
        {
            return "【提示】尚未配置 AI API Key。\n请在系统托盘右键“设置”中配置自定义 AI 视觉接口 (支持兼容 OpenAI / DeepSeek / Ollama / OneAPI / 硅基流动等格式)。";
        }

        var endpoint = settings.AiApiUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var url = endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : endpoint + "/chat/completions";

        // 非法地址（缺协议头、乱写域名等）此前会在 try 块外抛 UriFormatException，报错文案笼统；
        // 这里前置校验并按本方法的约定返回明确文案。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestUri)
            || (requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps))
        {
            return "AI 接口地址无效：" + settings.AiApiUrl + "\n请在“设置”中填写以 http:// 或 https:// 开头的完整接口地址。";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel.Trim());
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");

            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", string.IsNullOrWhiteSpace(prompt) ? settings.AiPrompt : prompt);
            writer.WriteEndObject();

            writer.WriteStartObject();
            writer.WriteString("type", "image_url");
            writer.WriteStartObject("image_url");
            writer.WriteString("url", "data:image/png;base64," + Convert.ToBase64String(png));
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteNumber("max_tokens", 2000);
            writer.WriteNumber("temperature", 0.5);
            writer.WriteEndObject();
        }

        stream.Position = 0;
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey.Trim());

        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(responseStream, Encoding.UTF8);
            var respText = await reader.ReadToEndAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return $"AI 请求失败 (状态码 {response.StatusCode}):\n{respText}";
            }

            var node = JsonNode.Parse(respText);
            var answer = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(answer) ? "AI 未返回有效内容。" : answer.Trim();
        }
        catch (OperationCanceledException)
        {
            return "AI 请求已被取消。";
        }
        catch (Exception ex)
        {
            return "网络或接口请求异常: " + ex.Message;
        }
    }
}
