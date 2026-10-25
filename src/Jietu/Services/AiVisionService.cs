using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jietu;

/// <summary>一次视觉请求的可调参数：转表格/提取类任务需要 temperature 0 和更高的输出上限，省流模式需要 detail=low。</summary>
internal sealed record AiAskOptions(double? Temperature = null, int? MaxTokens = null, string? Detail = null);

/// <summary>多轮对话中的一条消息。图片只应出现在首轮 user 消息里：多带一次图，计费与上下文就多耗一份。</summary>
internal sealed record VisionTurn(string Role, string? Text, byte[]? ImagePng);

internal static class AiVisionService
{
    private const int DefaultMaxTokens = 3000;
    private const double DefaultTemperature = 0.5;

    // 流式响应在首包到达后还要继续读体，HttpClient 的总超时会拦腰截断长回答，
    // 因此关掉它，改用 linked CTS 分段控制：等首包 45s、开始生成后整体 300s，手动取消随时生效。
    private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(300);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static Task<string> AskVisionAsync(byte[] png, string prompt, AppSettings settings, CancellationToken cancellationToken = default)
        => AskVisionAsync([new VisionTurn("user", prompt, png)], settings, null, cancellationToken, null);

    public static async Task<string> AskVisionAsync(
        IReadOnlyList<VisionTurn> turns,
        AppSettings settings,
        AiAskOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<string>? delta = null)
    {
        if (string.IsNullOrWhiteSpace(settings.AiApiKey))
        {
            return AiVisionContract.HintPrefix + "尚未配置 AI API Key。\n请在系统托盘右键“设置”中配置自定义 AI 视觉接口 (支持兼容 OpenAI / DeepSeek / Ollama / OneAPI / 硅基流动等格式)。";
        }

        // 与设置页“测试连接”共用同一套端点校验（绝对地址 + http 仅放行回环），规则只此一份。
        if (!TryResolveApiBase(settings.AiApiUrl, out var baseUri, out var urlError))
        {
            return AiVisionContract.HintPrefix + urlError;
        }

        var chatUrl = baseUri.AbsoluteUri.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUri.AbsoluteUri
            : baseUri.AbsoluteUri + "/chat/completions";

        // 大图先等比压到安全边长：4K 截图直发会产生数 MB 的 base64 请求体，
        // 视觉 token 按分辨率计费，部分网关还会直接拒收超大请求。
        var prepared = new VisionTurn[turns.Count];
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            prepared[i] = turn.ImagePng is { Length: > 0 } png
                ? turn with { ImagePng = await ImageBytes.ShrinkPngForVisionAsync(png) }
                : turn;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", string.IsNullOrWhiteSpace(settings.AiModel) ? AppSettings.DefaultAiModel : settings.AiModel.Trim());
            writer.WriteStartArray("messages");
            foreach (var turn in prepared)
            {
                writer.WriteStartObject();
                writer.WriteString("role", turn.Role);
                if (turn.Role == "user" && turn.ImagePng is { Length: > 0 } image)
                {
                    writer.WriteStartArray("content");
                    if (!string.IsNullOrEmpty(turn.Text))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "text");
                        writer.WriteString("text", turn.Text);
                        writer.WriteEndObject();
                    }

                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    WriteDataUrl(writer, image);
                    if (!string.IsNullOrEmpty(options?.Detail))
                    {
                        writer.WriteString("detail", options.Detail);
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString("content", turn.Text ?? "");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("max_tokens", options?.MaxTokens ?? DefaultMaxTokens);
            writer.WriteNumber("temperature", options?.Temperature ?? DefaultTemperature);
            writer.WriteBoolean("stream", true);
            writer.WriteEndObject();
        }

        stream.Position = 0;
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, chatUrl)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey.Trim());

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(HeadersTimeout);
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(linked.Token);
                if ((int)response.StatusCode == 429)
                {
                    return $"{AiVisionContract.RequestFailedPrefix} (状态码 429 已被限流):\n{Clip(body)}\n请稍后再试，或在服务商控制台检查配额。";
                }

                return $"{AiVisionContract.RequestFailedPrefix} (状态码 {response.StatusCode}):\n{Clip(body)}";
            }

            using var responseStream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(responseStream, Encoding.UTF8);
            var answer = new StringBuilder();
            string? finishReason = null;
            var sawSse = false;
            var gotContent = false;
            while (true)
            {
                var line = await reader.ReadLineAsync(linked.Token);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    sawSse = true;
                    var payload = line["data:".Length..].TrimStart();
                    if (payload == "[DONE]")
                    {
                        break;
                    }

                    if (payload.Length == 0)
                    {
                        continue;
                    }

                    var chunk = JsonNode.Parse(payload);
                    var choice = chunk?["choices"]?[0];
                    var piece = choice?["delta"]?["content"]?.GetValue<string>();
                    var chunkFinish = choice?["finish_reason"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(chunkFinish))
                    {
                        finishReason = chunkFinish;
                    }

                    if (!string.IsNullOrEmpty(piece))
                    {
                        if (!gotContent)
                        {
                            // 首个内容片段到达：从“等首包”切换到“整体生成”时限。
                            linked.CancelAfter(TotalTimeout);
                            gotContent = true;
                        }

                        answer.Append(piece);
                        delta?.Report(piece);
                    }
                }
                else if (!sawSse && line[0] == '{')
                {
                    // 网关忽略了 stream:true，直接回了完整 JSON：整包读出按非流式解析，不当作错误。
                    sawSse = true;
                    var whole = line + "\n" + await reader.ReadToEndAsync(linked.Token);
                    var node = JsonNode.Parse(whole);
                    var full = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                    var fullFinish = node?["choices"]?[0]?["finish_reason"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(fullFinish))
                    {
                        finishReason = fullFinish;
                    }

                    if (!string.IsNullOrEmpty(full))
                    {
                        linked.CancelAfter(TotalTimeout);
                        gotContent = true;
                        answer.Append(full);
                        delta?.Report(full);
                    }

                    break;
                }

                // 其余行（event:/注释/keep-alive）忽略
            }

            var text = answer.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return AiVisionContract.NoContentPrefix + "有效内容。";
            }

            // 截断不能静默成“已完成”：2000/3000 上限对大表格转 Markdown 并不宽裕。
            var result = text.Trim();
            if (finishReason == "length")
            {
                result += AiVisionContract.TruncatedSuffix;
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return "AI 请求已被取消。";
        }
        catch (OperationCanceledException)
        {
            return AiVisionContract.NetworkErrorPrefix + ": 请求超时（在时限内未完成生成，可重试或换更快的模型）。";
        }
        catch (Exception ex)
        {
            return AiVisionContract.NetworkErrorPrefix + ": " + ex.Message;
        }
    }

    /// <summary>错误响应体只回显开头一段：网关错误页常是数 KB 的 HTML，整段塞进结果区毫无可读性。</summary>
    internal static string Clip(string text, int max = 400)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…(已截断)";
    }

    /// <summary>
    /// 设置页与请求侧共用的端点校验：绝对 http(s) 地址，http 仅放行本机回环（Ollama 等本地推理）。
    /// 返回的 baseUri 已去掉尾部斜杠，可能自带 /chat/completions 后缀，由调用方各自处理。
    /// </summary>
    internal static bool TryResolveApiBase(string? rawUrl, out Uri baseUri, out string? error)
    {
        var endpoint = (rawUrl ?? "").Trim().TrimEnd('/');
        if (endpoint.Length == 0)
        {
            endpoint = AppSettings.DefaultAiApiUrl;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out baseUri!)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "AI 接口地址无效：" + (string.IsNullOrWhiteSpace(rawUrl) ? "(空)" : rawUrl);
            return false;
        }

        if (baseUri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(baseUri.Host))
        {
            error = "AI 接口地址使用了 http:// 明文端点，API Key 与截图会明文传输；仅支持本机回环 (127.0.0.1 / localhost)，远程端点请改用 https://";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>http 端点仅放行本机回环（Ollama 等本地推理服务），其余一律要求 https。</summary>
    internal static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || host.Equals("::1", StringComparison.Ordinal)
           || host.Equals("[::1]", StringComparison.Ordinal)
           || host.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>
    /// 把 data URL 以 UTF-8 字节形式写入 writer：base64 直接编码进池化缓冲，
    /// 避免“整串 base64 字符串 + 前缀拼接”的两份大对象分配（4K 截图仅 base64 就 3-8MB，都落在 LOH）。
    /// </summary>
    private static void WriteDataUrl(Utf8JsonWriter writer, byte[] png)
    {
        const string Prefix = "data:image/png;base64,";
        var total = Prefix.Length + ((png.Length + 2) / 3) * 4;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var destination = buffer.AsSpan(0, total);
            Encoding.ASCII.GetBytes(Prefix.AsSpan(), destination);
            System.Buffers.Text.Base64.EncodeToUtf8(png, destination[Prefix.Length..], out _, out _);
            writer.WriteString("url", destination);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// AI 结果文案前缀契约：AskVisionAsync 以带前缀的文案表达“需要配置/请求失败”，
/// AiVisionWindow.Classify 靠前缀归类状态色。此前两侧各自硬编码字符串，改一个字即双双失效；
/// 收敛到这里后，窗口与服务共享同一份定义。
/// </summary>
internal static class AiVisionContract
{
    public const string HintPrefix = "【提示】";
    public const string RequestFailedPrefix = "AI 请求失败";
    public const string NetworkErrorPrefix = "网络或接口请求异常";
    public const string NoContentPrefix = "AI 未返回";
    public const string InvokeErrorPrefix = "调用发生异常";

    /// <summary>finish_reason == length 时追加在回答末尾；窗口据此把状态标成“已截断”。</summary>
    public const string TruncatedSuffix = "\n\n⚠ 回答因长度上限被截断。";
}
