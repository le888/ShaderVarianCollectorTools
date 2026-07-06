using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine.Rendering;
using System.Collections.Generic;


public class ShaderVariantStripper : IPreprocessShaders
{
    private static HashSet<string> validVariants = new HashSet<string>();
    private static HashSet<string> stripShaderNames = new HashSet<string>();
    private static HashSet<string> excludeKeywords = new HashSet<string>();
    private static HashSet<string> needHandleNames = new HashSet<string>();
    private static bool initialized = false;

    public int callbackOrder => 0;

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (!initialized)
        {
            LoadAllShaderVariants();
            initialized = true;
        }

        // 完全裁剪：shader 在裁剪列表中，移除所有变种
        if (stripShaderNames.Contains(shader.name))
        {
            int removed = data.Count;
            data.Clear();
            Debug.Log($"[完全裁剪] Shader: {shader.name}, 移除全部 {removed} 个变种");
            return;
        }

        // 不在变体集里面的不处理
        if (!needHandleNames.Contains(shader.name))
        {
            return;
        }

        int originalCount = data.Count;
        int removedCount = 0;

        for (int i = data.Count - 1; i >= 0; i--)
        {
            string key = GenerateVariantKey(shader.name, snippet.passType, data[i].shaderKeywordSet.GetShaderKeywords());

            // 检查变种是否包含排除关键字
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
                Debug.Log($"[排除关键字] {key}");
                data.RemoveAt(i);
                removedCount++;
            }
            else if (!validVariants.Contains(key))
            {
                Debug.Log($"[剔除] {key}");
                data.RemoveAt(i);
                removedCount++;
            }
        }

        if (removedCount > 0)
        {
            Debug.Log($"[裁剪] Shader: {shader.name}, Pass: {snippet.passName}, 剔除 {removedCount} / {originalCount}");
        }
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
