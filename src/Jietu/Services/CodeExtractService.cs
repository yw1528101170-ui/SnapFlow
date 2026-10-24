using System.Text;
using System.Text.RegularExpressions;

namespace Jietu;

public readonly record struct CodeExtractResult(string Code, string Language, string Markdown);

public static class CodeExtractService
{
    private static readonly Regex LineNumberRegex = new(@"^\s*\d{1,5}\s*[:|│]\s*", RegexOptions.Compiled);
    private static readonly Regex NumberPrefixRegex = new(@"^\s*\d{1,4}\s{2,}", RegexOptions.Compiled);

    public static CodeExtractResult Process(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new CodeExtractResult("", "text", "```\n\n```");
        }

        var cleanedCode = CleanOcrArtifacts(rawText);
        var language = DetectLanguage(cleanedCode);
        var markdown = FormatMarkdown(cleanedCode, language);

        return new CodeExtractResult(cleanedCode, language, markdown);
    }

    public static string CleanOcrArtifacts(string rawText)
    {
        var lines = rawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cleanedLines = new List<string>(lines.Length);

        // 统计是否大部分非空行都包含行号前缀
        int totalNonEmpty = 0;
        int withLineNumber = 0;
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            totalNonEmpty++;
            if (LineNumberRegex.IsMatch(l) || NumberPrefixRegex.IsMatch(l))
            {
                withLineNumber++;
            }
        }

        bool stripLineNumbers = totalNonEmpty >= 3 && (withLineNumber * 100 / totalNonEmpty >= 60);

        foreach (var rawLine in lines)
        {
            var line = rawLine;

            // 1. 去除行号
            if (stripLineNumbers)
            {
                line = LineNumberRegex.Replace(line, "");
                line = NumberPrefixRegex.Replace(line, "");
            }

            // 2. 常见 OCR 全角与畸变字符清洗
            line = line
                .Replace("“", "\"")
                .Replace("”", "\"")
                .Replace("‘", "'")
                .Replace("’", "'")
                .Replace("—>", "->")
                .Replace("一>", "->")
                .Replace("=>", "=>")
                .Replace("；", ";")
                .Replace("：", ":")
                .Replace("（", "(")
                .Replace("）", ")")
                .Replace("，", ",")
                .Replace("【", "[")
                .Replace("】", "]")
                .Replace("！", "!");

            // 3. 修复箭头与逻辑符号常见粘连
            if (line.Contains(" = > ")) line = line.Replace(" = > ", " => ");
            if (line.Contains(" - > ")) line = line.Replace(" - > ", " -> ");

            cleanedLines.Add(line.TrimEnd());
        }

        // 去除开头与结尾的多余纯空行
        while (cleanedLines.Count > 0 && string.IsNullOrWhiteSpace(cleanedLines[0]))
        {
            cleanedLines.RemoveAt(0);
        }
        while (cleanedLines.Count > 0 && string.IsNullOrWhiteSpace(cleanedLines[^1]))
        {
            cleanedLines.RemoveAt(cleanedLines.Count - 1);
        }

        return string.Join("\n", cleanedLines);
    }

    public static string DetectLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "text";

        var scores = new Dictionary<string, int>
        {
            ["csharp"] = 0,
            ["python"] = 0,
            ["typescript"] = 0,
            ["javascript"] = 0,
            ["rust"] = 0,
            ["go"] = 0,
            ["sql"] = 0,
            ["html"] = 0,
            ["css"] = 0,
            ["json"] = 0,
            ["powershell"] = 0,
            ["shell"] = 0,
        };

        var lower = code.ToLowerInvariant();

        // 快速检测 JSON
        var trimmed = code.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            if (trimmed.Contains("\":") || trimmed.Contains("\": "))
            {
                scores["json"] += 15;
            }
        }

        // 快速检测 HTML
        if (lower.Contains("<!doctype html") || (lower.Contains("<html") && lower.Contains("</html>")) || (lower.Contains("<div") && lower.Contains("</div>")))
        {
            scores["html"] += 20;
        }

        // C#
        if (code.Contains("using System")) scores["csharp"] += 10;
        if (code.Contains("namespace ")) scores["csharp"] += 8;
        if (code.Contains("Console.WriteLine")) scores["csharp"] += 8;
        if (Regex.IsMatch(code, @"\b(public|private|internal|protected)\s+(class|sealed|record|struct|enum|interface)\b")) scores["csharp"] += 6;
        if (Regex.IsMatch(code, @"\b(async\s+Task|Task<|int\s+|string\s+|bool\s+|var\s+)\b")) scores["csharp"] += 3;
        if (code.Contains("=>")) scores["csharp"] += 2;

        // Python
        if (Regex.IsMatch(code, @"\bdef\s+\w+\s*\(")) scores["python"] += 8;
        if (code.Contains("__name__ == '__main__'") || code.Contains("__name__ == \"__main__\"")) scores["python"] += 10;
        if (Regex.IsMatch(code, @"\b(elif|import|from|self\.|lambda)\b")) scores["python"] += 3;
        if (code.Contains("print(")) scores["python"] += 3;
        if (Regex.IsMatch(code, @"\b(None|True|False)\b")) scores["python"] += 3;

        // TypeScript / JavaScript
        if (Regex.IsMatch(code, @"\b(const|let|var)\s+\w+\s*=")) { scores["javascript"] += 2; scores["typescript"] += 2; }
        if (Regex.IsMatch(code, @"\bconsole\.(log|error|warn)\(")) { scores["javascript"] += 4; scores["typescript"] += 4; }
        if (Regex.IsMatch(code, @"\bfunction\s+\w+\s*\(")) { scores["javascript"] += 3; scores["typescript"] += 3; }
        if (Regex.IsMatch(code, @"\b(interface|type)\s+\w+\s*[{=]")) scores["typescript"] += 7;
        if (Regex.IsMatch(code, @":\s*(string|number|boolean|any|void)\b")) scores["typescript"] += 5;
        if (code.Contains("export default ") || code.Contains("export const ")) { scores["typescript"] += 3; scores["javascript"] += 2; }

        // Rust
        if (Regex.IsMatch(code, @"\bfn\s+\w+\s*\(")) scores["rust"] += 7;
        if (code.Contains("println!")) scores["rust"] += 8;
        if (Regex.IsMatch(code, @"\blet\s+mut\s+")) scores["rust"] += 8;
        if (Regex.IsMatch(code, @"\b(pub\s+struct|impl\s+|match\s+)")) scores["rust"] += 6;

        // Go
        if (Regex.IsMatch(code, @"\bfunc\s+\w+\s*\(")) scores["go"] += 7;
        if (code.Contains("package main") || code.Contains("package ")) scores["go"] += 7;
        if (code.Contains("fmt.Println") || code.Contains("fmt.Printf")) scores["go"] += 8;
        if (code.Contains(":=")) scores["go"] += 4;

        // SQL
        if (Regex.IsMatch(code, @"\b(SELECT\s+.+\s+FROM|INSERT\s+INTO|UPDATE\s+.+\s+SET|DELETE\s+FROM|CREATE\s+TABLE)\b", RegexOptions.IgnoreCase)) scores["sql"] += 12;
        if (Regex.IsMatch(code, @"\b(WHERE|GROUP\s+BY|ORDER\s+BY|LEFT\s+JOIN|INNER\s+JOIN)\b", RegexOptions.IgnoreCase)) scores["sql"] += 4;

        // CSS
        if (Regex.IsMatch(code, @"\b(margin|padding|display|background|border|font-size|justify-content)\s*:\s*")) scores["css"] += 8;

        // PowerShell
        if (Regex.IsMatch(code, @"\b(Get-|Set-|New-|Remove-|Write-Host|Invoke-)\w+")) scores["powershell"] += 8;
        if (Regex.IsMatch(code, @"\$\w+\s*=")) scores["powershell"] += 2;

        // Shell
        if (code.StartsWith("#!/bin/bash") || code.StartsWith("#!/bin/sh")) scores["shell"] += 10;
        if (Regex.IsMatch(code, @"\b(sudo|chmod|chown|grep|cat|echo)\s+")) scores["shell"] += 4;

        var best = scores.OrderByDescending(kv => kv.Value).First();
        return best.Value >= 4 ? best.Key : "text";
    }

    public static string FormatMarkdown(string code, string language)
    {
        var sb = new StringBuilder();
        sb.Append("```").AppendLine(language);
        sb.AppendLine(code);
        sb.Append("```");
        return sb.ToString();
    }
}
