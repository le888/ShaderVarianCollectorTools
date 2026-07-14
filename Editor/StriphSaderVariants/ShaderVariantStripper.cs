using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor.Build.Reporting;


public class ShaderVariantStripper : IPreprocessShaders
{
    private static HashSet<string> stripShaderNames = new HashSet<string>();
    private static HashSet<string> excludeKeywords = new HashSet<string>();
    private static bool initialized = false;

    // 构建日志：记录每个 shader 保留的变种
    private static Dictionary<string, List<string>> _buildLog = new Dictionary<string, List<string>>();
    private static int _totalOriginal = 0;
    private static int _totalKept = 0;
    private static int _totalStripped = 0;

    public int callbackOrder => 0;

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (!initialized)
        {
            _buildLog.Clear();
            _totalOriginal = 0;
            _totalKept = 0;
            _totalStripped = 0;
            LoadAllShaderVariants();
            initialized = true;
        }

        // 当前回调的 shader 阶段（Unity 按阶段分别回调：vertex/fragment/...）
        string stageTag = StageShortName(snippet.shaderType);
        // 原始诊断：无条件记录每个 (shaderType, passType, count)，确认 fragment 阶段是否回调到
        Debug.Log($"[ShaderStrip诊断] shader={shader.name} shaderType={(int)snippet.shaderType}({snippet.shaderType}) passType={(int)snippet.passType} passName={snippet.passName} 变体数={data.Count}");

        // 完全裁剪：shader 在裁剪列表中，移除所有变种
        if (stripShaderNames.Contains(shader.name))
        {
            int removed = data.Count;
            _totalOriginal += removed;
            _totalStripped += removed;
            data.Clear();
            Debug.Log($"[完全裁剪] Shader: {shader.name}, Stage: {stageTag}, 移除全部 {removed} 个变种");
            return;
        }

        int originalCount = data.Count;
        _totalOriginal += originalCount;

        if (!_buildLog.ContainsKey(shader.name))
            _buildLog[shader.name] = new List<string>();

        for (int i = data.Count - 1; i >= 0; i--)
        {
            string displayKey = $"{stageTag}|{GenerateVariantKey(shader.name, snippet.passType, data[i].shaderKeywordSet.GetShaderKeywords())}";

            // 仅按排除关键字裁剪；不再做 SVC 白名单过滤（原逻辑在 URP 下只对 vertex 生效，易误删）
            bool hasExcludedKeyword = false;
            foreach (var kw in data[i].shaderKeywordSet.GetShaderKeywords())
            {
                if (excludeKeywords.Contains(kw.name))
                {
                    hasExcludedKeyword = true;
                    break;
                }
            }

            if (hasExcludedKeyword)
            {
                _buildLog[shader.name].Add($"[裁剪][排除关键字] {displayKey}");
                data.RemoveAt(i);
                _totalStripped++;
            }
            else
            {
                _buildLog[shader.name].Add($"[保留] {displayKey}");
                _totalKept++;
            }
        }

        if (originalCount != data.Count)
        {
            Debug.Log($"[裁剪] Shader: {shader.name}, Pass: {snippet.passName}, Stage: {stageTag}, 保留 {data.Count} / {originalCount}");
        }
    }

    /// <summary>
    /// 把 ShaderType 阶段枚举转成短名（vs/fs/...），未知值回退到枚举名
    /// </summary>
    private static string StageShortName(ShaderType st)
    {
        string s = st.ToString();
        switch (s)
        {
            case "Vertex": return "vs";
            case "Fragment": return "fs";
            case "Geometry": return "gs";
            case "Hull": return "hs";
            case "Domain": return "ds";
            default: return s;
        }
    }

    private static string FormatVariantKey(PassType passType, IEnumerable<ShaderKeyword> keywords)
    {
        List<string> keywordList = new List<string>();
        foreach (var kw in keywords)
            keywordList.Add(kw.name);
        keywordList.Sort();
        return $"{(int)passType}|{string.Join("+", keywordList)}";
    }

    /// <summary>
    /// 构建完成后写入日志文件
    /// </summary>
    internal static void WriteBuildLog()
    {
        if (_buildLog.Count == 0) return;

        string logDir = Path.Combine(Application.dataPath, "..", "BuildLogs");
        Directory.CreateDirectory(logDir);
        string logPath = Path.Combine(logDir, $"ShaderBuildLog_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt");

        var sb = new StringBuilder();
        sb.AppendLine("========== Shader 变种构建日志 ==========");
        sb.AppendLine($"时间: {System.DateTime.Now}");
        sb.AppendLine($"变种总数: 原始={_totalOriginal}, 保留={_totalKept}, 裁剪={_totalStripped}");
        sb.AppendLine();

        // 按 shader 名称排序
        var sortedShaders = new List<string>(_buildLog.Keys);
        sortedShaders.Sort();

        foreach (var shaderName in sortedShaders)
        {
            var variants = _buildLog[shaderName];
            sb.AppendLine($"--- {shaderName} ({variants.Count} 条) ---");

            // 按阶段(vs/fs/...)统计保留/裁剪数
            var stageStats = new Dictionary<string, (int kept, int stripped)>();
            foreach (var v in variants)
            {
                // 日志格式: "[原因] stage|shader|passType|keywords"，stage 在第一个 '|' 前、最后一个空格后
                int bar = v.IndexOf('|');
                string stage = "?";
                if (bar > 0)
                {
                    int space = v.LastIndexOf(' ', bar);
                    stage = v.Substring(space + 1, bar - space - 1);
                }
                bool kept = v.Contains("[保留]");
                if (!stageStats.ContainsKey(stage))
                    stageStats[stage] = (0, 0);
                var cur = stageStats[stage];
                stageStats[stage] = kept ? (cur.kept + 1, cur.stripped) : (cur.kept, cur.stripped + 1);
            }
            var stageSummary = new List<string>();
            foreach (var kv in stageStats)
                stageSummary.Add($"{kv.Key}: 保留{kv.Value.kept}/裁剪{kv.Value.stripped}");
            sb.AppendLine($"  [阶段统计] {string.Join(", ", stageSummary)}");

            foreach (var v in variants)
            {
                sb.AppendLine($"  {v}");
            }
            sb.AppendLine();
        }

        File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
        Debug.Log($"[构建日志] 已保存: {logPath}");

        _buildLog.Clear();
        initialized = false;
    }

    [MenuItem("Tools/ShaderVariantStripper LoadAllShaderVariants")]
    private static void LoadAllShaderVariants()
    {
        stripShaderNames.Clear();
        excludeKeywords.Clear();

        // 构建完全裁剪的 shader 列表：收集器过滤 + 额外配置
        var filterNames = ShaderVariantCollectorSetting.GetFilterShaderNames("Default");
        foreach (var name in filterNames)
        {
            if (!string.IsNullOrEmpty(name))
                stripShaderNames.Add(name);
        }
        var additionalNames = ShaderVariantCollectorSetting.GetStripAdditionalShaderNames("Default");
        foreach (var name in additionalNames)
        {
            if (!string.IsNullOrEmpty(name))
                stripShaderNames.Add(name);
        }

        // 构建排除关键字列表：收集器排除 + 额外配置
        var globalKw = ShaderVariantCollectorSetting.GetGlobalKeywords("Default");
        foreach (var kw in globalKw)
        {
            if (!string.IsNullOrEmpty(kw))
                excludeKeywords.Add(kw);
        }
        var additionalKw = ShaderVariantCollectorSetting.GetStripAdditionalKeywords("Default");
        foreach (var kw in additionalKw)
        {
            if (!string.IsNullOrEmpty(kw))
                excludeKeywords.Add(kw);
        }

        Debug.Log($"[裁剪配置] 完全裁剪 shader: {stripShaderNames.Count} 个");
        Debug.Log($"[裁剪配置] 排除关键字: {excludeKeywords.Count} 个");
    }

    private static string GenerateVariantKey(string shaderName, PassType passType, IEnumerable<ShaderKeyword> keywords)
    {
        List<string> keywordList = new List<string>();
        foreach (var kw in keywords)
        {
            keywordList.Add(kw.name);
        }
        keywordList.Sort();
        return $"{shaderName}|{(int)passType}|{string.Join("+", keywordList)}";
    }
}

/// <summary>
/// 构建完成后自动写入 shader 变种日志
/// </summary>
public class ShaderBuildLogWriter : IPostprocessBuildWithReport
{
    public int callbackOrder => 999;

    public void OnPostprocessBuild(BuildReport report)
    {
        ShaderVariantStripper.WriteBuildLog();
    }
}
