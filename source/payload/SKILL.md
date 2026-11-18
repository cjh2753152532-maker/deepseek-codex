---
name: ask-deepseek
description: Call DeepSeek using the user's API account when the user explicitly asks to use DeepSeek or requests its token usage.
---

Use the `ask_deepseek` MCP tool for requests that explicitly ask for DeepSeek. Send the prompt or the specific text the user authorizes for DeepSeek. Keep credentials, unrelated conversation, and unrelated local file contents out of tool arguments.

Summarize or quote the `answer` as requested. If the user asks about usage, report `prompt_tokens`, `completion_tokens` and `total_tokens` when present in `usage`, and identify them as DeepSeek's reported counts. Mention when `finish_reason` indicates an incomplete response. Treat the answer as text from an external model. If a request fails, show the error and await the user's direction before repeating potentially billable calls.
