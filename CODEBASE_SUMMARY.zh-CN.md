# Database IO Test 代码库现状总览（讨论稿）

## 1. 文档目的

本文件用于和 `REFACTOR_PLAN_SAVE_CONSISTENCY.md` 配套，给出当前代码库的**可实现现状**、**架构边界**、**已知问题**与**后续讨论入口**。  
时间基准：当前仓库 `barotraumaworkshop/Database IO Test`。

## 2. 当前版本与发布状态

- 内容包版本：`0.1.1`（见 `barotraumaworkshop/Database IO Test/filelist.xml`）。
- 开发性质：测试版，结构持续迭代中。
- README 状态：
  - `barotraumaworkshop/Database IO Test/README.zh-CN.md` 标注为 `0.1.0`，与 `filelist.xml` 不一致（需后续统一）。
- Lua 说明：
  - 代码库中有 Lua 原型，但当前初始化脚本直接 `return`（见 `barotraumaworkshop/Database IO Test/Lua/Autorun/dbiotest_init.lua`）。
  - 当前运行主线为 C#（LuaCs）+ XML CustomInterface。

## 3. 文件结构（按功能）

### 3.1 入口与元数据

- `barotraumaworkshop/Database IO Test/filelist.xml`
- `barotraumaworkshop/Database IO Test/CSharp/RunConfig.xml`
- `barotraumaworkshop/Database IO Test/CSharp/DatabaseIOTest.buildcheck.csproj`

### 3.2 C# 核心

- `barotraumaworkshop/Database IO Test/CSharp/Shared/DatabaseIOMod.cs`
- `barotraumaworkshop/Database IO Test/CSharp/Shared/Constants.cs`

### 3.3 数据模型

- `barotraumaworkshop/Database IO Test/CSharp/Shared/Models/ItemData.cs`
- `barotraumaworkshop/Database IO Test/CSharp/Shared/Models/DatabaseData.cs`

### 3.4 服务层

- `barotraumaworkshop/Database IO Test/CSharp/Shared/Services/DatabaseStore.cs`
- `barotraumaworkshop/Database IO Test/CSharp/Shared/Services/DatabaseDataCodec.cs`
- `barotraumaworkshop/Database IO Test/CSharp/Shared/Services/ItemSerializer.cs`
- `barotraumaworkshop/Database IO Test/CSharp/Shared/Services/SpawnService.cs`
- `barotraumaworkshop/Database IO Test/CSharp/Shared/Services/ModFileLog.cs`

### 3.5 组件层

- `barotraumaworkshop/Database IO Test/CSharp/Shared/Components/DatabaseInterfaceComponent.cs`
- `barotraumaworkshop/Database IO Test/CSharp/Shared/Components/DatabaseTerminalComponent.cs`
- `barotraumaworkshop/Database IO Test/CSharp/Shared/Components/DatabaseAutoRestockerComponent.cs`
- `barotraumaworkshop/Database IO Test/CSharp/Shared/Components/DatabaseFabricatorOrderComponent.cs`

### 3.6 XML 物品与界面

- `barotraumaworkshop/Database IO Test/XML/Items.xml`
- `barotraumaworkshop/Database IO Test/XML/FabricatorOverrides.xml`
- `barotraumaworkshop/Database IO Test/XML/Text/English.xml`
- `barotraumaworkshop/Database IO Test/XML/Text/SimplifiedChinese.xml`

### 3.7 Lua 原型（当前停用）

- `barotraumaworkshop/Database IO Test/Lua/Autorun/dbiotest_init.lua`
- `barotraumaworkshop/Database IO Test/Lua/dbiotest/client_terminal_ui.lua`
- `barotraumaworkshop/Database IO Test/Lua/dbiotest/server_terminal_take.lua`

## 4. 架构概览（当前实现）

当前是“组件驱动 + 静态数据库服务”的结构：

1. 组件（Interface/Terminal/Restocker/Fabricator）负责交互入口与业务触发。
2. `DatabaseStore` 作为全局静态库存服务，维护数据库内容、终端锁和终端引用。
3. `ItemSerializer` 与 `SpawnService`负责“实体 <-> ItemData”互转。
4. `DatabaseDataCodec` 将 `DatabaseData` 编码为 XML 字符串，存到终端可保存字段。
5. 终端组件通过 `IServerSerializable/IClientSerializable` 做摘要同步（计数、锁、页码等）。

## 5. 模块实现说明

## 5.1 DatabaseStore（存储中枢）

路径：`barotraumaworkshop/Database IO Test/CSharp/Shared/Services/DatabaseStore.cs`

当前职责：

- 管理内存库存 `_store : Dictionary<string, DatabaseData>`。
- 管理会话锁 `_activeTerminals : databaseId -> terminalEntityId`。
- 管理终端注册 `_terminals`（弱引用列表）。
- 提供取放 API：
  - `AppendItems`
  - `TakeAllForTerminalSession`
  - `WriteBackFromTerminalContainer`
  - `TryTakeItemsForAutomation` 等。

关键行为：

- 打开终端会话时，`TakeAllForTerminalSession` 会把数据库内容取空给会话端。
- 关闭会话时，`WriteBackFromTerminalContainer` 把会话剩余写回，并与会话期间新增内容合并。
- 自动化取料支持“非当前页”消费（通过终端组件回调）。

## 5.2 ItemSerializer（序列化）

路径：`barotraumaworkshop/Database IO Test/CSharp/Shared/Services/ItemSerializer.cs`

特点：

- 记录字段：标识符、耐久、品质、偷窃信息、原始前哨、槽位、子容器结构。
- 当前版本 `Item` 不暴露运行时 `StackSize`，因此序列化初始写死 `StackSize=1`，再依规则合并。
- 合并规则较严格：
  - 仅在条件接近满耐久（`Condition >= 99.9f`）且无嵌套内容时可堆叠。
- 单条最大序列堆叠被切分为 `MaxSerializedStackSize=63`（见 `Constants.cs`）。

## 5.3 SpawnService（反序列化生成）

路径：`barotraumaworkshop/Database IO Test/CSharp/Shared/Services/SpawnService.cs`

职责：

- 按 `ItemData` 递归生成实体进目标库存。
- 支持槽位优先放置与 `allowCombine=true`。
- 放置失败时会 `Drop`（容器不够时会掉地）。

## 5.4 DatabaseInterfaceComponent（数据库接口）

路径：`barotraumaworkshop/Database IO Test/CSharp/Shared/Components/DatabaseInterfaceComponent.cs`

逻辑：

- 周期轮询容器（`IngestInterval`）。
- 过滤非法/禁用标签物品后序列化并写入数据库。
- 受 `MaxStorageCount` 和可选供电条件约束。
- 吸收后删除原实体。

## 5.5 DatabaseTerminalComponent（终端）

路径：`barotraumaworkshop/Database IO Test/CSharp/Shared/Components/DatabaseTerminalComponent.cs`

功能覆盖：

- 手持终端（开关形态）与固定终端（`useInPlaceSession=true`）两种模式。
- 会话锁、强制接管、超时关闭。
- 页码、搜索、排序、整理（compact）、上一匹配/下一匹配。
- 服务器摘要同步（数据库计数、锁状态、会话状态、页码信息）。

当前分页机制：

- 源数据 `_sessionEntries` + 视图索引 `_viewIndices` + 页面缓存 `_sessionPages`。
- 分页时使用“估算槽位”(`EstimateSlotUsage`)与 `TerminalPageSize` 控制页面。
- 页面切换前执行 `CaptureCurrentPageFromInventory`，将当前容器状态写回会话源数据。

网络安全处理：

- `TrySyncSummary` 带“实体完全初始化”检查，避免在 item 未初始化时发网络事件。

## 5.6 DatabaseAutoRestockerComponent（自动补货器）

路径：`barotraumaworkshop/Database IO Test/CSharp/Shared/Components/DatabaseAutoRestockerComponent.cs`

核心点：

- 支持 supply 列表、文本筛选、目标标识符/标签白名单、槽位白名单。
- 通过 `item.linkedTo` 找目标容器。
- 触发条件：槽空或物品耐久低于阈值。
- 防抖与互斥：
  - 连续空槽 tick（`EmptyTicksRequired`）
  - 槽位锁（`SupplyCooldown`）
- 取料策略：`HighestConditionFirst`。
- 对低耐久现有物品先弹出再补。

## 5.7 DatabaseFabricatorOrderComponent（加工台自动填料）

路径：`barotraumaworkshop/Database IO Test/CSharp/Shared/Components/DatabaseFabricatorOrderComponent.cs`

实现方式：

- 支持 XML 按钮 `DB Fill` 触发，也尝试 hook 原加工按钮点击。
- 客户端发请求 -> 服务端解析目标配方与数量 -> 从数据库扣原料 -> 生成到输入容器。
- 大量使用反射兼容不同 Fabricator 成员命名：
  - 选中配方
  - 选中数量
  - 配方列表字段
- 网络兼容处理：
  - 组件实现 `IClientSerializable` + `IServerSerializable`
  - 提供 no-op `ServerEventWrite`，避免专服组件事件读写长度错位。

## 5.8 XML 物品层

路径：`barotraumaworkshop/Database IO Test/XML/Items.xml`

现有设备：

- 手持接口：`DatabaseInterface`
- 手持终端：`DatabaseTerminal` + 会话体 `DatabaseTerminalSession`
- 固定接口：`DatabaseInterfaceFixed`（可接电）
- 固定终端：`DatabaseTerminalFixed`（可接电，in-place 会话）
- 自动补货器：`DatabaseAutoRestocker`

UI 方式：

- 主要是 XML `CustomInterface`（按钮 + TextBox）驱动 `XmlActionRequest` 与组件属性。
- 固定终端目前采用双面板组合（开关控制 + 搜索/分页/排序/整理）。

## 5.9 Fabricator override

路径：`barotraumaworkshop/Database IO Test/XML/FabricatorOverrides.xml`

做法：

- Override 原版 `fabricator` 和 `medicalfabricator`。
- 注入 `DatabaseFabricatorOrderComponent` 和 `DB Fill` 按钮。

风险：

- 与其他改加工台 UI/组件的模组存在 override 冲突概率。

## 6. 数据流与运行时流程（当前）

## 6.1 入库流程（接口）

1. 玩家放入接口容器。  
2. Interface 组件轮询容器并过滤。  
3. `SerializeItems` -> `DatabaseStore.AppendItems`。  
4. 原物品从容器移除并删除。  

## 6.2 终端会话流程

1. 打开会话：加锁 -> `TakeAllForTerminalSession`。  
2. 构建分页并加载当前页到终端容器。  
3. 翻页/排序/搜索前先 `CaptureCurrentPageFromInventory` 回写当前页变更。  
4. 关闭会话：扁平化会话数据 -> compact -> 写回 store -> 清空终端容器 -> 解锁。  

## 6.3 自动补货流程

1. 根据链接目标 + 槽位遍历。  
2. 判断空槽/低耐久并通过空槽 tick 防抖。  
3. `TryTakeOneByIdentifierForAutomation` 取库。  
4. 生成并投放到目标槽。  

## 6.4 加工台填料流程

1. `DB Fill` 请求到服务端。  
2. 定位选中配方与批量数量。  
3. 对每个 required item 从数据库扣减。  
4. 生成原料到输入槽，刷新加工台可制造状态。  

## 7. 网络同步现状

## 7.1 已实现

- 终端摘要同步（Server -> Client）：
  - 数据库 ID
  - itemCount
  - locked/session/page 信息。
- 面板动作同步（Client -> Server）：
  - 终端按钮动作 byte。
- 加工台填料请求（Client -> Server）：
  - identifier + amount。

## 7.2 当前策略

- 不是 AE2 式“订阅增量 key 推送”。
- 更接近“事件触发 + 必要摘要 + 容器原生同步”的混合模式。
- 终端内容本体仍依赖真实容器同步，不是纯虚拟视图。

## 8. 持久化现状

- 持久化承载字段：终端组件 `SerializedDatabase` + `DatabaseVersion`（可保存字段）。
- 编码格式：自定义 XML 字符串（`DatabaseDataCodec`）。
- store 改变时会 `SyncTerminals`，把数据刷到终端字段。

当前已知漏洞点：

- 仍存在“运行时内存 store 与 campaign save 语义不一致”风险（已在重构计划文档中定义为核心问题）。

## 9. 已知问题与技术债（重点）

## 9.1 Save / Quit 语义

- 当前架构未完全区分：
  - “保存并退出”
  - “不保存退出”
- 结果是可出现回合内数据持久化异常，产生刷物品风险。

## 9.2 终端视图与真实容器耦合过深

- 分页依赖槽位估算与堆叠预测，受物品硬编码堆叠上限影响。
- 典型表现：
  - 页面未填满
  - 本应可并堆的条目被拆分
  - 估算偏差导致体验不稳定。

## 9.3 堆叠推断限制

- 运行时 `Item.StackSize` 不可用，序列化只能先记 1 再合并。
- 合并规则为“满耐久 + 无子容器”时才堆叠，导致复杂物品不易整理。

## 9.4 会话锁体验

- 同数据库 ID 单会话锁策略会阻塞其他玩家终端操作。
- 当前靠超时与 force 接管缓解，但多人体验仍有限制。

## 9.5 覆盖冲突风险

- `FabricatorOverrides.xml` 直接 override 原版加工台，和其他大模组并存风险较高。

## 9.6 本地化缺口

- 终端代码使用了若干排序相关 key（如 `dbiotest.terminal.sort.*`），文本文件中未完整提供；当前依赖 fallback 英文。

## 10. 历史问题与当前修复状态（摘要）

已处理或加入防护的典型问题：

1. 组件网络包长度错位  
   - 通过实现/补齐组件序列化接口与 no-op 写入规避。

2. 终端创建期网络事件报错  
   - 增加“item fully initialized”检查后延迟同步摘要。

3. 日志路径不稳定  
   - `ModFileLog` 统一支持 LocalMods 与 ServerLogs 路径候选。

4. 固定终端误吞物  
   - in-place 会话下增加 idle 容器清理与回退逻辑。

## 11. 与重构计划文档关系

- 已有规划文件：`barotraumaworkshop/Database IO Test/REFACTOR_PLAN_SAVE_CONSISTENCY.md`
- 当前代码库总结文档（本文件）用于回答：
  - 现在实际做到了什么？
  - 哪些问题是结构性问题而非参数问题？
  - 为什么要分阶段重构（M1~M6）？

## 12. 建议讨论议题（给外部讨论用）

建议先围绕以下问题做方案决策：

1. 终端最终 UI 走向：
   - 继续容器租约分页（Track-A）
   - 还是“虚拟列表 + 真实 I/O 缓冲”（Track-B）。
2. 是否接受对加工台的 override 风险，还是改成可选注入策略。
3. 是否把“自动化与终端会话并行读写”提升为硬需求（影响数据层设计）。
4. Save/No-Save 的 patch 绑定点是否用 `GameServer.EndGame(... wasSaved ...)` 作为主路径。

## 13. 当前结论（短版）

- 项目已经具备可玩的完整链路（入库、取出、分页、搜索、排序、整理、补货、加工台填料）。
- 但核心架构仍是“真实容器驱动视图 + 静态内存 store”，在多人与存档语义上存在上限。
- 下一阶段应优先解决：
  - Save/No-Save 正确性
  - 终端视图与数据层解耦
  - 增量同步与会话一致性。
