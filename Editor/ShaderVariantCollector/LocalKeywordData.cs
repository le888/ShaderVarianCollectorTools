using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class LocalKeywordData
{
    /// <summary>
    /// Shader名称
    /// </summary>
    public string ShaderName;

    /// <summary>
    /// 局部关键字
    /// </summary>
    public string Keyword;

    public LocalKeywordData(string shaderName, string keyword)
    {
        ShaderName = shaderName;
        Keyword = keyword;
    }
}

[Serializable]
public class LocalKeywordCollection
{
    /// <summary>
    /// 局部关键字列表
    /// </summary>
    public List<LocalKeywordData> LocalKeywords = new List<LocalKeywordData>();

    /// <summary>
    /// 添加局部关键字
    /// </summary>
    public void AddLocalKeyword(string shaderName, string keyword)
    {
        // 检查是否已存在相同的shader名称和关键字
        foreach (var data in LocalKeywords)
        {
            if (data.ShaderName == shaderName && data.Keyword == keyword)
            {
                return; // 已存在，不添加
            }
        }

        // 添加新的局部关键字数据
        LocalKeywords.Add(new LocalKeywordData(shaderName, keyword));
    }

    /// <summary>
    /// 移除局部关键字
    /// </summary>
    public void RemoveLocalKeyword(string shaderName, string keyword)
    {
        LocalKeywords.RemoveAll(data => data.ShaderName == shaderName && data.Keyword == keyword);
    }

    /// <summary>
    /// 获取指定Shader的所有局部关键字
    /// </summary>
    public List<string> GetKeywordsForShader(string shaderName)
    {
        List<string> keywords = new List<string>();
        foreach (var data in LocalKeywords)
        {
            if (data.ShaderName == shaderName)
            {
                keywords.Add(data.Keyword);
            }
        }
        return keywords;
    }

    /// <summary>
    /// 转换为字符串数组，用于存储
    /// </summary>
    public string[] ToStringArray()
    {
        List<string> result = new List<string>();
        foreach (var data in LocalKeywords)
        {
            result.Add($"{data.ShaderName}:{data.Keyword}");
        }
        return result.ToArray();
    }

    /// <summary>
    /// 从字符串数组创建LocalKeywordCollection
    /// </summary>
    public static LocalKeywordCollection FromStringArray(string[] data)
    {
        LocalKeywordCollection collection = new LocalKeywordCollection();
        if (data == null || data.Length == 0)
        {
            return collection;
        }

        foreach (var item in data)
        {
            string[] parts = item.Split(':');
            if (parts.Length == 2)
            {
                collection.AddLocalKeyword(parts[0], parts[1]);
            }
        }

        return collection;
    }
}