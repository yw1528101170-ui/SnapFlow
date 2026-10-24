namespace Jietu;

public sealed class AppSettings
{
    public string RegionHotkey { get; set; } = "Alt+A";
    public string HistoryHotkey { get; set; } = "Alt+V";
    public int RetentionDays { get; set; } = 30;
    public bool Paused { get; set; }
    public bool RunAtStartup { get; set; }
    public bool SaveScreenshots { get; set; }
    public bool RecordTerminals { get; set; }
    public string SaveFolder { get; set; } = "";
    public List<string> IgnoredProcesses { get; set; } = [];
    public bool ShowOsd { get; set; } = true;
    public string OsdPosition { get; set; } = "TopRight";
    public string OsdDisplay { get; set; } = "Primary";
    public bool OsdHideOnCapture { get; set; } = true;
    public string MultiMonitorCaptureMode { get; set; } = "Auto";
    public string AiApiUrl { get; set; } = "https://api.openai.com/v1";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "gpt-4o-mini";
    public string AiPrompt { get; set; } = "请分析并总结这张截图的内容：若是代码请解释核心逻辑与排查建议，若是表格或图文请提取关键信息。";
}
