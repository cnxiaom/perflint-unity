# PerfLint for Unity

[![English](https://img.shields.io/badge/PerfLint-English%20README-blue)](README.md)

> 装进 Unity 编辑器的资深技术经理。一键扫描项目，定位性能瓶颈与版本迁移阻塞，给出可执行的修复建议 —— 能改的一键改。
>
> **本地优先 · 零遥测 · 你的代码和美术资产永远不会被上传。**
>
> **编辑器里能跑、命令行能跑、CI 里能跑 —— 你的 AI agent 也能通过 MCP 调它。**

**[perflint.dev](https://perflint.dev)** · [文档](https://perflint.dev/docs/) · [博客](https://perflint.dev/blog/)

https://github.com/user-attachments/assets/0261ba42-09d8-437a-b083-539f3b3696b9

*▶ 80 秒演示 —— 一个真实的生产工程：**AssetBundle 重复依赖 337 → 5**，磁盘占用 **1.3 GB → 808 MB**。（也可以在 [perflint.dev](https://perflint.dev/#demo) 上看）*

## 找出拖慢、撑大、搞坏你工程的那些东西

一次扫描，几秒钟，覆盖四个域。每一条问题都带着**严重级别**、精确的 **Locate**（跳到出问题的那一行/那个资产）、一句人话讲清楚它的代价，以及**修复建议** —— 改起来没风险的那些，点一下就替你改完。

检测走的是**确定性规则引擎** —— 可复现、离线、零 token，同一个工程跑多少次结果都一样。扫描不需要注册账号，也没有任何东西离开你的机器。

## 真实的修复。改前改后都量过。

不是效果图 —— 下面全是真实 Unity 工程应用了 PerfLint 给出的修复之后的截图。

**Addressables 去重** —— 杀手锏功能，用 Unity 自带的 Addressables 分析来验：

| 改前 | 改后 |
|:---:|:---:|
| ![Unity Addressables 分析：337 个重复资产，77 个 bundle 合计 1.29 GB](https://perflint.dev/before-after/aadup-before.jpg?v=2) | ![去重后的 Unity Addressables 分析：5 个重复资产，bundle 合计 805 MB](https://perflint.dev/before-after/aadup-after.jpg?v=2) |
| 337 个重复资产 · bundle 1.29 GB | **5** 个 · 805 MB **（−485 MB）** |

**Unity 6 迁移** —— 升级之后水面渲染成洋红的那个 URP 场景：

| 改前 | 改后 |
|:---:|:---:|
| ![Unity 6 / URP 升级后湖面渲染成纯洋红](https://perflint.dev/before-after/mig-water-before.png) | ![同一个场景，水面重新正常渲染](https://perflint.dev/before-after/mig-water-after.png) |
| shader 挂了 → 洋红 | **恢复正常** —— AI Migrate 修完过编译验证 |

**内存** —— Assets 扫描报出来的冗余 cubemap 和超尺寸贴图，用 Unity 自带的 Memory Profiler 来验：

| 显存 | 设备实际占用 |
|:---:|:---:|
| ![Unity Memory Profiler：显存下降 227.9 MB](https://perflint.dev/before-after/memory-graphics.png?v=2) | ![Unity Memory Profiler：常驻内存从 1.48 GB 降到 0.92 GB](https://perflint.dev/before-after/memory-device.png?v=2) |
| 显存 **−227.9 MB** | 常驻内存 **−0.55 GB**（1.48 GB → 0.92 GB） |

→ [更多案例在博客上](https://perflint.dev/blog/)

## 诊断是确定性的，AI 只在它真有用的地方出场

检测由规则引擎完成 —— 可复现、离线、零 token。LLM 只负责把一条问题讲成人话、回答你的追问、生成修复片段。结果稳定，成本可控。

- **🎯 确定性引擎** —— Roslyn 脚本分析 + 资产 / 导入设置 / 工程设置扫描器。同一个工程，每次跑出同一批问题。出报告不需要联网跑一圈。
- **🛠️ 安全的一键修复** —— 导入设置类修复支持批量应用，带预览、可完整 Undo。控制权在你手里，工程不会被悄悄改掉。
- **🤖 带安全网的 AI Fix** —— 脚本级修复写入后会触发编译验证；编译一旦失败自动回滚。发出去的永远只有你选中的那一段代码。

**Free —— 找出并看懂每一个问题。** 完整扫描、全部问题、可分享的健康报告、文字版修复指引，外加每日一份 AI Fix / Explain 额度。

**Pro —— 一次修一整个工程。** 一键修复、批量修复、按目标优化，整文件迁移助手，更多 AI 额度，以及自带 API key。→ [在 perflint.dev 上看方案](https://perflint.dev/#pricing)

<a name="agent"></a>

## 你的 AI agent 能直接调它

PerfLint 把自己的诊断能力注册成 Unity Pipeline 命令，于是它们直接出现在 Unity 自家 MCP server 的工具表里 —— **不需要另外装一个 MCP server**。把 agent 指过去，用大白话提要求，它读到的是实测数据，而不是靠翻你的文件猜。

```
unity mcp configure claude-code
#        ...或者: cursor | codex | copilot-cli | windsurf | zed
```

在 Unity 官方 Viking Village 示例工程上的一段真实会话，未做删改：

> **&gt;** 用 PerfLint 把安全的问题修一下。
>
> `perflint_scan` → 得分 42 · 等级 D · 852 条问题 · 204 条可自动修
>
> *"动这 95 个模型导入设置之前先确认一件事 —— 关掉 Read/Write 会让任何在运行时读取 mesh 数据的脚本挂掉。[…] 唯一命中的是 `WaterSystemFeature.cs`，它是在**写**一个程序化生成的 mesh —— 那是运行时 Mesh，不受导入设置的 Read/Write 影响。没有任何代码读取导入模型的几何数据，所以这 95 个 FBX 关掉它是安全的。"*
>
> `perflint_fix` → **204 条已应用 · 0 条失败 · 等级 D → C（得分 42 → 63）**
>
> 另有三条被交回来让人自己判断 —— 一张重复的法线贴图（合并会重接引用）、一个 shader 丢失的材质、一处废弃 API 调用。全部可以 `Edit ▸ Undo` 撤销。

总耗时：**1 分 41 秒。**

**中间那一步才是重点。** 编辑器里的 *Fix All* 会应用完全相同的 204 条改动，点起来还更快 —— 但一个按钮读不了你的代码，它只能假设没人在运行时读 mesh 数据。agent 是先查过才动手的。

你的 agent 能拿到六个命令：`perflint_scan`、`perflint_list_findings`、`perflint_optimize_plan`、`perflint_optimize_apply`、`perflint_fix`、`perflint_gate`。扫描始终在本地、不上传任何东西 —— 但 agent 收到的问题清单里含资产路径和行号，这些会进到你接的那个 agent 里。

→ [配置方法、六个工具、以及我们踩过的坑](https://perflint.dev/docs/#agent)

## 诊断域

每条问题都带严重级别、精确位置、代价说明和修复方案。

### 🚀 性能 —— 找出真正吃掉帧的东西
- 未压缩 / 超尺寸贴图、多余的 Read/Write、Sprite mipmap
- 每帧 GC：`Update` 里的 `GetComponent` / `Camera.main` / `FindObjectOfType`、字符串拼接、LINQ、`WaitForSeconds`
- 留在发布版里的 `Debug.Log`、打断合批的材质、SRP instancing 提示
- 撑大内存的 Mesh 与音频导入设置
- Play Mode 性能剖析：把卡顿、每帧 GC、CPU 热点定位到具体脚本

### 📦 资产 —— 把白占包体的东西清出去
- 重复资产（按内容哈希分组），一键选中 + 导出 CSV
- 在 AssetBundle / Addressables 里被打包两遍的资产
- 被裹进包体的未引用资产、爆炸的 shader 变体
- 判定保守、误报率低 —— 报告类条目绝不自动删除

### ⬆️ 迁移 —— 活着走完 Unity 6 升级
- 废弃 / 已移除 API，定位到具体行，附替代写法
- 新旧 Input System 混用、包版本与你的 Unity 版本兼容性
- `manifest.json` 的 preview / legacy 包检查，Unity 6 阻塞项（RenderTargetHandle、洋红 shader）
- Pro 版迁移助手做整文件修复 —— 过编译验证，失败自动回滚

<a name="anywhere"></a>

## 编辑器、命令行、CI 里都能跑

![Unity CLI](https://perflint.dev/blog/cli/unity-cli.png)

从终端驱动 PerfLint。装了 Unity 的 Pipeline 包之后，它直接作用于你**已经开着的那个编辑器**，一句样板代码都不用 —— 不用写编辑器路径、不用 `-batchmode`、不用 `-projectPath`：

```
unity command perflint_scan               # 健康分、等级、问题计数
unity command perflint_list_findings      # 逐条问题（可按规则 / 域 / 严重级别过滤）
unity command perflint_gate --min_score 60
unity command perflint_fix                # 应用安全修复   （加 --dry_run 先预览）
```

在一个当时编译都过不了的工程上完整跑一遍：**0 → 42 → 63，F 到 C** —— 升级阻塞项在编辑器里修掉（那些是代码改动，CLI 不碰），然后 `perflint_fix` 应用 204 条、0 条失败，最后**独立重扫一遍确认**，而不是信 apply 命令自己的汇报。→ [完整过程记录](https://perflint.dev/docs/#ci)

**这些命令你的 AI agent 同样能调** —— 见上面 [你的 AI agent 能直接调它](#agent)。

CI 里也能无头跑 —— 健康分一退步就让构建失败，退出码可以信：

```
Unity -batchmode -projectPath . -executeMethod PerfLint.Ci.PerfLintCli.RunGate -perflintMinScore 60 -logFile -
```

→ [完整的 CLI 与 CI 指南](https://perflint.dev/docs/#ci)

## 安装（Unity Package Manager — Git URL）

`Window ▸ Package Manager ▸ + ▸ Add package from git URL…`，粘贴：

```
https://github.com/cnxiaom/perflint-unity.git
```

或者写进 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "com.perflint.unity": "https://github.com/cnxiaom/perflint-unity.git"
  }
}
```

想锁版本就在后面加 tag，例如 `…perflint-unity.git#v1.0.0`。要求 **Unity 2021.3 及以上**（含 Unity 6）。

## 从安装到修完，几分钟

1. **用 UPM 装。** 按 Git URL 添加包 —— 不用注册，不用登录。
2. **点 Scan。** 打开 **Tools ▸ PerfLint ▸ Scan Project**（`Ctrl/Cmd + Shift + L`），点 **Scan Project**。几秒钟后你会拿到一个 **0–100 的工程健康分**，问题按 **Performance / Assets / Migration / Project Settings** 分组 —— 每条都带严重级别、精确的 **Locate**、一句人话讲清代价，以及修复方案。
3. **修完、发版。** 照着免费的文字指引改，或者用你每天的 AI Fix 额度；再导出一份自包含、可分享的 **HTML 报告**发给团队。想要一键修复、批量修复和整文件迁移，升级 Pro。

更习惯用终端？见上面 [编辑器、命令行、CI 里都能跑](#anywhere)。

## 隐私

所有扫描都在本地跑 —— 你的代码和美术资产永远不会离开你的机器，扫描也不需要账号。
唯一会被发出去的东西，是你**主动**使用可选的 Explain / AI Fix 时，那条问题的文本、或者你选中的那**一段**代码。仅此而已。**零遥测。**

## FAQ

**它会上传我的工程或者源码吗？**
不会。扫描和分析全部在你的编辑器里本地完成。唯一会被发出去的，是你主动用 Explain 或 AI Fix 时，那条问题的元数据、或你选中的那一段代码 —— 经由零日志代理，或者你自己填了 key 的话就直连你的服务商。

**必须有 API key 吗？**
不需要。AI Fix 和 Explain 开箱即用，走你所在方案的 AI 额度。Pro 用户也可以改成填自己的 Claude 或 DeepSeek key，那些请求直连服务商、不限量。确定性扫描、问题清单和健康报告完全不需要 key，也不需要联网。

**支持哪些 Unity 版本？**
Unity 2021.3 及以上，包含 Unity 6。迁移规则是分版本的 —— 不会拿一堆在你这个版本里根本没废弃的 API 来烦你。

**AI Fix 会不会把我代码改坏？**
AI Fix 写入改动后会触发编译，编译失败自动回滚。它的适用范围限定在安全、机械的改动上（比如 API 改名）。仍然建议你先提交一次版本控制。

## 了解更多

- **[文档](https://perflint.dev/docs/)** —— 安装、第一次扫描、CLI 与 CI、隐私说明。
- **[博客](https://perflint.dev/blog/)** —— 案例复盘、Unity 6 迁移指南、深入拆解。

## 许可

从 **Unity Asset Store** 获取的副本适用 Unity Asset Store EULA；从我们网站或 UPM Git URL 获取的副本适用 **[perflint.dev/license](https://perflint.dev/license)**。见 [LICENSE.md](LICENSE.md)。
