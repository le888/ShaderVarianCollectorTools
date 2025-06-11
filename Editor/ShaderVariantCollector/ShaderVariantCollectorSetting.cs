using UnityEngine;
using UnityEditor;

public class ShaderVariantCollectorSetting : ScriptableObject
{
    private const string DefaultSavePath = "MyShaderVariants.shadervariants";

    public static string GetFileName(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_GeFileNamePath";
        return EditorPrefs.GetString(key, DefaultSavePath);
    }
    
    public static void SetFileName(string packageName, string name)
    {
        string key = $"{Application.productName}_{packageName}_GeFileNamePath";
        EditorPrefs.SetString(key, name);
    }
    
    public static string GeFileSavePath(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_GeFileSavePath";
        return EditorPrefs.GetString(key, DefaultSavePath);
    }
    public static void SetFileSavePath(string packageName, string savePath)
    {
        string key = $"{Application.productName}_{packageName}_GeFileSavePath";
        EditorPrefs.SetString(key, savePath);
    }

    public static int GeProcessCapacity(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_GeProcessCapacity";
        return EditorPrefs.GetInt(key, 1000);
    }
    public static void SetProcessCapacity(string packageName, int capacity)
    {
        string key = $"{Application.productName}_{packageName}_GeProcessCapacity";
        EditorPrefs.SetInt(key, capacity);
    }
    
    public static string GeFileSearchPath(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_GeFileInputPath";
        return EditorPrefs.GetString(key, "Assets");
    }
    public static void SetFileSearchPath(string packageName, string inputPath)
    {
        string key = $"{Application.productName}_{packageName}_GeFileInputPath";
        EditorPrefs.SetString(key, inputPath);
    }

    public static string GeSecneSearchPath(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_GeScenePath";
        return EditorPrefs.GetString(key, "Assets");
    }
    
    public static void SetSceneSearchPath(string packageName, string scenePath)
    {
        string key = $"{Application.productName}_{packageName}_GeScenePath";
        EditorPrefs.SetString(key, scenePath);
    }
    
    public static string[] GeBlackPath(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_GeBlackPath";
        string value = EditorPrefs.GetString(key, "Assets");
        string[] paths = value.Split(',');
        return paths;
    }
    
    public static void SetBlackPath(string packageName, string scenePath)
    {
        string key = $"{Application.productName}_{packageName}_GeBlackPath";
        EditorPrefs.SetString(key, scenePath);
    }

    public static bool GetSplitByShaderName(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_SplitByShaderName";
        return EditorPrefs.GetBool(key, false);
    }

    public static void SetSplitByShaderName(string packageName, bool value)
    {
        string key = $"{Application.productName}_{packageName}_SplitByShaderName";
        EditorPrefs.SetBool(key, value);
    }

    public static bool GetCollectSceneVariants(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_CollectSceneVariants";
        return EditorPrefs.GetBool(key, false);
    }

    public static void SetCollectSceneVariants(string packageName, bool value)
    {
        string key = $"{Application.productName}_{packageName}_CollectSceneVariants";
        EditorPrefs.SetBool(key, value);
    }

    public static string[] GetGlobalKeywords(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_GlobalKeywords";
        string value = EditorPrefs.GetString(key, "");
        return string.IsNullOrEmpty(value) ? new string[0] : value.Split(',');
    }

    public static void SetGlobalKeywords(string packageName, string[] keywords)
    {
        string key = $"{Application.productName}_{packageName}_GlobalKeywords";
        string value = string.Join(",", keywords);
        EditorPrefs.SetString(key, value);
    }
    
    public static LocalKeywordCollection GetLocalKeywords(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_LocalKeywords";
        string value = EditorPrefs.GetString(key, "");
        string[] data = string.IsNullOrEmpty(value) ? new string[0] : value.Split(',');
        return LocalKeywordCollection.FromStringArray(data);
    }
    
    public static void SetLocalKeywords(string packageName, LocalKeywordCollection localKeywords)
    {
        string key = $"{Application.productName}_{packageName}_LocalKeywords";
        string[] data = localKeywords.ToStringArray();
        string value = string.Join(",", data);
        EditorPrefs.SetString(key, value);
    }
    
    public static string[] GetFilterShaderNames(string packageName)
    {
        string key = $"{Application.productName}_{packageName}_FilterShaderNames";
        string value = EditorPrefs.GetString(key, "");
        return string.IsNullOrEmpty(value) ? new string[0] : value.Split(',');
    }
    
    public static void SetFilterShaderNames(string packageName, string[] filterNames)
    {
        string key = $"{Application.productName}_{packageName}_FilterShaderNames";
        string value = string.Join(",", filterNames);
        EditorPrefs.SetString(key, value);
    }
}