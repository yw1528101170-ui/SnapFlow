namespace Jietu;

internal sealed class TranslateCaptureHandler : ICaptureActionHandler
{
    public int Priority => 5;

    public bool CanHandle(PostCaptureContext context) => context.Shot.Translate;

    public async Task ExecuteAsync(PostCaptureContext context)
    {
        var shot = context.Shot;
        context.Toast.ShowSticky("正在识别并翻译...");
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
            try
            {
                var added = context.Store.TryAdd(ClipKind.Text, text, null, Hashing.OfText(text), ClipSource.Clipboard);
                context.Clipboard.SetText(text);
                if (added != null)
                {
                    context.NotifyHistoryInserted(added);
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://translate.google.com/?sl=auto&tl=zh-CN&text={Uri.EscapeDataString(text)}") { UseShellExecute = true });
                context.Toast.Show("已提取文字并打开在线翻译");
            }
            catch
            {
                await BitmapCaptureHandler.ExecuteBitmapAsync(context);
            }
        }
    }
}
