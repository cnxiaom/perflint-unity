# 启用脚本分析模块（Roslyn）

脚本 GC / 每帧分配诊断（`ScriptGcScanner`）需要 Roslyn 编译器 API（`Microsoft.CodeAnalysis`）。
Unity 内部虽然用了 Roslyn，但**不对外暴露可引用的 API**，因此需手动引入 NuGet DLL，并打开一个编译宏。

> 在完成下面步骤前，本模块（`PerfLint.Editor.Roslyn` 程序集）因 `defineConstraints: PERFLINT_ROSLYN`
> **不会被编译**，核心包正常工作、不受任何影响。

---

## 最省心：面板「一键启用」

打开 **Tools ▸ PerfLint ▸ Scan Project**，若脚本分析未启用，顶部会有黄条提示与 **一键启用脚本分析** 按钮。
点它即自动完成：把内置的 `Microsoft.CodeAnalysis(.CSharp).dll` 连同预置导入设置（仅 Editor、关 Validate
References）拷入 `Assets/Plugins/PerfLintRoslyn/`、加入 `PERFLINT_ROSLYN` 宏、触发重编译。编译完重新扫描即可。

> 仅当包内 `Editor/Scripting/RoslynDlls~/` 已内置 DLL 时按钮才是"一键启用"；否则按钮变为"查看启用步骤"，
> 按下面 NuGet 路径手动安装。下面两节为手动/排错参考。

---

## 推荐路径：NuGetForUnity（最省心）

1. 安装 [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity)（通过 Package Manager 的 Git URL）。
2. 菜单 **NuGet ▸ Manage NuGet Packages**，搜索并安装 **`Microsoft.CodeAnalysis.CSharp`**。
   - 建议版本 **4.x**（如 4.8.0）。它依赖 `Microsoft.CodeAnalysis.Common`、`System.Collections.Immutable`、
     `System.Reflection.Metadata` 等，NuGetForUnity 会一并拉取。
3. 打开编译宏：**Project Settings ▸ Player ▸ Scripting Define Symbols**，加入 `PERFLINT_ROSLYN`。
4. 等待重新编译。重新打开 **Tools ▸ PerfLint ▸ Scan Project**，结果里应出现
   `Script GC / Per-Frame Allocations (Roslyn)` 来源的 `PERF.UPD*` / `PERF.GC*` 条目。

---

## 处理依赖冲突 / 缺依赖（如遇编译或运行报错）

### A. 缺依赖 / 版本过旧（`TypeLoadException`）

若**点 Scan 时**报：

```
TypeLoadException: Could not load type of field 'Microsoft.CodeAnalysis.CSharp.LocalScopeBinder:_locals'
  due to: Could not load file or assembly 'System.Collections.Immutable, Version=7.0.0.0 …'
```

说明 Roslyn 的**支持依赖缺失或版本对不上**——只放 `Microsoft.CodeAnalysis(.CSharp).dll` 是不够的。
典型：Roslyn **4.x** 需要 `System.Collections.Immutable 7.0`，而 **Unity 2021.3 没带该版本**。

修法（任选）：
- **首选：用上面的 NuGetForUnity 路径**，它会把 `System.Collections.Immutable` / `System.Reflection.Metadata` /
  `System.Runtime.CompilerServices.Unsafe` / `System.Memory` 等整条闭包按匹配版本一起装好。
- 或手动补齐：从对应 NuGet 包取出 Roslyn 版本要求版本的上述 DLL，一并放入插件目录。
- 或**换用与你的 Unity 匹配的 Roslyn 版本**（老 Unity 配 3.x 线往往比 4.x 少踩依赖坑）。

> 注：自 `[0.14.7]` 起，这类加载失败**不会再让整次扫描崩溃**——`ScanRunner` 跳过加载不了的可选模块类型，
> 核心诊断照常跑，只是 GC/每帧分配/CPU 规则不生效（顶部降级提示仍在）。

### B. 版本重复（“在多个程序集中定义” / “Multiple precompiled assemblies with the same name”）

> 注：自 `[0.15.1]` 起，**面板「一键启用」已内置冲突感知**——拷贝内置依赖前会扫描工程 `Assets` 下是否已有同名
> DLL（很多第三方包带 `System.Runtime.CompilerServices.Unsafe` 等垫片）。已存在且**版本够用**则跳过、保留你工程
> 现有版本；已存在但**现有版本过旧**（低于 Roslyn 要求，按程序集版本判定）则**中止启用并提示具体文件与版本**，
> 不会留下半装状态。只扫 `Assets`（不扫只读的 `Packages`）。下面是需要手动处理时的参考。

Unity 自身已带部分程序集，与 NuGet 拉来的版本可能重复，典型报错是某类型“在多个程序集中定义”。
常见需要**从工程中删除（或在其 Inspector 里取消 Any Platform）** 的重复 DLL：

- `System.Collections.Immutable.dll`
- `System.Reflection.Metadata.dll`
- `System.Runtime.CompilerServices.Unsafe.dll`
- `System.Memory.dll` / `System.Threading.Tasks.Extensions.dll` / `System.Numerics.Vectors.dll`

处理原则：**保留 Unity 已提供的版本，删掉 NuGet 重复的那份**；只留下 `Microsoft.CodeAnalysis.dll`
与 `Microsoft.CodeAnalysis.CSharp.dll` 是必需的。

对每个手动放入的 DLL，在其 Inspector 里确认：
- ✅ 平台 **仅勾 Editor**（本模块是 Editor-only）。
- ✅ 取消 **Validate References**（避免 strong-name 校验问题）。
- ⚠️ 不要把 `Microsoft.CodeAnalysis.*` 当作 **Roslyn analyzer** 标签导入——它是我们*引用*的库，
  不是要 Unity 在编译期自动运行的分析器。

---

## 手动放置 DLL（不想用 NuGetForUnity 时）

1. 从 nuget.org 下载 `Microsoft.CodeAnalysis.CSharp`（.nupkg 本质是 zip），取出 `lib/netstandard2.0/`
   下的 `Microsoft.CodeAnalysis.dll`、`Microsoft.CodeAnalysis.CSharp.dll`，以及其依赖包里的
   `System.Collections.Immutable.dll`、`System.Reflection.Metadata.dll` 等。
2. 放入工程 `Assets/Plugins/PerfLintRoslyn/`（或任意 Editor-only 插件目录）。
3. 按上一节设置每个 DLL 的导入选项（仅 Editor、取消 Validate References）。
4. 加入 Scripting Define `PERFLINT_ROSLYN`。

---

## 卸载 / 临时关闭

移除 Scripting Define `PERFLINT_ROSLYN` 即可——本模块停止编译，其余诊断照常工作。
