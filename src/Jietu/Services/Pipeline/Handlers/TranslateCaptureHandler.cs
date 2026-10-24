namespace Jietu;

internal sealed class TranslateCaptureHandler : ICaptureActionHandler
{
    public int Priority => 5;

    public bool CanHandle(PostCaptureContext context) => context.Shot.Translate;

    public async Task ExecuteAsync(PostCaptureContext context)
    {
        var shot = context.Shot;
        // 明确告知文字的去向：截图 OCR 结果会发送到 Google 翻译服务。
        context.Toast.ShowSticky("正在识别并翻译（文字将发送到 Google 翻译）...");
        string? text = null;
        try
        {
            var png = await ImageBytes.ToPngAsync(shot.Pixels, shot.Width, shot.Height);
            text = await OcrService.ReadAsync(png);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await BitmapCaptureHandler.ExecuteBitmapAsync(context);
            context.Toast.Show("未识别到文字，已按图片复制");
            return;
        }

        var translated = await TranslateService.TranslateAsync(text);
        if (!string.IsNullOrEmpty(translated))
        {
            try
            {
                var added = context.Store.TryAdd(ClipKind.Text, translated, null, Hashing.OfText(translated), ClipSource.Clipboard);
                context.Clipboard.SetText(translated);
                if (added != null)
                {
                    context.NotifyHistoryInserted(added);
                }

                var preview = translated.Length > 20 ? string.Concat(translated.AsSpan(0, 20), "...") : translated;
                context.Toast.Show($"已翻译并复制：{preview}");
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                context.Toast.Show("翻译成功但写入剪贴板失败");
            }
        }
        else
        {
            // 翻译失败：不再把原文拼进浏览器 URL 打开网页版——OCR 出的内容（可能含密码/
            // 内网信息）会因此进入浏览器历史与网络请求日志。改为复制原文交给用户自行处理。
            try
            {
                var added = context.Store.TryAdd(ClipKind.Text, text, null, Hashing.OfText(text), ClipSource.Clipboard);
                context.Clipboard.SetText(text);
                if (added != null)
                {
                    context.NotifyHistoryInserted(added);
                }

                context.Toast.Show("翻译服务不可用，已复制识别出的原文");
            }
            catch
            {
                await BitmapCaptureHandler.ExecuteBitmapAsync(context);
            }
        }
    }
}
