using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

public class PathSelector : VisualElement
{
    private TextField _pathField;
    private Button _selectButton;
    private Button _removeButton;
    public Action<string> OnSaveEvent;
    public Action OnRemoveEvent;
  
    public string SelectedPath
    {
        get => _pathField.text;
        set => _pathField.value = value;
    }

    public PathSelector(string dpath, bool hasRemoveBtn = true, string field = "路径")
    {
        // 创建 UI 元素
        _pathField = new TextField { label = field };
        _pathField.style.flexGrow = 1;
        _selectButton = new Button { text = "选择路径" };
        _selectButton.style.width = 80;
        _removeButton = new Button { text = "移除" };
        _removeButton.style.width = 40;
        style.flexDirection = FlexDirection.Row;
        style.justifyContent = Justify.FlexStart; // 设置按钮靠右
        
        SelectedPath = dpath;

        // 添加点击事件
        _selectButton.clicked += () =>
        {
            // 打开文件选择对话框
            string path = EditorUtility.OpenFolderPanel("选择文件夹", "Assets", "Assets");

            // 更新路径
            if (!string.IsNullOrEmpty(path))
            {
                path = GetAssetDatabasePathFromRelativePath(path);
                SelectedPath = path;
                if (OnSaveEvent != null) OnSaveEvent.Invoke(SelectedPath);
            }
        };
        // 添加点击事件
        _removeButton.clicked += () =>
        {
            if (OnRemoveEvent != null) OnRemoveEvent.Invoke();
        };
        

        // 将 UI 元素添加到组件
        Add(_pathField);
        Add(_selectButton);
        if (hasRemoveBtn)
        {
            Add(_removeButton);
        }
    }
    
    /// <summary>
    /// 由Unity资源的相对路径获取资源的AssetDatabase路径。
    /// 仅用于编辑器。
    /// </summary>
    /// <param name="assetRelativePath">Unity资源文件的相对路径。</param>
    /// <param name="callerFilePath">请勿传入此参数。</param>
    /// <returns></returns>
    public static string GetAssetDatabasePathFromRelativePath(string assetRelativePath, [System.Runtime.CompilerServices.CallerFilePath] string callerFilePath = null)
    {
        var callerDirectoryPath = System.IO.Path.GetDirectoryName(callerFilePath);
        var unityAssetRelativePath = System.IO.Path.Combine(callerDirectoryPath, assetRelativePath);
        var unityAssetAbsolutePath = System.IO.Path.GetFullPath(unityAssetRelativePath);
        var unityAssetEditorPath = $"Assets{unityAssetAbsolutePath.Replace("\\", "/").Replace(Application.dataPath, null)}";
        return unityAssetEditorPath;
    }

}