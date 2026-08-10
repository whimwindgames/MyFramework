# 热更新服务器部署

本目录是 hot-store 服务端资产的**唯一真值来源**。线上服务器 `47.243.79.140`
的文件必须与这里保持一致；修改本目录后需要同步服务器并重新完成
上传、回读和 HTTPS Range 验收。

## 文件

- `hot_store.py`：发布端上传、回读、列举和发布锁协议（协议版本 2，
  见文件内 `VERSION` 常量）。
- `hot-store.json`：服务器资源目录、状态目录和锁租期模板。
- `nginx-http.conf`：首次申请 HTTPS 证书前的 HTTP 配置。
- `nginx-https.conf`：客户端只读 HTTPS 配置。
- `reload-nginx`：证书续期成功后检查并重载 Nginx。
- `tests/test_hot_store.py`：协议测试，含与 Unity 发布客户端
  （`Packages/com.whimwindgames.myframework/Editor/HotUpd/Pub/SshStore.cs`）
  的版本一致性校验。

## 服务器位置

- 发布程序：`/usr/local/bin/hot-store`
- 发布配置：`/etc/hot-store.json`
- 客户端资源：`/srv/arcade-hub/hot`
- 断点和发布锁：`/var/lib/arcade-hub/hot-store`
- Nginx 配置：`/etc/nginx/sites-available/arcade-hot`
- 证书续期钩子：`/etc/letsencrypt/renewal-hooks/deploy/reload-nginx`

IP HTTPS 证书是短周期证书，服务器上的 Certbot 定时器负责自动续期。

## 协议版本

`hot_store.py` 的 `VERSION` 与 Unity 发布客户端 `SshStore.check()` 期望的
`hot-store/<N>` 必须一致，不一致时发布客户端会拒绝连接。两侧其中之一改动
协议时，另一侧与 `tests/test_hot_store.py::test_version_matches_unity_client`
必须同步更新。

协议 v2 语义：不可变 Release（已存在且内容不同的对象拒绝覆盖）、可变
`{test,prod}/{latest,previous}/<platform>/*.json` 指针、断点续传（meta 不匹配
或尺寸异常时自动重传）、发布锁租约（`leaseSeconds`，允许过期后重新获取）。

## 密钥与凭证

不要把 SSH 私钥、证书私钥或 Latest 签名私钥放进本仓库。本机约定目录：

- 发布 SSH 私钥：`~/.myframework-keys/hotdeploy/openssh`（权限 600，
  用户 `hotdeploy`）
- Latest 签名私钥：`~/.myframework-keys/<project>/<env>/latest.pem`

发布窗口与无头入口只保存这些文件的路径。首次连接服务器时必须读取 SSH
主机密钥，并在服务器控制台核对 SHA-256 指纹后确认；后续主机密钥变化会
直接中止连接。

## 验收

```bash
python3 Deploy/HotUpdate/tests/test_hot_store.py
```

线上验收（需要发布凭证）：

1. `ssh -i ~/.myframework-keys/hotdeploy/openssh hotdeploy@47.243.79.140 /usr/local/bin/hot-store check` 返回 `hot-store/2`；
2. `curl https://47.243.79.140/.hot-health` 返回 `ok`；
3. Unity 内运行 `[Explicit]` 测试 `PubSmokeTests.RealServerPublishAndRollback`
   完成上传、回读、Latest 曝光、Previous 回退和 HTTPS 验签。

旧 Base 中冻结的资源地址不会被自动改写；切换服务器后应生产并验证新的
Base、Release 和客户端。
