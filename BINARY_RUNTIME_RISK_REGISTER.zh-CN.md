# Database IO Test 预编译运行高风险点清单

更新时间: 2026-02-25

## 1. 目标
这个文档只记录“预编译 DLL 运行路径”中的高风险点，尤其是会导致:
- `MethodAccessException`
- Harmony patch 安装失败
- 本地 DLL 未同步导致“修了但没生效”

## 2. 高风险点总览

### R-001 直接访问 `GameMain.NetworkMember`（高风险，未完全收敛）
- 风险说明:
  - 在预编译 DLL 模式下，`GameMain.NetworkMember` 可能因可见性变化触发 `MethodAccessException`。
  - 该问题已经在 `EndGameSaveDecisionPatch` 上出现过同类崩溃。
- 当前代码命中:
  - `CSharp/Shared/Components/DatabaseAutoRestockerComponent.cs:114`
  - `CSharp/Shared/Components/DatabaseAutoRestockerComponent.cs:425`
  - `CSharp/Shared/Components/DatabaseAutoRestockerComponent.cs:527`
  - `CSharp/Shared/Components/DatabaseStorageAnchorComponent.cs:24`
  - `CSharp/Shared/Components/DatabaseInterfaceComponent.cs:32`
  - `CSharp/Shared/Components/DatabaseFabricatorOrderComponent.cs:29`
  - `CSharp/Shared/Components/DatabaseFabricatorOrderComponent.cs:137`
  - `CSharp/Shared/Components/DatabaseTerminalComponent.State.cs:241`
  - `CSharp/Shared/Components/DatabaseTerminalComponent.Network.cs:221`
- 当前状态:
  - 仅 `EndGameSaveDecisionPatch` 和 `ModFileLog` 已做反射安全化。
  - 其余组件仍有直接访问，属于后续必须收敛项。
- 建议:
  - 引入统一 helper（例如 `RuntimeApi.IsServerAuthoritySafe()` / `IsClientSafe()`），替换所有直接访问。

### R-002 `DebugConsole.NewMessage` 直接调用（高风险，已缓解）
- 风险说明:
  - 预编译模式下直接调 `DebugConsole.NewMessage` 可能触发访问异常。
- 当前状态:
  - 已统一替换为 `ModFileLog.TryConsoleMessage(...)`。
  - 安全封装位置: `CSharp/Shared/Services/ModFileLog.cs:62`
- 备注:
  - 后续新增 UI/日志代码必须继续走封装，禁止回归到直接调用。

### R-003 Harmony 目标方法漂移（高风险，部分缓解）
- 风险说明:
  - 不同版本下方法签名/可见性变化会导致 patch 失败。
- 高风险点:
  - `CSharp/Shared/Patches/EndGameSaveDecisionPatch.cs`
  - `CSharp/Shared/Patches/DatabaseTerminalFixedHudPatch.cs`
  - `CSharp/Shared/DatabaseIOMod.cs` 中 patch 安装流程
- 当前状态:
  - 已做分组安装与部分反射容错。
- 建议:
  - 保持“UI patch 与 SaveDecision patch 分组失败隔离”。
  - 所有 patch 继续优先 `TargetMethods()` 反射找目标，避免硬编码签名。

### R-004 引用版本不匹配（高风险，流程风险）
- 风险说明:
  - `luacsforbarotrauma_refs.zip` 与当前游戏版本不一致时，可能“编译通过，运行崩溃”。
- 当前相关位置:
  - `build-assembly.ps1` 仅检查 refs 文件存在，不校验版本兼容性。
- 建议:
  - 发布前固定一套 refs 并记录来源版本（游戏版本 + refs 提交号）。
  - 每次游戏更新后必须做二进制回归测试。

### R-005 DLL 被占用导致同步失败（高风险，运维风险）
- 风险说明:
  - Barotrauma 进程占用 `LocalMods/.../bin/.../DatabaseIOTest.dll` 时，hook 同步失败但源码侧已更新，造成“误以为已生效”。
- 现状:
  - 已在多次同步中出现 `ERROR 32` 文件锁。
- 建议:
  - 测试前关闭游戏再执行同步。
  - hook 脚本后续可增加“检测到锁时显式警告并退出非 0”。

### R-006 通道配置不一致（中高风险）
- 风险说明:
  - `source/binary` 两通道若 `filelist.xml`、`sync-map.json`、LocalMods 状态不一致，会出现加载路径混乱。
- 当前机制:
  - 已提供 `switch-channel.ps1` 一键切换。
  - 模板文件:
    - `filelist.binary.xml`
    - `filelist.source.xml`
- 建议:
  - 发布前强制执行一次 `switch-channel.ps1`，并做配置一致性检查。

### R-007 `ACsMod` 基类过时（中风险）
- 风险说明:
  - 当前仍有编译警告: `ACsMod` 已过时，未来版本可能移除。
- 当前位置:
  - `CSharp/Shared/DatabaseIOMod.cs:9`
- 建议:
  - 规划迁移至 `IAssemblyPlugin`。

## 3. 已完成缓解项
- [x] `DebugConsole.NewMessage` 统一替换为安全封装。
- [x] `EndGameSaveDecisionPatch.IsServerAuthority()` 改为反射访问路径。
- [x] 双通道切换脚本（`switch-channel.ps1`）已建立。
- [x] Hook 流程已支持提交/推送自动 build + sync。

## 4. 二进制发布前最低门槛（建议）
- [ ] 所有 `GameMain.NetworkMember` 直接访问清零（统一 helper）。
- [ ] `filelist.xml` / `sync-map.json` / LocalMods 三方一致性检查通过。
- [ ] 在目标整合包做一次完整多人冒烟（开局、终端、供货器、退出到菜单、保存/不保存）。
- [ ] 同步过程无 DLL 文件锁失败。

## 5. 发布策略建议
- 当前建议:
  - 稳定发布优先 `source` 通道。
  - `binary` 通道保留为可选分发。
- 切换命令:
  - `make channel-source`
  - `make channel-binary`
