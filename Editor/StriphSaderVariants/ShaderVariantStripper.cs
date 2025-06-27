using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine.Rendering;
using System.Collections.Generic;


public class ShaderVariantStripper : IPreprocessShaders
{
    // 修改为你的 ShaderVariantCollection 文件夹路径（相对于 Assets）
    // private const string VariantCollectionFolder = "Assets/Scenes/vs";
    private const string VariantCollectionFolder = "Assets/ResourcesAB/Config/ShaderVarians";

    private static HashSet<string> validVariants = new HashSet<string>();
    private static HashSet<string> needHandleNames = new HashSet<string>();

    private static List<ShaderVariantCollection> shaderVariantCollections = new List<ShaderVariantCollection>();
    private static bool initialized = false;

    public int callbackOrder => 0;

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (!initialized)
        {
            LoadAllShaderVariants();
            initialized = true;
        }

        //不包含在变体集里面的不处理
        if (!needHandleNames.Contains(shader.name))
        {
            Debug.Log("不在变体收集列表里面,不做剔除处理："+ shader.name);
            return;
        }

        int originalCount = data.Count;
        int removedCount = 0;

        for (int i = data.Count - 1; i >= 0; i--)
        {
            if (SkipSpecial(shader.name, snippet, data[i]))
            {
                Debug.Log("[SkipSpecial 保留]"+GenerateVariantKey(shader.name, snippet, data[i]));
                continue;    
            }
            
            string key = GenerateVariantKey(shader.name, snippet, data[i]);
            if (!validVariants.Contains(key))
            {
                Debug.Log("[剔除]"+key);
                data.RemoveAt(i);
                removedCount++;
            }
            else
            {
                Debug.Log("[保留]"+key);
            }
        }

        // if (removedCount > 0)
        {
            Debug.Log($"[剔除] Shader: {shader.name}, Snippet: {snippet.passName}，剔除 {removedCount} / {originalCount}");
        }
    }

    private bool SkipSpecial(string shaderName, ShaderSnippetData snippet, ShaderCompilerData data)
    {
        var keys = data.shaderKeywordSet.GetShaderKeywords();
        foreach (var key in keys)
        {
            if (key.name == "_ADDITIONAL_LIGHT_SHADOWS")
            {
                return false;
            }

            if (key.name == "_ADDITIONAL_LIGHTS")
            {
                return true;
            }
            
            if (key.name == "LIGHTMAP_ON")
            {
                return true;
            }
        }
        return false;
    }


    [MenuItem("Tools/ShaderVariantStripper LoadAllShaderVariants")]
    private static void LoadAllShaderVariants()
    {
        validVariants.Clear();
        needHandleNames.Clear();

        string[] guids = AssetDatabase.FindAssets("t:ShaderVariantCollection", new[] { VariantCollectionFolder });

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
                needHandleNames.Add(shader.name);
                
                if (shader == null || variantsProp == null || !variantsProp.isArray) continue;

                for (int j = 0; j < variantsProp.arraySize; j++)
                {
                    var variantProp = variantsProp.GetArrayElementAtIndex(j);
                    var keywordsProp = variantProp.FindPropertyRelative("keywords");

                    List<string> keywords = new List<string>();
                    if (keywordsProp != null && keywordsProp.propertyType == SerializedPropertyType.String)
                    {
                        string keywordLine = keywordsProp.stringValue;
                        if (!string.IsNullOrEmpty(keywordLine))
                        {
                            keywords.AddRange(keywordLine.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
                        }
                    }


                    // 用 passType int 代替 passName（如果你没有passName字段）
                    int passType = variantProp.FindPropertyRelative("passType")?.intValue ?? 0;
                    string passTypeName = ((UnityEngine.Rendering.PassType)passType).ToString();
                    
                    string key = GenerateVariantKey(shader.name, passTypeName, keywords.ToArray());
                    // Debug.Log(key);
                    validVariants.Add(key);
                }
            }
        }

        Debug.Log($"[初始化] 收集到有效变体总数: {validVariants.Count}");
    }

    private static string GenerateVariantKey(string shaderName, string passName, string[] keywords)
    {
        List<string> keywordList = new List<string>(keywords);
        keywordList.Sort(); // 排序保证一致性
        // return $"{shaderName}|{passName}|{string.Join("+", keywordList)}";
        return $"{shaderName}|{string.Join("+", keywordList)}";
    }

    private static string GenerateVariantKey(string shaderName, ShaderSnippetData snippet, ShaderCompilerData data)
    {
        return GenerateVariantKey(shaderName, snippet.passType, data.shaderKeywordSet.GetShaderKeywords());
    }

    private static string GenerateVariantKey(string shaderName, PassType passType, IEnumerable<ShaderKeyword> keywords)
    {
        List<string> keywordList = new List<string>();
        foreach (var kw in keywords)
        {
            keywordList.Add(kw.name);
        }

        keywordList.Sort(); // 保证顺序一致

        // return $"{shaderName}|{passType}|{string.Join("+", keywordList)}";
        return $"{shaderName}|{string.Join("+", keywordList)}";
    }
    // We no longer need the GenerateVariantKey methods since we're using ShaderVariantCollection.Contains directly
}