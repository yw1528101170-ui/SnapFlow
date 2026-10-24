using System.Net.Http;
using System.Text.Json;

namespace Jietu;

internal static class TranslateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<string?> TranslateAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var hasChinese = text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
            var targetLang = hasChinese ? "en" : "zh-CN";
            // 文本放 POST 请求体而不是 URL 查询串：GET 的 q= 参数会进入代理/网关的访问日志，
            // 把屏幕上 OCR 出的敏感内容泄露给网络中间层。gtx 端点对两种方式等价接受。
            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={targetLang}&dt=t";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = text });
            var response = await Http.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var sentences = doc.RootElement[0];
                if (sentences.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var s in sentences.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0)
                        {
                            sb.Append(s[0].GetString());
                        }
                    }

                    var result = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(result))
                    {
                        return result;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("翻译服务异常：" + ex.Message);
        }

        return null;
    }
}
