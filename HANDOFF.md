# dsh++ 开发交接

更新时间：2026-11-13（Asia/Shanghai）

## 当前状态

- 项目：`F:\dh talk\dsh-plus-plus`
- Git 分支：`main`
- Git HEAD：`5c03f0d Check plugin kernel requirements before updating and add the next runtime channel`
- 工作区：干净
- 当前代码版本：`3.7.0`
- GitHub：`https://github.com/Jensen-Yao/dsh-plus-plus`
- 最新 Release：`v3.5.5`（3.6.0 / 3.7.0 尚未推送 / 发布）
- 固定安装入口：`F:\SelfSoftware\dsh++\dsh-plus-plus.exe`
- 当前运行版本：`3.7.0.0`（旧版备份：`backups\dsh-plus-plus-3.5.5.exe`、`backups\dsh-plus-plus-3.6.0.exe`）

## dsh++ 已完成能力

- 管理 DSH Web 服务生命周期，支持启动、停止、重启和远程连接。
- 管理官方桌面版，支持启动、停止、重启、Web 配置同步和启动验证。
- 插件清单展示、Profile 筛选、来源诊断、锁文件检查、聚合子包发现和恢复点。
- 插件更新支持声明内更新、指定版本和“升级到最新”。
- 3.7.0：插件检查与更新前核对目标版本的 DSH 内核要求（peerDependencies / dsh.engines.dsh），不满足时状态、详情、提示与确认框均给出范围与当前内核，安装前硬性拦截（不停服、不改依赖）；内核通道新增 next（RC）与精确版本，CLI 新增 `--runtime-update`。
- 3.6.0：“升级到最新”可选固定精确版本或浮动 `^` 声明（默认浮动，0.x 包提示小版本锁定）；状态区分“固定”与“范围外”并带声明原文；悬停详情与选中提示解释声明内最高、远端 latest 及解除方法；帮助新增“声明与版本”一节。
- 本地 `link:` / `file:` 插件不会被批量更新静默替换。
- 本地插件切换到 npm 同名包前会核对两边的 `repository`，来源不一致时拒绝切换。
- 官方桌面版更新读取 `app-update.yml`，校验下载大小与 SHA-512，然后调用官方安装器并复核 `app.asar` runtime 版本。
- 失败诊断包含阶段、目标、命令、退出码、HTTP 状态、下载大小、SHA-512、安装后版本和恢复结果。
- 会话记录栏只读读取官方 DSH 会话目录和缓存，不改写会话数据。
- 液体玻璃材质、彩虹徽标、双主题、健康检查和日志导出。

## 当前本机版本

| 项目 | Web | Desktop |
| --- | --- | --- |
| DSH runtime | `0.2.2-rc.2`（2026-11-13 经 dsh++ `--runtime-update next` 升级） | 官方桌面版 `0.2.2-rc.1` |
| `@linxin666/dsh-web-all` | `0.4.16` | `0.4.16` |
| `@dickpy/dsh-imagegen` | `1.16.0` | `1.16.0` |
| `dsh-builtin-browser` | `0.5.8` | `0.5.8` |
| `dsh-context` | `0.83.0` | `0.83.0` |
| `dshmarket` | `1.73.16` | `1.73.16` |
| `dsh-side-chat` | 本地链接，源码在 `F:\\dh talk\\dsh-side-chat` | 同上 |
| `dsh-model-palette` | 本地 tgz，`0.19.1-final` | 本地 tgz，`0.19.1` |
| Browser bridge | 本地链接，源码在 `C:\\Users\\18052\\.dsh\\dsh-browser` | 同上 |

## 验证结果

- dsh++ 自检（3.7.0）：`109` 项通过（104 + 5 项内核要求新用例）。注意：`shortened redacted composition errors do not overflow` 一例偶发时序失败（取消窗口敏感），重跑即过，待单独修复。
- 内核升级（2026-11-13）：`--runtime-update next` 将 DSH 内核 `0.2.2-rc.1` → `0.2.2-rc.2`，组合检查通过，备份保留；升级后 web 服务正常（页面 HTTP 401 为无凭据 curl 的预期认证响应，bridge `/ext/bridge-config` HTTP 200）。
- Web 插件检查（内核 rc.2 后）：`39` 项，`0` 失败，`0` 来源异常；`@linxin666/dsh-web-all 0.4.18` 内核要求已满足，不再拦截。
- web profile 的 `@deepseek-ai/dsh-client-store` / `dsh-client-ui-primitives` 声明保留 `0.2.2-rc.1`：内核 rc.2 安装目录未携带这两个包（伴随包同步只采用内核目录实际提供的版本），npm next 通道有 rc.2，如需对齐可手动处理——不是故障。
- 待处理更新（用户自行决定）：`dsh-context 0.83.1 → 0.84.0`（^0.83.0 范围外，可「升级到最新」）；`dsh-web-billing` Git HEAD 有新提交待同步。
- dsh++ 健康检查：`0 错误`。
- render-check（3.6.0）：`70` 张，`0` 按钮裁切，输出在 `F:\temp\dshpp-ui-360`。
- 官方桌面版：`0.2.2-rc.1`，窗口和进程验证通过（官方更新通道未发布 rc.2）。

## 重要路径

- DSH Web 安装：`F:\\DeepSeek harness\\dsh-app`
- Web profile：`C:\\Users\\18052\\.dsh\\profiles\\web`
- Desktop profile：`C:\\Users\\18052\\.dsh\\profiles\\desktop`
- dsh++ 状态目录：`C:\\Users\\18052\\AppData\\Roaming\\dsh-control`
- dsh++ 本地安装：`F:\\SelfSoftware\\dsh++`
- Side Chat 源码：`F:\\dh talk\\dsh-side-chat`
- Browser bridge 源码：`C:\\Users\\18052\\.dsh\\dsh-browser`
- Model palette 源码：`F:\\dsh-model-palette`

## 已知限制

- DeepSeek 模型额度耗尽时，Side Chat 的摘要或 handoff 会返回 429；这属于模型额度，不是插件加载错误。
- `dsh-model-palette` 和 Browser bridge 是本地来源，npm registry 没有对应发布包，控制台会显示“npm 未发布 / 本地维护”。
- Web 和 Desktop 共用 DSH 数据根目录；任务看板存在全局单写锁，同时运行两端时其中一端可能降级。
- Desktop profile 由官方 Electron 应用管理，修改 desktop profile 优先通过 dsh++ 的“从 Web 同步配置”。

## 下一步建议

1. 继续开发前先读取本文件和仓库 `README.md`。
2. 修改插件或 DSH 版本前创建新的恢复点，不删除 profile lockfile。
3. 更新后运行：

```powershell
dotnet build dsh-plus-plus.csproj -c Release --no-restore
.\\bin\\Release\\net8.0-windows\\dsh-plus-plus.exe --self-test
.\\bin\\Release\\net8.0-windows\\dsh-plus-plus.exe --health
```

4. 如果修改 Side Chat，先在 `F:\\dh talk\\dsh-side-chat` 运行 `npm run typecheck`、`npm test` 和 `npm run build`，再重启 DSH Web 验证。
