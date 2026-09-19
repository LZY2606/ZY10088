# PlateTrace — 多孔板移液/稀释/混匀/读板复盘系统

一套完全本地、可验证的实验室批次复盘工具：录入板型、样本别名、移液事件、枪头
更换与读数后，系统重建**带体积守恒的时间有序转移图**，在出现异常信号时枚举
**所有满足阈值的传播路径及其累计稀释区间**，并允许用户在**候选层**提出污染假设
（某次吸液携带、某批稀释液受影响、一次性枪头污染），评估它们能解释哪些异常孔、
又会额外预测哪些正常孔异常。

- 不依赖任何云端账号或外部数据库：数据就是数据目录下的几个 JSON/JSONL 文件。
- 原始操作日志只增不改；纠正以“带理由的替代事件”形式追加，工作投影应用替代，
  已发布的旧结论永不变写。
- 未知体积是**区间的未知边界（null/?），永远不会被当作 0**。
- 批量提交以一次提交为边界：要么全部事件生效，要么日志保持原状。
- 后台评估可崩溃恢复；作业用稳定幂等标识去重，结果不会重复记入。

## 技术栈与目录

- .NET 10（SDK 10.0.x），ASP.NET Core Minimal API + 原生 HTML/CSS/JS 单页。
- `src/PlateTrace.Core`：领域模型、规则引擎、文件存储、后台作业（无外部依赖）。
- `src/Web`：REST API、静态页面、后台作业轮询、演示数据播种。
- `tests/PlateTrace.Core.Tests`：xUnit 测试（32 个），含内存存储与真实文件存储。

```
src/PlateTrace.Core/
  Models/      Interval/WellRef/Units/事件/批次/投影/假设/结论/导出/作业
  Store/       ITraceStore、FileTraceStore（JSONL + 原子 temp+move）、格式版本
  Engine/      ProjectionBuilder、EventApplier、SubmissionValidator、
               PathTracer（路径与累计稀释区间）、HypothesisEngine、TraceService
  Jobs/        JobRunner（Pending/Running/Completed，恢复 + 恰好一次结果）
src/Web/
  Program.cs           DI、启动播种、静态文件
  ApiEndpoints.cs      /api/* REST 端点
  BackgroundJobService.cs  作业队列轮询与启动恢复
  DemoSeeder.cs        首次运行写入两板连续稀释演示
  wwwroot/             index.html / styles.css / app.js
```

## 快速开始

```bash
dotnet restore
dotnet test --nologo
dotnet run --project src/Web --urls http://127.0.0.1:5216
```

打开 <http://127.0.0.1:5216>。首次启动会自动写入一份演示数据（P1/P2 两板、
稀释液批次 DIL-A、连续两跳 20 uL 移液、正常/异常读数各一）。页面右上角
“重置为演示数据”可清空数据目录重放。

数据目录默认是 `src/Web/App_Data`，可用环境变量 `PLATE_TRACE_DATA` 指定其它位置。

## 业务规则摘要

孔身份是 `(PlateId, Well)` 复合标识；不同板上的 `A1` 永远是两个孔。

- **体积守恒**：载入/加试剂增加体积区间，移液在“最大吸液量 ≤ 最小可用体积”时
  才可行，否则产生 `ASpirate_EXCEEDS_VOLUME` 证据错误且整批拒绝；源孔扣除、目标孔增加。
- **时间**：事件时间必须全局单调（可相等，按提交序号消歧）；倒序立即证据错误。
- **一次性枪头**：一个 `tipId` 只能安装一次、使用一次；重复使用或弃用后再用分别
  产生 `TIP_REUSED` / `TIP_DISCARDED_REUSED`。
- **冻结**：板冻结后拒绝一切液体变更（仍允许读数）；试剂批次可冻结为具名版本。
- **读数**：单位必须在内置显式单位表中；两个来源先按显式换算进入规范单位再比较，
  相对偏差超过 15% 产生 `READING_CONFLICT`。
- **纠正**：`Correction` 引用原事件 ID 且必须带理由；原事件保留在原始日志中，
  工作投影用替代事件顶替原位置重新计算。
- **路径**：仅沿时间递增的有向边枚举简单路径（不重复经孔），累计稀释为各边
  转移比例的区间乘积；上界未知（?）的路径始终保留，默认阈值 `1e-6`，可在页面调整。
- **结论发布**：引用的每块板与每个试剂批次都必须已冻结；发布时内嵌完整导出快照
  （输入、规则版本、事件顺序、路径、区间计算、假设版本）。之后任一相关上游纠正
  会为该结论追加一条 `NeedsReview` 新修订，旧 `Published` 修订与其快照保持不变。

### 假设评估（候选层）

- `CarryOver`：指定一次转移事件，污染物浓度区间 × 携带比例区间注入该边目标孔，
  再沿后续路径传播。
- `ContaminatedReagent`：指定试剂批次，每次 `AddReagent` 都是一个注入点。
- `ContaminatedTip`：指定枪头，其每次参与的转移都按携带比例注入。

每个异常孔被分类为 **必然解释 / 区间相容 / 无法解释**；同时列出“读数正常却被
预测异常”和“未读但被预测”的孔，每条结论可展开传播路径与区间计算。

## 持久化格式与兼容策略

数据目录（默认 `src/Web/App_Data/`）内容：

| 文件 | 内容 | 写入方式 |
| --- | --- | --- |
| `format.json` | 格式名与主/次版本 | 仅初始化时写一次 |
| `batches.jsonl` | 每行一个批次（含接受/拒绝结果、事件、证据错误、幂等键、规则版本） | 每批一次原子重写 |
| `hypotheses.jsonl` | 假设修订（编辑产生新版本，旧版本保留） | 原子重写 |
| `conclusions.jsonl` | 结论修订（Published / NeedsReview 追加） | 原子重写 |
| `jobs.json` | 后台作业状态、尝试次数、结果与指纹 | 整体序列化后原子重写 |
| `.lock` | 同机跨进程写锁文件 | 仅用于互斥 |

**原子性**：每次写盘都先写同目录临时文件，再在跨进程锁内 `File.Move(overwrite)`
发布；崩溃时只会保留“旧文件”或“完整新文件”。一次批次提交是单次写操作，因此
要么整批（含事件）落盘，要么只有拒绝记录，日志不会出现半批。

**格式版本与兼容策略**：

- 版本字符串为 `plate-trace-store/<major>.<minor>`（当前 `1.0`），记录在
  `format.json`，导出包中也有 `formatVersion`。
- **主版本**变化（1.x → 2.x）可能调整文件结构，需要迁移；旧主版本软件遇到
  更高主版本数据会明确拒绝启动。
- **次版本**变化只允许新增可选字段或新增文件，同主版本软件可继续读取。
- 所有 JSON 使用 camelCase、枚举以字符串存储、UTF-8、无注释；未知字段在读取时
  保留容忍（前向兼容），关键的不可变标识（事件 ID、批次 ID、作业 ID）永不复用。

`GET /api/export` 返回完整 `ExportBundle`，结论发布时同样内嵌一份；其中包含
批次原始日志、板/孔状态、转移边、试剂边、全部阈值内路径与区间、读数换算比较、
假设版本、评估结果与结论修订，可离线复核或归档。

## REST API 速览

- `GET /api/state`：当前工作投影（板、孔区间、枪头、证据错误、纠正）。
- `GET /api/timeline`：原始批次日志 + 工作时间线（含替代标记）。
- `POST /api/batches`：原子提交一批事件；相同 `idempotencyKey` 安全重放。
- `GET /api/graph?minFraction=...`：转移边与全部阈值内路径。
- `GET /api/wells/{plate}/{well}/paths`：进入某孔的路径与累计稀释区间。
- `GET /api/readings/compare`：多来源读数显式换算后的一致性比较。
- `POST /api/hypotheses`：新增假设修订并入队后台评估（202 + jobId）。
- `GET /api/jobs`、`POST /api/jobs/pump`：作业状态查看与立即处理。
- `POST /api/conclusions`：发布结论（冻结不足时 422 + 证据错误）。
- `GET /api/conclusions`：全部结论修订。
- `GET /api/export`：完整导出包。
- `POST /api/reset-demo`：清空并重放演示数据。

## 测试

```bash
dotnet test --nologo
```

覆盖：未知体积非零与区间运算、显式单位换算、跨板同坐标不混淆、超量吸液整批
回滚、一次性枪头复用/弃用复用、时间倒序、冻结保护、布点越界、路径枚举与累计
稀释区间阈值、三类假设的解释与额外预测、不同单位读数一致/冲突、幂等重放、
纠正只影响工作投影、发布冻结门禁、纠正触发 NeedsReview 而旧快照不变、作业
崩溃恢复恰好一次、文件存储重建与拒绝批次保留、导出包内容。
