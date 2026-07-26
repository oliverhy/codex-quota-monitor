# Codex Quota Monitor for Windows

一个轻量的 Windows 常驻小程序，用于查看 Codex 剩余额度、重置额度和 Token 用量。

## 功能

- 窗口置顶、拖动和系统托盘常驻
- 显示 5 小时/周额度剩余百分比
- 显示普通额度的下次重置时间
- 显示可用重置额度次数
- 显示每张重置额度的精确到秒到期时间
- 支持执行一次额度重置
- 记录输入、输出、推理和缓存命中 Token
- 计算缓存命中率，并支持实时、今天、昨天、近 7 天、总计和自定义日期查询

## 数据来源

程序通过本机 Codex `app-server` 的 `account/rateLimits/read` 和 `account/usage/read` 方法读取数据，不保存或上传登录 Token。

重置额度到期时间取自官方响应中的 `rateLimitResetCredits.credits[].expiresAt` 字段，按本地时区显示为 `yyyy-MM-dd HH:mm:ss`。

## 构建

要求 Windows 和 .NET Framework 4.x（系统通常已自带）。

在本目录运行：

```powershell
./build.ps1
```

构建产物会写入上级 `outputs` 目录。

## 使用

先安装并登录 Codex，再运行构建出的 `CodexQuota-Token-v5.exe`。程序只读取当前本机 Codex 会话；如果看不到数据，请先启动或登录 Codex。

## 免责声明

这是个人开源工具，接口字段可能随 Codex 更新而变化。请勿把构建产物或本地账号数据提交到公开仓库。
