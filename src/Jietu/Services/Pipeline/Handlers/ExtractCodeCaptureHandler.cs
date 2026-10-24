namespace Jietu;

internal sealed class ExtractCodeCaptureHandler : ICaptureActionHandler
{
    public int Priority => 12;

    public bool CanHandle(PostCaptureContext context) => context.Shot.ExtractCode;

    public async Task ExecuteAsync(PostCaptureContext context)
    {
        var shot = context.Shot;
        context.Toast.ShowSticky("正在智能识别代码");
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
            context.Toast.Show("未识别到文本，已按图片复制");
            return;
        }

        var result = CodeExtractService.Process(text);
        var targetText = result.Markdown;

        try
        {
            var added = context.Store.TryAdd(ClipKind.Text, targetText, null, Hashing.OfText(targetText), ClipSource.Clipboard);
            context.Clipboard.SetText(targetText);
            if (added != null)
            {
                context.NotifyHistoryInserted(added);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            context.Toast.Show("写入剪贴板失败");
            return;
        }

        var pasted = false;
        try
        {
            await Task.Delay(60);
            pasted = await PasteService.TryPasteAsync(context.PasteBack);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        context.Toast.Show(pasted
            ? $"已提取代码并粘贴 ({result.Language})"
            : $"已提取代码并复制为 Markdown ({result.Language})");
    }
}
