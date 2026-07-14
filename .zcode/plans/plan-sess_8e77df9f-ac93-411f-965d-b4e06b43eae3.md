## 修复方案：渲染模式卡 90% + 取消无效 + 重开仍 90%

### 根因
- **主卡死点**：`WaitingDone`(`ShaderVariantCollector.cs:865-875`) 进入时**没有重启计时器**，用的是 `CollectWaitToScene:809` 里 `.Stop()` 后冻结的值（约 500~1000ms）。`WaitingDone` 要求 `> 1000ms` 才前进 → 永远不满足 → 死锁在 0.9。
- **取消无效**：渲染模式 `EditorUpdate`(`:722-917`) 从不读 `_cancelRequested`，只有分析模式读。
- **重开仍 90%**：`_steps`/`_analyzeProgress` 是 `static` 字段；窗口无 `OnDestroy`，关窗不重置状态、不注销 update。
- **无法重启**：`Run:141` `if (_steps != ESteps.None) return;` 卡死后直接 return。

### 改动清单

#### 文件 1：`Editor/ShaderVariantCollector/ShaderVariantCollector.cs`

**A. 新增统一清理方法 `ResetCollectionState()`**（放在 `FinishCollection` 前）
- 注销 `EditorApplication.update -= EditorUpdate`
- 调 `DestroyAllSpheres()` + `DestoryLoadScene()` 清理临时场景资源
- 清零 `_steps=None`、`_analyzeProgress=0`、`_analyzeStatus=""`、`_cancelRequested=false`、`_splitQueue=null`、`_splitShaderNames=null`

**B. 改 `FinishCollection()`**(`:1480-1489`)：保留 `AssetDatabase.Refresh` + 回调逻辑，主体改为调用 `ResetCollectionState()`（回调先缓存到局部变量，重置后再 invoke，避免回调里再读状态）

**C. `EditorUpdate` 顶部加取消检查**（`:724` `if (_steps == None) return;` 之后）：
```
if (_cancelRequested) { Debug.Log("用户取消收集"); ResetCollectionState(); return; }
```
这样渲染模式点"取消搜集"立刻生效。

**D. `WaitingDone` 进入时重启计时器**（`CollectSceneVariants` 无场景分支 `:845-851`）：在 `_steps = ESteps.WaitingDone` 之前加 `_elapsedTime = Stopwatch.StartNew();`，让 1000ms 等待从进入该状态重新计时，**彻底消除死锁**。

**E. `SaveCollection` 加异常保护**(`:877-894`)：把 `SaveShaderVariantCollection(buildSplitQueue: true)` 包进 `try/catch`，catch 里 `Debug.LogError` + `FinishCollection()` + `return`，防止反射调用 Unity 内部 API 偶发抛异常时被 `EditorApplication.update` 吞掉导致每帧重抛、卡在 0.9。

**F. `Run` 开头补重置**(`:167-172`)：追加 `_analyzeProgress = 0f; _analyzeStatus = ""; _cancelRequested = false;`，让重新开始时 UI 干净（防御性）。

#### 文件 2：`Editor/ShaderVariantCollector/ShaderVariantCollectorWindow.cs`

**G. 新增 `OnDestroy()`**：若 `ShaderVariantCollector.IsCollecting`，调 `ShaderVariantCollector.Cancel()`。
- 关窗 → `Cancel()` → 下一帧 `EditorUpdate` 检测到 `_cancelRequested` → `ResetCollectionState()` → 状态全部清零、注销 update。
- 重开窗口时 `_steps == None`、`_analyzeProgress == 0`，显示干净，"开始搜集"按钮可正常点击。

### 不改动的内容
- 不"修复"任何已固化的拼写错误（包名、`Ge*` 方法、`StriphSaderVariants` 等），避免破坏序列化引用。
- 不动分析模式的三个 update（它们已正确处理取消）。
- 不动 `ShaderVariantCollectorSetting.asset` 及任何配置/路径。
- 不改 `FinishSplitCollection` 删除 `_savePath`、写 `shaderNames.txt` 的既有逻辑（只让它继续走 `FinishCollection`）。

### 验证方式
改完后导入 Unity，渲染模式收集应能正常越过 90% 到 100%；卡住时点"取消搜集"立即停止并清零进度；关闭窗口重开显示 0% 且可重新开始。