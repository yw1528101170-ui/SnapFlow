namespace Jietu;

internal sealed class AiVisionCaptureHandler : ICaptureActionHandler
{
    public int Priority => 5;

    public bool CanHandle(PostCaptureContext context) => context.Shot.AiVision;

    public async Task ExecuteAsync(PostCaptureContext context)
    {
        var shot = context.Shot;
        try
        {
            var png = await ImageBytes.ToPngAsync(shot.Pixels, shot.Width, shot.Height);
            context.OpenAiVisionAction(png, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            context.Toast.Show("启动 AI 视觉解析失败：" + ex.Message);
        }
    }
}
