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
    private static HashSet<string> validVariants = new HashSet<string>();
    private static HashSet<string> stripShaderNames = new HashSet<string>();
    private static HashSet<string> excludeKeywords = new HashSet<string>();
    private static HashSet<string> needHandleNames = new HashSet<string>();
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

        bool inSVCRange = needHandleNames.Contains(shader.name);

        for (int i = data.Count - 1; i >= 0; i--)
        {
            // key 保持阶段无关（用于 SVC 匹配）；displayKey 带阶段仅用于日志展示
            string key = GenerateVariantKey(shader.name, snippet.passType, data[i].shaderKeywordSet.GetShaderKeywords());
            string displayKey = $"{stageTag}|{key}";

            // 检查变种是否包含排除关键字（对所有 shader 生效）
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
            else if (inSVCRange && !validVariants.Contains(key))
            {
                _buildLog[shader.name].Add($"[裁剪][未收录SVC] {displayKey}");
                data.RemoveAt(i);
                _totalStripped++;
            }
            else if (!inSVCRange)
            {
                _buildLog[shader.name].Add($"[保留][未在收集范围] {displayKey}");
                _totalKept++;
            }
            else
            {
                _buildLog[shader.name].Add($"[保留][SVC收录] {displayKey}");
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
        validVariants.Clear();
        stripShaderNames.Clear();
        excludeKeywords.Clear();
        needHandleNames.Clear();

        // 从配置读取 SVC 路径
        string svcFolder = ShaderVariantCollectorSetting.GetStripSVCPath("Default");

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

        Debug.Log($"[裁剪配置] SVC路径: {svcFolder}");
        Debug.Log($"[裁剪配置] 完全裁剪 shader: {stripShaderNames.Count} 个");
        Debug.Log($"[裁剪配置] 排除关键字: {excludeKeywords.Count} 个");

        // 加载 SVC 文件
        string[] guids = AssetDatabase.FindAssets("t:ShaderVariantCollection", new[] { svcFolder });

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            ShaderVariantCollection svc = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(path);
            if (svc == null) continue;

            SerializedObject so = new SerializedObject(svc);
            SerializedProperty shadersProp = so.FindProperty("m_Shaders");
            if (shadersProp == null || !shadersProp.isArray)
            {
                Debug.LogWarning($"无法读取 ShaderVariantCollection 中的 m_Shaders：{path}");
                continue;
            }

            for (int i = 0; i < shadersProp.arraySize; i++)
            {
                var shaderEntry = shadersProp.GetArrayElementAtIndex(i);
                var shaderProp = shaderEntry.FindPropertyRelative("first");
                var variantsProp = shaderEntry.FindPropertyRelative("second.variants");

                Shader shader = shaderProp.objectReferenceValue as Shader;
                if (shader == null || variantsProp == null || !variantsProp.isArray) continue;

                // 跳过完全裁剪的 shader
                if (stripShaderNames.Contains(shader.name)) continue;

                needHandleNames.Add(shader.name);

                for (int j = 0; j < variantsProp.arraySize; j++)
                {
                    var variantProp = variantsProp.GetArrayElementAtIndex(j);
                    var keywordsProp = variantProp.FindPropertyRelative("keywords");
                    int passType = variantProp.FindPropertyRelative("passType")?.intValue ?? 0;

                    List<string> keywords = new List<string>();
                    if (keywordsProp != null && keywordsProp.propertyType == SerializedPropertyType.String)
                    {
                        string keywordLine = keywordsProp.stringValue;
                        if (!string.IsNullOrEmpty(keywordLine))
                        {
                            keywords.AddRange(keywordLine.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
                        }
                    }

                    string key = GenerateVariantKey(shader.name, (PassType)passType, keywords.ToArray());
                    validVariants.Add(key);
                }
            }
        }

        Debug.Log($"[裁剪配置] 有效变体总数: {validVariants.Count}");
    }

    private static string GenerateVariantKey(string shaderName, PassType passType, string[] keywords)
    {
        List<string> keywordList = new List<string>(keywords);
        keywordList.Sort();
        return $"{shaderName}|{(int)passType}|{string.Join("+", keywordList)}";
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
