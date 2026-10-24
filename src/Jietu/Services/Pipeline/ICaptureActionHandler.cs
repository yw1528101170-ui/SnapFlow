namespace Jietu;

internal interface ICaptureActionHandler
{
    int Priority { get; }
    bool CanHandle(PostCaptureContext context);
    Task ExecuteAsync(PostCaptureContext context);
}
