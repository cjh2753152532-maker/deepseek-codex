# DeepSeek API for Codex（Windows）

[下载 Windows 安装器](https://github.com/cjh2753152532-maker/deepseek-codex/releases/latest)

双击 `DeepSeekForCodex.Setup.exe`，填写 DeepSeek 开放平台的 API 密钥，点击 **保存并安装**。安装器会查找 Windows 版 Codex 的 `codex.exe`，也可以手动浏览选择。使用 **验证密钥** 会查询 DeepSeek 的模型列表，无需发起文本生成请求。

完全退出并重新打开 Codex，开启新聊天。如果插件已显示在插件列表中，确认 **DeepSeek API** 已启用。

> 请用 DeepSeek 回答「你好」，并显示输入、输出和总 token 用量。

插件把这次调用的提示词发给 DeepSeek。DeepSeek API 使用你的 DeepSeek 账户计费并返回本次 token 用量。Codex 自己的对话由当前选用的 Codex 模型处理。默认使用 `deepseek-flash`，也可指定 `deepseek-v4-pro`。默认最多生成 2048 个 token，可按次设置为 1–8192，并可请求开启思考模式。

密钥由 Windows DPAPI 加密，保存在当前账户的 `%LOCALAPPDATA%\DeepSeekForCodex\credential.bin`。重新运行安装程序即可换密钥。完成安装后可以移动安装 EXE，插件源和运行文件保存在当前账户的数据目录。

要移除插件，在 Codex 的插件页面卸载 **DeepSeek API**，或者运行 `codex plugin remove deepseek@deepseek-for-codex`。如需清除本地密钥，再删除上述 `credential.bin`。如需移除本地插件源，运行 `codex plugin marketplace remove deepseek-for-codex`。

## 重新构建

在 Windows PowerShell 中运行 `./source/Build.ps1`。需要 Windows .NET Framework 4.8；使用系统自带的 C# 编译器，无需下载构建依赖。

[DeepSeek API 文档](https://api-docs.deepseek.com/api/create-chat-completion/) · [Codex 插件打包文档](https://developers.openai.com/plugins/build/plugins)
