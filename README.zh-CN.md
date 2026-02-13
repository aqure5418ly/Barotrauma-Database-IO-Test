# Database IO Test（0.1.0）

## 当前状态
- 这是一个**测试版本**。
- 主要逻辑由 **C#（LuaCs）** 实现。
- 当前版本**没有可用的 Lua UI**。
- `Lua/` 目录目前仅作后续开发预留，运行时可视为暂时废弃。

## 模组内容
- `DatabaseInterface`（手持）与 `DatabaseInterfaceFixed`（固定式、需接电）
  - 吸收物品并序列化写入共享数据库。
- `DatabaseTerminal`（手持）与 `DatabaseTerminalFixed`（固定式、需接电）
  - 通过会话方式访问数据库物品。
  - 支持分页、搜索、排序、整理（Compact）（XML + C# 实现）。
- `DatabaseAutoRestocker`（自动补货器）
  - 从数据库取物并补充到链接目标。
- 加工台联动（`DB Fill` 按钮，基于 override）
  - 从数据库拉取配方原料到加工台输入槽。

## 关键说明
- 本模组的 `filelist.xml` 当前不会加载 Lua 脚本。
- 现有交互界面为 XML CustomInterface + C# 组件，不依赖 Lua UI。
- 数据结构仍在持续迭代中。

## 灵感与参考
- 灵感来源：
  - Applied Energistics 2（AE2）：https://appliedenergistics.org/
- 代码/实现参考：
  - Item IO Framework：https://steamcommunity.com/sharedfiles/filedetails/?id=2950383008
  - IO Storage：https://steamcommunity.com/sharedfiles/filedetails/?id=3646358075

以上项目仅作为设计与实现参考，本模组与其无隶属关系。

## 存档兼容性警告
- 后续更新可能调整数据库序列化结构。
- 旧战役存档可能受到影响（例如：数据库物品丢失、重复、重置等破坏性变化）。
- **更新前请务必备份存档。**

## 推荐更新流程
1. 先关闭所有数据库终端会话。
2. 正常结束本轮并保存战役。
3. 备份存档文件。
4. 再更新模组。
5. 先用非关键存档进行回归测试。

## 运行需求
- 启用 LuaCs 的 Barotrauma。
- 在内容包中启用本模组。
