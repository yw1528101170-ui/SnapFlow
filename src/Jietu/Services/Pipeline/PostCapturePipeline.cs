namespace Jietu;

internal sealed class PostCapturePipeline
{
    private readonly List<ICaptureActionHandler> _handlers;

    public PostCapturePipeline(IEnumerable<ICaptureActionHandler> handlers)
    {
        _handlers = handlers.OrderBy(h => h.Priority).ToList();
    }

    public async Task ExecuteAsync(PostCaptureContext context)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(context))
            {
                await handler.ExecuteAsync(context);
                return;
            }
        }
    }
}
