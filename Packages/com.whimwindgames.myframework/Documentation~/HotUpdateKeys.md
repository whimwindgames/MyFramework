# 热更新签名密钥

Schema 11 的 Latest 使用 P-256/ES256 签名。公钥随 Base 冻结并进入客户端；私钥只能位于项目和 Git 工作区之外。

## 从零配置

1. 在 Unity 打开 `MyFramework/HotUpdate/密钥与轮换`。
2. 确认项目密钥标识。框架默认使用项目目录名，并把目录固定为 `~/.myframework-keys/{project}/{env}/`。
3. 分别在 `test`、`prod` 创建初始密钥。正式环境建议输入至少 12 个字符的密码，生成 AES-256-CBC 加密 PEM；密码不会写入 EditorPrefs。
4. 把窗口显示的公钥写入对应环境的下一份 Base 配置。发布窗口只保存 test/prod 私钥路径，两个环境复用同一路径会被拒绝。
5. 在仓库执行 `Tools/install-git-hooks.sh`。预提交检查会拒绝私钥容器和完整 PEM 私钥材料。

CI 使用加密 PEM 时，通过秘密环境变量注入密码，只把变量名传给发布入口：

```text
-pubPrivKey /secure/prod/latest.pem -pubPrivKeyPasswordEnv HOTUPDATE_KEY_PASSWORD
```

不要把密码直接放进命令行参数、Unity 资源、ProjectSettings 或 EditorPrefs。`RelReq.privateKeyPassword` 和 `PubEnv.privateKeyPasswordForEnv` 也只应连接到进程内秘密提供器。该接口为后续系统钥匙串或硬件签名提供器保留扩展边界。

## 密钥轮换

轮换必须按以下顺序完成，窗口会保存每一步的状态并拒绝跳步：

1. 生成 Pending 密钥对，Active 保持不变。
2. 使用旧 Active 私钥对当前 Latest 重新签发一份过渡 Latest。输出保存在 Release 根目录的 `{env}/rotation/{platform}/`，keyring 同时记录其哈希、旧 Base 和 seq。
3. 构建下一份 Base，并确认冻结的公钥等于 Pending 公钥；旧 Base 与新 Base 不能相同。
4. 完成轮换。框架将旧私钥移动到 `archive/{keyId}.pem.disabled`、设置只读权限，并把 Pending 提升为 Active。

归档私钥默认禁用，不再用于新 Release。需要调查历史签名时保留归档文件；恢复使用必须作为独立的人工安全操作处理，框架不提供一键恢复。

## 旧项目迁移

旧链使用 `MyFramework.HotUpd.privKey` EditorPrefs。密钥窗口可以读取其中的 PEM 正文或绝对路径，并复制到当前环境的约定目录。迁移不会覆盖已有 keyring，也不会自动删除旧值。

先用新路径完成一次公私钥匹配校验，再在窗口点击“确认后清除旧 EditorPrefs”。若旧值是外部文件路径，EditorPrefs 清除不会删除原文件；原文件仍需按团队的安全流程人工归档或销毁。
