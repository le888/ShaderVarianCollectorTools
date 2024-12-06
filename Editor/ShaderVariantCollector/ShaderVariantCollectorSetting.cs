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
}