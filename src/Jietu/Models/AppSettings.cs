namespace Jietu;

public sealed class AppSettings
{
    // 保留天数与 AI 默认配置的唯一权威值：此前默认模型/URL/Prompt 字面量散布 10+ 处，
    // 改默认值要追着改一圈；UI 与服务的「未配置回退」一律引用这里的常量。
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;
    public const string DefaultAiApiUrl = "https://api.openai.com/v1";
    public const string DefaultAiModel = "gpt-4o-mini";
    public const string DefaultAiPrompt = "请分析并总结这张截图的内容：若是代码请解释核心逻辑与排查建议，若是表格或图文请提取关键信息。";

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
    public string AiApiUrl { get; set; } = DefaultAiApiUrl;
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = DefaultAiModel;
    public string AiPrompt { get; set; } = DefaultAiPrompt;
}
