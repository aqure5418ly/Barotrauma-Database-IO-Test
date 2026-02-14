# Database IO Test 文档总整合（技术细节完整版）

> 目标：在不修改任何原始文档前提下，将当前仓库所有 `.md` 文档中的关键信息进行统一梳理、去重整合、技术细化与决策化表达，便于后续版本规划与实施。

---

## 0. 覆盖范围与来源

本整合文档覆盖以下原始文档：

1. `README.md`
2. `README.zh-CN.md`
3. `README.en.md`
4. `CODEBASE_SUMMARY.zh-CN.md`
5. `REFACTOR_PLAN_SAVE_CONSISTENCY.md`
6. `ae2compare.md`
7. `claude_suggest.md`
8. `gemini_suggest.md`（已补充 Proxy Item 新方案）

---

## 1. 项目定位与版本信息（统一视图）

### 1.1 项目定位

- 项目为 **测试版模组**（test build）。
- 当前可运行主线：**C#（LuaCs）+ XML CustomInterface**。
- `Lua/` 目录保留了原型与后续扩展空间，但当前运行时不依赖 Lua UI。

### 1.2 对外功能定位（README 一致信息）

项目当前已提供完整功能链路：

- 数据库入库设备（手持+固定）
- 终端设备（手持+固定）
  - 分页、搜索、排序、整理（compact）
- 自动补货器（根据规则从数据库拉取物资）
- Fabricator 联动（`DB Fill` 自动填料）

### 1.3 版本信息冲突（文档一致性问题）

- `CODEBASE_SUMMARY.zh-CN.md` 指出内容包版本是 `0.1.1`（基于 `filelist.xml`）。
- `README.zh-CN.md` / `README.en.md` 标题处仍标注 `0.1.0`。

结论：存在文档版本信息漂移，需要在下一次发布前统一版本元信息，避免测试反馈与问题复现时出现基线混乱。

---

## 2. 仓库结构与模块边界（按实现层）

### 2.1 入口与构建元数据

- `filelist.xml`：内容包入口与版本标识。
- `CSharp/RunConfig.xml`：运行配置。
- `CSharp/DatabaseIOTest.buildcheck.csproj`：构建检查工程（net6.0，引用 Barotrauma 相关 DLL）。

### 2.2 C# 核心与常量

- `DatabaseIOMod.cs`
- `Constants.cs`

职责：模组初始化、基础配置常量、跨组件共享参数。

### 2.3 数据模型层

- `Models/ItemData.cs`
- `Models/DatabaseData.cs`

职责：数据库条目与聚合结构的数据承载（序列化/反序列化的中间模型）。

### 2.4 服务层（核心逻辑）

1. `DatabaseStore.cs`
   - 全局静态库存与会话锁中枢。
2. `DatabaseDataCodec.cs`
   - 数据库存档编码/解码（XML 字符串）。
3. `ItemSerializer.cs`
   - 游戏实体 Item -> ItemData。
4. `SpawnService.cs`
   - ItemData -> 游戏实体生成与投放。
5. `ModFileLog.cs`
   - 模组日志路径与输出。

### 2.5 组件层（玩法入口）

1. `DatabaseInterfaceComponent.cs`
2. `DatabaseTerminalComponent.cs`
3. `DatabaseAutoRestockerComponent.cs`
4. `DatabaseFabricatorOrderComponent.cs`

职责：把库存逻辑映射为可交互设备行为（UI、网络请求、自动化动作）。

### 2.6 XML 层

- `XML/Items.xml`
- `XML/FabricatorOverrides.xml`
- `XML/Text/English.xml`
- `XML/Text/SimplifiedChinese.xml`

职责：物品定义、界面节点、按钮动作绑定、多语言文本。

### 2.7 Lua 原型层（当前停用）

- `Lua/Autorun/dbiotest_init.lua`
- `Lua/dbiotest/client_terminal_ui.lua`
- `Lua/dbiotest/server_terminal_take.lua`

结论：Lua 当前不是运行主线路径，但作为未来虚拟终端 UI 方向仍有潜在价值。

---

## 3. 当前架构解剖（“组件驱动 + 静态 Store”）

当前架构主链路：

1. **组件层触发行为**：接口入库、终端会话、自动补货、加工台填料。
2. **DatabaseStore** 维护全局内存态与锁态。
3. **序列化/反序列化服务** 在实体与数据模型之间转换。
4. **Codec** 把数据库数据编码到终端可保存字段（用于持久化承载）。
5. **网络同步** 以摘要同步 + 容器原生同步的混合方式运作。

关键特点：

- 终端会话会“取空数据库到会话容器”，关闭时再“写回并合并”。
- 分页依赖槽位估算与堆叠预测（存在结构性误差来源）。
- 会话锁以 `databaseId` 为粒度，单库单活跃会话。

---

## 4. 核心模块技术细节（来自现状文档）

### 4.1 DatabaseStore（库存/会话中枢）

核心结构：

- `_store : Dictionary<string, DatabaseData>`：库存真值（当前实现下是运行时静态内存态）。
- `_activeTerminals : databaseId -> terminalEntityId`：会话锁。
- `_terminals`：终端弱引用注册表，用于广播同步。

核心 API（文档提及）：

- `AppendItems`
- `TakeAllForTerminalSession`
- `WriteBackFromTerminalContainer`
- `TryTakeItemsForAutomation`

关键行为：

- 打开终端：数据库内容可被“借出/搬运”到终端会话容器。
- 关闭终端：会话期间剩余内容写回并与外部变化合并。
- 自动化可在一定策略下消费数据库内容（包括非当前页条目场景）。

### 4.2 ItemSerializer（序列化策略）

记录字段包含：

- 标识符、耐久、品质、偷窃信息、前哨来源、槽位信息、子容器结构。

已知限制与策略：

- 当前版本中运行时 `Item.StackSize` 不可直接用于可靠序列化推断。
- 序列化初始按 `StackSize=1` 处理，再按规则合并。
- 合并规则偏保守：接近满耐久（`Condition >= 99.9f`）且无嵌套时才可合并。
- 有 `MaxSerializedStackSize=63` 的单条切分上限（见常量）。

### 4.3 SpawnService（反序列化落地）

- 按 `ItemData` 递归生成实体。
- 放置优先目标槽位，允许 `allowCombine=true`。
- 容器不足时会掉落（Drop）作为回退行为。

### 4.4 DatabaseInterfaceComponent（入库设备）

- 周期轮询（`IngestInterval`）容器。
- 过滤非法/禁用标签项。
- 受 `MaxStorageCount`、供电条件约束（固定设备）。
- 成功入库后删除原实体。

### 4.5 DatabaseTerminalComponent（终端）

支持模式：

- 手持终端（开关会话）
- 固定终端（`useInPlaceSession=true`）

功能覆盖：

- 会话锁、强制接管、超时关闭。
- 搜索、排序、分页、compact、上一匹配/下一匹配。
- 服务器摘要同步（itemCount、锁状态、会话状态、页码状态）。

分页路径（当前）：

- `_sessionEntries` + `_viewIndices` + `_sessionPages`。
- 使用 `EstimateSlotUsage` 和 `TerminalPageSize` 估算分页。
- 翻页前执行 `CaptureCurrentPageFromInventory` 捕获当前页真实变更。

网络健壮性：

- 做了“实体完全初始化后再同步摘要”的防护。

### 4.6 DatabaseAutoRestockerComponent（自动补货）

- 规则：supply 列表、文本筛选、目标 identifier/tag 白名单、槽位白名单。
- 目标通过 `item.linkedTo` 定位。
- 触发：空槽或低耐久。
- 防抖与互斥：`EmptyTicksRequired` + `SupplyCooldown`。
- 取料策略：`HighestConditionFirst`。
- 可先弹出现有低耐久，再补新物品。

### 4.7 DatabaseFabricatorOrderComponent（加工台填料）

- XML `DB Fill` 按钮触发 + 尝试 hook 原加工逻辑。
- C->S 请求后服务端解析配方与数量。
- 从数据库扣料并生成到输入槽。
- 通过反射适配不同 Fabricator 成员命名。
- 组件实现 `IClientSerializable/IServerSerializable`，并带 no-op 写入保障专服包结构稳定。

### 4.8 XML 设备层与 Override 风险

- `Items.xml` 定义手持/固定接口、终端、会话体、自动补货器。
- `FabricatorOverrides.xml` 直接 override 原版 `fabricator`/`medicalfabricator`。

风险：

- 与其他修改加工台 UI/组件的模组冲突概率高。

---

## 5. 当前运行时数据流（端到端）

### 5.1 入库流

1. 玩家将物品放入 Interface 容器。
2. 轮询过滤后序列化。
3. 追加到 DatabaseStore。
4. 实体移除并删除。

### 5.2 终端会话流

1. 加锁并取全量到会话。
2. 生成当前页到终端容器。
3. 翻页/排序/搜索前先捕获当前页状态。
4. 关闭时：扁平化 -> compact -> 写回 -> 清空容器 -> 解锁。

### 5.3 自动补货流

1. 枚举链接目标槽位。
2. 判空或低耐久（含防抖）。
3. 从数据库按策略取料。
4. 生成并投放目标槽。

### 5.4 加工台填料流

1. 客户端触发 `DB Fill`。
2. 服务端解析选中配方与批量。
3. 按 required items 从数据库扣减。
4. 生成原料到输入槽并刷新可制造状态。

---

## 6. 同步与持久化现状（含风险）

### 6.1 网络同步策略

- 已有：终端摘要 S->C，同步数据库 ID、数量、锁/会话/页码状态。
- 已有：终端动作 C->S（按钮动作码）。
- 已有：加工台填料请求 C->S（identifier + amount）。

总体：

- 非 AE2 风格的“按 key 增量订阅”系统。
- 当前更接近“事件触发 + 摘要同步 + 原生容器同步”的混合方案。

### 6.2 持久化策略

- 持久化字段载体：终端组件 `SerializedDatabase` + `DatabaseVersion`。
- 编码：`DatabaseDataCodec` 自定义 XML 字符串。
- Store 变化会触发 `SyncTerminals` 把数据刷入终端字段。

### 6.3 关键漏洞（高优先级）

- 运行时内存态与 campaign save 语义可能不一致。
- 在 Save / No-Save 分叉上存在刷物资风险（重构文档中定义为核心问题）。

---

## 7. 已知问题清单（按严重度）

### P0：Save / Quit 语义不严格

- 未完全区分 `Save & Quit` 与 `Quit without saving`。
- 可能导致回合内新增库存在无保存退出后仍“留存感知”。

### P1：终端视图与真实容器耦合深

- 分页受槽位估算和堆叠预测影响。
- 常见症状：页面未填满、可堆叠条目拆分、翻页体验不稳定。

### P1：堆叠推断能力不足

- `StackSize` 运行时不可直接可靠获取，导致策略保守。
- 复杂物品（低耐久/子容器）整理与压缩能力受限。

### P2：单会话锁多人体验受限

- 同数据库 ID 同时只允许一个活跃会话。
- 依赖超时与强制接管缓解，无法从模型上消除并发体验损失。

### P2：Fabricator override 生态冲突风险

- 对原版加工台直接 override，兼容复杂模组组合时风险较高。

### P3：本地化缺口

- 终端排序相关 key 未完全覆盖，部分依赖 fallback 英文。

---

## 8. 重构总方案（REFACTOR 文档核心）

## 8.1 目标

- `Quit without saving` 必须严格回滚到已提交状态。
- `Save & Quit` 必须确定性提交。
- 保持多人服务端权威。
- 避免终端会话与自动化并行下的丢失/复制。

## 8.2 三阶段主路线

### Phase A（快速止血）

- `roundStart` 清 volatile cache 并从持久字段重建。
- 加强早期同步防护与日志。

### Phase B（核心）

引入双态：

- `Committed`：已保存确认态（持久化真值源）
- `Working`：回合运行态（所有实时操作）

流程：

1. 开局：`Working <- deepClone(Committed)`
2. 回合内：全操作写 `Working`
3. 保存确认：`Committed <- deepClone(Working)`，并同步持久字段
4. 不保存退出：`Working <- deepClone(Committed)`，并清理会话与锁

### Phase C（信号绑定）

- 在可判断保存语义的服务端方法（建议 `GameServer.EndGame(... wasSaved ...)`）绑定：
  - `wasSaved=true -> CommitRound()`
  - `wasSaved=false -> RollbackRound()`
- 保留 `roundStart` 兜底清理。

## 8.3 新 API 规划

- `BeginRound()`
- `CommitRound()`
- `RollbackRound(reason)`
- `ClearVolatile()`
- `RebuildFromPersistedTerminals()`

规则：

- 摘要同步读 `Working`
- 持久化写仅从 `Committed`

## 8.4 关键测试矩阵（必须通过）

1. Save & Quit 持久化正确。
2. Quit without saving 不保留新变更。
3. 专服多人并发：终端会话 + 自动化同时运行。
4. 类崩溃路径：无保存确认终止后重启只加载 committed。
5. 回归：排序/搜索/分页/compact 无复制丢失。

---

## 9. 里程碑执行计划（M0~M6）整合版

### M0：基线与开关

- 建分支、打行为快照日志。
- 增加特性开关：`EnableDeltaSync` / `EnableLeasePaging` / `EnableCommittedWorkingStore`。

### M1：Save/No-Save 正确性

- 生命周期 API + EndGame 保存语义绑定 + rollback 前强制收尾会话。

### M2：数据核心拆分

- 引入 `DatabaseState/StateEntry/StateMutation`。
- 全量实时操作迁移到 `WorkingState`。
- commit/rollback clone 与版本推进规则。

### M3：增量同步

- `WatchRegistry`（watchAll/watchByKey）
- mutation event bus
- key 粒度 delta 推送
- 客户端版本差 fallback 全量

### M4：终端视图重写（默认 Track-A）

- 去 `EstimateSlotUsage`
- lease paging 生命周期
- 实际插入失败即停（不再估算）
- 排序/搜索与渲染流程解耦

### M5：自动化对齐

- restocker/fabricator 统一走 WorkingState consume API
- 活跃租约页与自动化消费边界规则

### M6：迁移与发布

- 旧数据迁移策略
- 全矩阵验证
- 文档更新与 `modversion` 提升

依赖顺序：M0 -> M1 -> M2 -> (M3/M4) -> M5 -> M6。

---

## 10. 来自 AE2 对比文档的“架构启发”

`ae2compare.md` 给出以下可借鉴点：

1. **服务端权威与职责拆分**
   - 当前 `DatabaseStore` 职责过多（真值+锁+广播），建议逐步服务化。

2. **协议分层**
   - 将网络包按 clientbound/serverbound/bidirectional 归类。
   - 引入容器/会话 ID 校验，防旧包污染当前会话。

3. **声明式同步 + 增量库存更新**
   - 以字段 schema + dirty-check 替代散落式手工同步。
   - 大列表采用 serial + delta + chunk。

4. **显式状态机**
   - 会话流程由隐式副作用转为状态机（Opening/Active/Flushing/Closed/Aborted）。

5. **并发与一致性策略**
   - 建议引入事务日志（WAL）与 fencing token（强制接管后旧 token 写入拒绝）。

6. **长期 UI 方向建议（Track-B 倾向）**
   - 虚拟列表 + 少量 I/O 缓冲槽，减少“容器即视图”结构性上限。

---

## 11. 来自 claude 建议文档的“落地策略”

### 11.1 与 M 路线一致的优先级

- P0：先做 Save/No-Save（M1+M2）。
- P1：终端 UI 路线先稳健实现 Option A（Lease Paging），为 Option B 留接口。
- P2：M3 做 Delta Sync（stable serial + watcher + version fallback）。
- P3：M5 统一自动化写入路径，解决并行冲突。
- P4：M6 做 Fabricator 兼容化与文本补齐。

### 11.2 Lua 虚拟 UI 的中长期建议

文档后段强调：

- Sandbox Menu 证明 Lua GUI 能力可覆盖复杂列表渲染。
- 难点不在“能否渲染”，而在“服务端权威数据协议”。
- 推荐 C# 提供“只读查询 + 写请求”窄接口，Lua 仅做渲染与交互转发。

建议接口示意：

- `QueryPage(databaseId, page, filter, sort)`
- `GetTotalCount(databaseId, filter)`
- `RequestExtract(databaseId, itemKey, amount)`
- `RequestDeposit(databaseId, items)`

关键原则：Lua 不直接改数据库，所有状态变更必须由 C# 服务端执行。

### 11.3 新增方案：Proxy Item（代理物品）路径评估（基于最新 Gemini 提案）

提案核心：

- 终端容器不放真实物品，而是放统一的 `database_dummy_item`（显示代理项）。
- 通过 C# 在客户端动态替换 Dummy 的图标/提示（表现为真实物品与数量）。
- 玩家取出时拦截拖拽/合并行为，阻止 Dummy 进入真实背包，再按规则生成真实物品并扣减数据库。
- 玩家存入时识别真实物品并销毁，转化为数据库计数。

提案预期收益：

1. 单格显示大数量（例如 1000/5000）不再受真实物品堆叠硬上限约束。
2. 终端排序与分页可由逻辑层主导，弱化容器真实堆叠规则干扰。
3. UI 视觉可接近“真实物品操作”。

我方技术性疑点（你的怀疑是合理的）：

1. **同步一致性风险高于普通虚拟列表**
   - Dummy 物品本体仍在容器网络同步链路里。
   - 一旦客户端动态图标、Tooltip、数量文本与服务端状态出现帧级或事件级延迟，用户会看到“显示值与真实库存短暂不一致”。

2. **交互拦截点脆弱，易受引擎更新/模组互操作影响**
   - 依赖 Hook `Inventory.TryPutItem` / `ItemContainer.Combine` 等底层行为。
   - 这些拦截点若被其他模组也 patch，或游戏版本行为变化，容易出现重复生成、未扣减、或吞物问题。

3. **“假物品入包瞬间删除”路径存在竞态**
   - 如果 Dummy 在某些路径先进入背包再删除，期间触发其他监听（快捷键、自动整理、网络回包）可能造成边界 bug。

4. **图标动态替换成本与兼容性**
   - 若需要大量 Dummy 同时显示不同目标图标，客户端刷新成本、缓存策略、图集引用与资源生命周期都需要专门管理。

5. **与 Save/No-Save 主问题无直接解耦**
   - 该方案解决的是“视图承载与堆叠上限”。
   - 若在 M1/M2 前推进，可能把 UI 层复杂性叠加到尚未稳定的数据一致性层。

结论（建议定位）：

- Proxy Item 方案可作为 **Track-B 的变体 PoC（B2）**，用于验证“容器承载代理显示”的可玩性。
- 但在优先级上不应前置于 M1/M2。
- 推荐顺序：
  1) 先完成 Committed/Working 与 Save/No-Save 正确性；
  2) 再用 feature flag 引入 `EnableProxyItemView` 做小范围实验；
  3) 用专门回归矩阵覆盖拖拽、拆分、并堆、断线重连、多人并发、模组互操作。

---

## 12. 方案分歧与统一结论

### 12.1 当前文档间的“分歧点”

- M4 默认轨道：`REFACTOR` 文档默认 Track-A；外部建议多偏向中长期 Track-B。
- 是否立即做虚拟终端 UI：有“先稳住数据层再动 UI”的共识，但 UI 切换时机不同。

### 12.2 可统一的“共识主线”

1. **必须先修保存语义正确性（M1/M2）**。
2. **终端视图与容器耦合需要逐步降级（至少先到 lease paging）**。
3. **最终要有增量同步（M3）与并发统一写路径（M5）**。
4. **Fabricator override 风险需治理（M6）**。

---

## 13. 决策建议（给下一步实现）

### 13.1 近端（本迭代）

- 仅做 M1 + M2 的最小闭环，并产出可复现实验脚本（Save / No-Save 对照）。
- 同步补齐版本文档一致性（README 与 filelist）。

### 13.2 中端（下一迭代）

- M3（delta）+ M4（lease paging）并行推进，保持 feature flag 可回滚。

### 13.3 远端（后续版本）

- 评估 Track-B PoC（虚拟列表 + I/O 缓冲），以协议层先行，UI 后迁移。

---

## 14. 文档质量与空文档说明

- `gemini_suggest.md` 已出现新的 Proxy Item（代理物品）思路，本整合文档已纳入技术评估与风险分析。
- 当前整合后建议把“设计决策记录（ADR）”单独建档，避免后续建议文档继续堆叠导致信息重复。

---

## 15. 一页版结论（TL;DR）

- 项目已具备可玩闭环，但核心架构上限明确：**保存语义一致性 + 终端容器耦合**。
- 技术路线最优先应是：**M1/M2（正确性） -> M3/M4（同步与视图） -> M5/M6（并发与生态）**。
- Track-B（虚拟终端）是中长期高收益方向，但前提是服务端数据权威和协议层先稳定。

---

## 16. 当前进度核查（聚焦 M1，结合 luacsforbarotrauma 文档/API）

> 核查基线：`REFACTOR_PLAN_SAVE_CONSISTENCY.md` 的 M1 拆分任务（T1.1~T1.4）。

### 16.1 M1 任务状态判定

| 任务 | 当前状态 | 代码/文档依据 | 判定 |
|---|---|---|---|
| T1.1 生命周期桥接 API | `BeginRound/CommitRound/RollbackRound` 已实现，且有 `ClearVolatile/RebuildFromPersistedTerminals`。 | `CSharp/Shared/Services/DatabaseStore.cs` | ✅ 已完成 |
| T1.2 保存语义 Patch 绑定 | 目前仅有“尝试安装 patch”的占位逻辑；未真正把 `wasSaved` 分支绑定到 commit/rollback。 | `CSharp/Shared/DatabaseIOMod.cs` | ⚠️ 部分完成（关键路径未落地） |
| T1.3 `roundStart` 兜底 | 实现了会话令牌驱动的 `EnsureRoundInitialized` + `BeginRound` 重建；同时保留 mod-load 启动路径。 | `CSharp/Shared/Services/DatabaseStore.cs` + `CSharp/Shared/DatabaseIOMod.cs` | ✅ 基本完成 |
| T1.4 rollback 前强制收尾会话 | `BeginRound/CommitRound/RollbackRound` 均先 `ForceCloseAllActiveSessions`，并清锁同步。 | `CSharp/Shared/Services/DatabaseStore.cs` | ✅ 已完成 |

### 16.2 luacsforbarotrauma 文档/API 交叉验证结论

1. **Hook.Patch 在 LuaCs 文档中是可用且建议的正式路径**。  
   `Hooks.lua` 与 manual 都给出了 `Hook.Patch` 的参数模式（含可选 identifier、parameterTypes、Before/After）。这说明“通过 patch 绑定保存决策”在能力层面是成立的。 

2. **Lua 层公开的 `Game.EndGame()` 无 `wasSaved` 参数**。  
   `Game.lua` 仅暴露无参 `Game.EndGame()`；因此 M1 文档里提到的 `GameServer.EndGame(... wasSaved ...)` 更像是 **C# 侧内部签名目标**，不能直接从 Lua API 文档推导出最终参数列表。 

3. **当前代码尚未进入“真正 patch 成功”阶段**。  
   `DatabaseIOMod.TryInstallSaveDecisionPatch()` 目前只做类型与方法存在性探测并记录日志，尚无 `Hook.Patch` 调用与回调绑定实现，所以 Save/No-Save 正确性的退出条件还未满足。 

### 16.3 对“m1 已经进行了一部分”的精确结论

- 你们对 M1 的判断是准确的：**目前约完成 3/4（T1.1、T1.3、T1.4 已落地；T1.2 未打通）**。
- 阻塞点集中在 **保存语义事件桥接**，不是数据模型本身。

### 16.4 下一步最小闭环建议（仅补齐 T1.2）

1. 在 `TryInstallSaveDecisionPatch` 中落地真正的 `Hook.Patch` 调用（优先 Before/After 二选一并固定 parameterTypes）。
2. 回调里按可用参数识别保存结果：
   - `saved=true` -> `DatabaseStore.CommitRound("endgame-saved")`
   - `saved=false` -> `DatabaseStore.RollbackRound("endgame-unsaved")`
3. 若当前版本签名不稳定，先实现 Plan-B：
   - `roundEnd` 事件 + 保守回滚（默认 rollback，只有明确 save 才 commit）
   - 同时在日志中输出命中的签名/分支，作为后续版本适配依据。
