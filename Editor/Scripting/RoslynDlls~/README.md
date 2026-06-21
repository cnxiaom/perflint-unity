# RoslynDlls~ —— 内置 Roslyn DLL 的休眠目录（面向打包者）

> 目录名以 `~` 结尾：**Unity 完全忽略本目录**——这里的 DLL 不会被编译、不产生程序集冲突，
> 处于"休眠"状态。用户在 PerfLint 面板点「一键启用脚本分析」时，`RoslynSetup.Install()` 用纯文件 IO
> 把 DLL（连同预置 `.meta`）拷到工程 `Assets/Plugins/PerfLintRoslyn/`，再加 `PERFLINT_ROSLYN` 宏并重编译。

## 打包者一次性步骤：放入两个 DLL

把 `Microsoft.CodeAnalysis.dll` + `Microsoft.CodeAnalysis.CSharp.dll`（NuGet 包 `Microsoft.CodeAnalysis.CSharp`
的 `lib/netstandard2.0/`）放进**本目录**，与各自的 `.meta` 并列（`.meta` 已预置导入设置，勿删）。

> ⚠️ **依赖闭包不是"只放两个"那么简单**（这点之前写错了，实测会坑）。Roslyn 的支持依赖
> （`System.Collections.Immutable`、`System.Reflection.Metadata`、`System.Runtime.CompilerServices.Unsafe`、
> `System.Memory` 等）**Unity 不一定自带、或自带版本对不上**——典型：Roslyn **4.x** 需要 `Immutable 7.0`，
> 而 Unity 2021.3 没带 7.0，运行时直接 `TypeLoadException`（"Could not load … System.Collections.Immutable, Version=7.0.0.0"）。
>
> 两条可靠路线：
> 1. **强烈推荐让用户走 NuGetForUnity**（SETUP-ROSLYN.md 首选路径）——它会把整条依赖闭包按匹配版本拉齐，
>    再按「处理依赖冲突」删掉与 Unity 重复的那几个。比"随包塞 DLL"稳得多。
> 2. 若坚持随包内置，必须把**整条依赖闭包**（上述全部）按 Roslyn 版本要求的版本一起放进本目录，
>    并针对目标 Unity 版本验证（不同 Unity 自带的 BCL 版本不同，闭包要相应调整）。
>
> 简言之：随包内置只在你为**特定 Unity 版本**配齐并验证过闭包时才可靠；否则首选引导 NuGetForUnity。

## 为什么不直接放进 Assets / 包内可编译目录

- 放进可编译目录 → 没装也会被编译/校验，可能与 Unity 自带程序集冲突，破坏"未启用时零影响"的承诺。
- `~` 休眠 + 启用时再拷出，是隔离风险的最稳妥方式（同 DOTween 把模块随包发、Setup 时再激活的思路）。

## 预置的 .meta 做了什么

`*.dll.meta` 已设：**仅 Editor 平台**、**关闭 Validate References**（避免 strong-name 校验问题）。
拷到工程后 Unity 直接沿用这些设置，用户无需在 Inspector 里逐个手动配置。
