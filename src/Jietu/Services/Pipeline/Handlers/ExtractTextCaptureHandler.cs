namespace Jietu;

internal sealed class ExtractTextCaptureHandler : ICaptureActionHandler
{
    public int Priority => 10;

    public bool CanHandle(PostCaptureContext context) => context.Shot.ExtractText;

    public async Task ExecuteAsync(PostCaptureContext context)
    {
        var shot = context.Shot;
        context.Toast.ShowSticky("正在识别");
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
            context.Toast.Show("没有识别到文字，已按图片复制");
            return;
        }

        try
        {
            var added = context.Store.TryAdd(ClipKind.Text, text, null, Hashing.OfText(text), ClipSource.Clipboard);
            context.Clipboard.SetText(text);
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

        context.Toast.Show(pasted ? "已粘贴文字" : "已复制文字，可直接粘贴");
    }
}
