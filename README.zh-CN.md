# Database IO Test（0.3.9）

## 当前状态
- 核心逻辑和交互界面由 C#（LuaCs） 实现。
- 当前终端界面为 C# UI + XML CustomInterface。
- 数据结构和交互行为仍在迭代中。

## 模组内容
- `DatabaseStorageAnchor`（数据库存储器/锚点）
  - 承载数据库持久化存储。
- `DatabaseInterface`（手持）与 `DatabaseInterfaceFixed`（固定式）
  - 吸收物品并序列化写入共享数据库。
- `DatabaseTerminal`（手持）与 `DatabaseTerminalFixed`（固定式）
  - 通过会话方式访问数据库物品。
  - 支持分页、搜索、排序、整理（Compact）等操作（C# UI）。
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

## 存档兼容性警告
- 后续更新可能调整数据库序列化结构。
- 旧战役存档可能受到影响（例如：数据库物品丢失、重复、重置等破坏性变化）。
- 更新前请务必备份存档。

## 推荐更新流程
1. 先关闭所有数据库终端会话。
2. 正常结束本轮并保存战役。
3. 备份存档文件。
4. 再更新模组。
5. 先用非关键存档进行回归测试。
