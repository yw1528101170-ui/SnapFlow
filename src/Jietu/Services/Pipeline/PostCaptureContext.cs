namespace Jietu;

internal sealed record PostCaptureContext(
    PickedShot Shot,
    PasteTarget PasteBack,
    AppSettings Settings,
    HistoryStore Store,
    ClipIngestService Ingest,
    ClipboardService Clipboard,
    IToastSink Toast,
    Action<string> PinFileAction,
    Action<string> PinTextAction,
    Action<ClipRecord> NotifyHistoryInserted,
    Action<string, byte[]> RememberImageCache,
    Action<byte[], string?> OpenAiVisionAction);
