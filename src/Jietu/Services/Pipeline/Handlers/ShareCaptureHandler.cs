namespace Jietu;

internal sealed class ShareCaptureHandler : ICaptureActionHandler
{
    public int Priority => 8;

    public bool CanHandle(PostCaptureContext context) => context.Shot.Share;

    public async Task ExecuteAsync(PostCaptureContext context)
    {
        await BitmapCaptureHandler.ExecuteBitmapAsync(context);
        context.Toast.Show("截图已复制，可在聊天窗口直接粘贴发送");
    }
}
