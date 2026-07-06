using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 扫描项目中 VolumeProfile 使用的后处理效果，映射到 shader 关键字
/// </summary>
public static class PostProcessScanner
{
    public class ScanResult
    {
        public List<EffectInfo> usedEffects = new List<EffectInfo>();
        public List<EffectInfo> unusedEffects = new List<EffectInfo>();
        public int totalProfiles;
        public int totalComponents;
    }

    public class EffectInfo
    {
        public string effectName;
        public List<string> keywords;
        public List<string> profilePaths;
    }

    // 效果类型 → shader 关键字映射
    private static readonly Dictionary<string, List<string>> EffectKeywordMap = new Dictionary<string, List<string>>
    {
        { "Bloom", new List<string> { "_BLOOM_LQ", "_BLOOM_HQ", "_BLOOM_LQ_DIRT", "_BLOOM_HQ_DIRT" } },
        { "Tonemapping", new List<string> { "_TONEMAP_ACES", "_TONEMAP_NEUTRAL" } },
        { "DepthOfField", new List<string> { "_DOF_GAUSSIAN", "_DOF_BOKEH" } },
        { "FilmGrain", new List<string> { "_FILM_GRAIN" } },
        { "ChromaticAberration", new List<string> { "_CHROMATIC_ABERRATION" } },
        { "Vignette", new List<string> { "_VIGNETTE" } },
        { "LensDistortion", new List<string> { "_LENS_DISTORTION" } },
        { "MotionBlur", new List<string> { "_MOTION_BLUR" } },
        { "PaniniProjection", new List<string> { "_PANINI_PROJECTION" } },
        { "ColorAdjustments", new List<string> { "_COLOR_ADJUSTMENTS" } },
        { "WhiteBalance", new List<string> { "_WHITE_BALANCE" } },
        { "ShadowsMidtonesHighlights", new List<string> { "_SHADOWS_MIDTONES_HIGHLIGHTS" } },
        { "ChannelMixer", new List<string> { "_CHANNEL_MIXER" } },
        { "ColorCurves", new List<string> { "_COLOR_CURVES" } },
        { "SplitToning", new List<string> { "_SPLIT_TONING" } },
        { "LiftGammaGain", new List<string> { "_LIFT_GAMMA_GAIN" } },
    };

    /// <summary>
    /// 获取所有已知的效果关键字
    /// </summary>
    public static HashSet<string> GetAllEffectKeywords()
    {
        var all = new HashSet<string>();
        foreach (var kvp in EffectKeywordMap)
            foreach (var kw in kvp.Value)
                all.Add(kw);
        return all;
    }

    /// <summary>
    /// 扫描项目中所有 VolumeProfile，返回使用情况
    /// </summary>
    public static ScanResult Scan()
    {
        var result = new ScanResult();
        var usedEffectMap = new Dictionary<string, EffectInfo>(); // effectName -> EffectInfo

        // 扫描所有 VolumeProfile 资源
        string[] guids = AssetDatabase.FindAssets("t:VolumeProfile");
        result.totalProfiles = guids.Length;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null) continue;

            foreach (var component in profile.components)
            {
                if (component == null || !component.active) continue;

                result.totalComponents++;
                string effectName = component.GetType().Name;

                if (!usedEffectMap.TryGetValue(effectName, out var info))
                {
                    info = new EffectInfo
                    {
                        effectName = effectName,
                        keywords = EffectKeywordMap.ContainsKey(effectName)
                            ? EffectKeywordMap[effectName]
                            : new List<string>(),
                        profilePaths = new List<string>()
                    };
                    usedEffectMap[effectName] = info;
                }

                if (!info.profilePaths.Contains(path))
                    info.profilePaths.Add(path);
            }
        }

        // 分类已使用和未使用
        foreach (var kvp in EffectKeywordMap)
        {
            if (usedEffectMap.ContainsKey(kvp.Key))
                result.usedEffects.Add(usedEffectMap[kvp.Key]);
            else
                result.unusedEffects.Add(new EffectInfo
                {
                    effectName = kvp.Key,
                    keywords = kvp.Value,
                    profilePaths = new List<string>()
                });
        }

        // 按名称排序
        result.usedEffects.Sort((a, b) => a.effectName.CompareTo(b.effectName));
        result.unusedEffects.Sort((a, b) => a.effectName.CompareTo(b.effectName));

        return result;
    }

    /// <summary>
    /// 获取未使用效果的所有关键字
    /// </summary>
    public static List<string> GetUnusedKeywords(ScanResult result)
    {
        var keywords = new List<string>();
        foreach (var effect in result.unusedEffects)
            keywords.AddRange(effect.keywords);
        return keywords;
    }
}
