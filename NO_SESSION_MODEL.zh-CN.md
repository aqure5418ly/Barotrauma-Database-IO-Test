# Database IO Test 无会话原子终端模型

## 目标
- 移除终端会话锁（Open/Force/Close）链路。
- 改为服务端权威原子读写，客户端快照只读展示。

## 现行行为
- 打开终端 UI 不再改变数据库状态，也不占用会话锁。
- 取物请求携带 `identifier + variantKey + count`，服务端原子执行后广播增量。
- 终端容器分为输入/输出缓冲区：
  - 输入区：自动入库并清空。
  - 输出区：服务端写入取出物。
- 固定式与手持式终端共用同一无会话语义。

## 兼容策略
- `DatabaseTerminalSession` 保留为隐藏兼容物品（旧存档迁移），行为等同于 `DatabaseTerminal`。
- `DatabaseStore` 的会话锁接口保留为 `Obsolete` 壳，不再被终端调用链使用。

## 不再提供
- OpenSession / ForceOpenSession / CloseSession 业务语义。
- 会话写回、会话页缓存、会话锁抢占流程。
