# Database IO Test（1.1.5）

## 当前状态
- 核心逻辑和交互界面由 C#（LuaCs） 实现。
- 当前终端界面为 C# UI + XML CustomInterface。
- 数据结构和交互行为仍在迭代中。
- 终端模型说明：`NO_SESSION_MODEL.zh-CN.md`（无会话原子模式）。

## 模组内容
- `DatabaseStorageAnchor`（数据库存储器/锚点）
  - 承载数据库持久化存储。
- `DatabaseInterface`（手持）与 `DatabaseInterfaceFixed`（固定式）
  - 吸收物品并序列化写入共享数据库。
- `DatabaseUnpackInterface`（手持解包接口）与 `DatabaseUnpackInterfaceFixed`（固定式解包接口）
  - 可以自动解包放入其中的容器，并将容器内的所有物品递归存入数据库。
- `DatabaseTerminal`（手持）、`DatabaseTerminalFixed`（固定式）与 `DatabaseCraftTerminal`（数据库合成终端）
  - 已彻底移除旧版独占会话机制，采用无会话原子模式访问数据库。
  - 支持多玩家、多终端同时打开进行独立操作，互不抢占。
  - 本地 UI 快照只读，取物通过原子请求写入终端输出缓冲区。
  - 支持分类、搜索、排序、变体分格与可调 cell 大小（C# UI）。
- `DatabaseAutoRestocker`（自动补货器）
  - 从数据库取物并补充到链接目标。
- 加工台联动（`DB Fill` 按钮，基于 override）
  - 从数据库拉取配方原料到加工台输入槽。

## 运行前置
- 需要安装 LuaCsForBarotrauma。
- 在游戏右上角 LuaCs Settings 中打开 Enable CSharp Scripting。
- 在内容包中启用本模组。

## 灵感与参考
- Applied Energistics 2（AE2）：https://appliedenergistics.org/
- Item IO Framework：https://steamcommunity.com/sharedfiles/filedetails/?id=2950383008
- IO Storage：https://steamcommunity.com/sharedfiles/filedetails/?id=3646358075
- UI 参考 Super Terminal：https://steamcommunity.com/sharedfiles/filedetails/?id=3670545214&searchtext=superterminal

以上项目仅作为设计与实现参考，本模组与其无隶属关系。

## 未来计划
1. **代码清理:** 重构并清理旧版遗留代码。
2. **API 安全化:** 将内部方法改造为安全的外部调用接口。
3. **预编译发布:** 未来计划改为发布预编译（DLL）版本。
4. **扩展游戏性:** 为本模组设计专属任务、事件、自动化机器，丰富游戏内容。
5. **DLC 适配:** 等待官方 DLC 更新后进行适配。

## 存档兼容性警告
- 后续更新可能调整数据库序列化结构。
- 旧战役存档可能受到影响（例如：数据库物品丢失、重复、重置等破坏性变化）。
- 更新前请务必备份存档。

## 推荐更新流程
1. 正常结束本轮并保存战役。
2. 备份存档文件。
3. 更新模组后先用非关键存档进行回归测试。
