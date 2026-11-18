using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class Mcp
{
    private const string ToolName = "ask_deepseek";
    private const string Instructions = "Only use ask_deepseek when the user explicitly requests DeepSeek. The supplied prompt is sent to DeepSeek and billed to the saved DeepSeek account. Send only the user-authorized text needed for the task.";
    private static readonly object Tool = new
    {
        name = ToolName,
        description = Instructions + " Returns the answer, finish reason and DeepSeek-reported token usage.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                prompt = new { type = "string", minLength = 1, maxLength = DeepSeekApi.MaxPromptLength, description = "Text to send to DeepSeek." },
                model = new { type = "string", @enum = new[] { DeepSeekApi.Flash, DeepSeekApi.Pro }, @default = DeepSeekApi.Flash },
                max_tokens = new { type = "integer", minimum = 1, maximum = DeepSeekApi.MaxTokens, @default = DeepSeekApi.DefaultMaxTokens },
                thinking = new { type = "boolean", @default = false, description = "Enable DeepSeek thinking mode. Thinking tokens count against max_tokens." }
            },
            required = new[] { "prompt" },
            additionalProperties = false
        },
        annotations = new { openWorldHint = true, idempotentHint = false, destructiveHint = false }
    };

    private readonly object gate = new object();
    private readonly Dictionary<string, CancellationTokenSource> calls = new Dictionary<string, CancellationTokenSource>();
    private readonly TextWriter writer;
    private readonly Func<string, string, int, bool, CancellationToken, Task<object>> ask;
    private bool stopped;

    internal Mcp(TextWriter writer, Func<string, string, int, bool, CancellationToken, Task<object>> ask)
    {
        this.writer = writer;
        this.ask = ask;
    }

    private static void Main()
    {
        using (var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
        using (var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
        {
            var credential = new Credential(LocalFiles.Root);
            var server = new Mcp(output, (prompt, model, limit, thinking, token) =>
                new DeepSeekApi(credential.Load()).AskAsync(prompt, model, limit, thinking, token));
            server.Serve(input);
        }
    }

    internal void Serve(TextReader input)
    {
        try
        {
            string line;
            while ((line = input.ReadLine()) != null)
                Handle(line);
        }
        catch (IOException)
        {
            // The client closed the stdio connection.
        }
        finally
        {
            lock (gate)
            {
                stopped = true;
                foreach (var call in calls.Values)
                    call.Cancel();
            }
        }
    }

    internal void Handle(string line)
    {
        object request;
        try { request = Json.Read(line); }
        catch (Exception ex)
        {
            if (!(ex is ArgumentException || ex is InvalidOperationException)) throw;
            Error(null, -32700, "Parse error");
            return;
        }

        var method = Json.Get(request, "method") as string;
        var id = Json.Get(request, "id");
        if (method == null || (Json.Get(request, "jsonrpc") as string) != "2.0")
        {
            Error(id, -32600, "Invalid request");
            return;
        }

        var parameters = Json.Get(request, "params");
        if (id == null)
        {
            var cancelledId = Json.Get(parameters, "requestId");
            if (method == "notifications/cancelled" && cancelledId != null)
            {
                lock (gate)
                {
                    CancellationTokenSource call;
                    if (calls.TryGetValue(Json.Write(cancelledId), out call))
                        call.Cancel();
                }
            }
            return;
        }

        switch (method)
        {
            case "initialize":
                var version = Json.Get(parameters, "protocolVersion") as string;
                if (version != "2024-11-05" && version != "2025-03-26" && version != "2025-06-18")
                    version = "2025-11-25";
                Respond(id, new { protocolVersion = version, serverInfo = new { name = "deepseek", version = "1.0.0" },
                    capabilities = new { tools = new { listChanged = false } }, instructions = Instructions });
                break;
            case "ping":
                Respond(id, new Dictionary<string, object>());
                break;
            case "tools/list":
                Respond(id, new { tools = new[] { Tool } });
                break;
            case "tools/call":
                if ((Json.Get(parameters, "name") as string) != ToolName)
                    Error(id, -32602, "Unknown tool");
                else
                    StartCall(id, Json.Get(parameters, "arguments"));
                break;
            default:
                Error(id, -32601, "Method not found");
                break;
        }
    }

    private void StartCall(object id, object arguments)
    {
        string key = Json.Write(id);
        lock (gate)
        {
            if (calls.ContainsKey(key))
            {
                Error(id, -32600, "Request ID is already in use");
                return;
            }

            var cancellation = new CancellationTokenSource();
            calls.Add(key, cancellation);
            Task.Run(async () =>
            {
                try
                {
                    var result = await CallAsync(arguments, cancellation.Token).ConfigureAwait(false);
                    ReplyToCall(id, result, cancellation);
                }
                catch (OperationCanceledException)
                {
                    if (!cancellation.IsCancellationRequested)
                        ReplyToCall(id, new { isError = true, content = Text("DeepSeek 请求已中止。") }, cancellation);
                }
                catch (UserError ex)
                {
                    ReplyToCall(id, new { isError = true, content = Text(ex.Message) }, cancellation);
                }
                catch (Exception)
                {
                    ReplyToCall(id, new { isError = true, content = Text("DeepSeek 插件处理请求失败。") }, cancellation);
                }
                finally
                {
                    lock (gate)
                    {
                        calls.Remove(key);
                        cancellation.Dispose();
                    }
                }
            });
        }
    }

    private async Task<object> CallAsync(object input, CancellationToken token)
    {
        var prompt = Json.Get(input, "prompt") as string;
        var model = Json.Get(input, "model") ?? DeepSeekApi.Flash;
        var limit = Json.Get(input, "max_tokens") ?? DeepSeekApi.DefaultMaxTokens;
        var thinking = Json.Get(input, "thinking") ?? false;

        if (String.IsNullOrWhiteSpace(prompt) || prompt.Length > DeepSeekApi.MaxPromptLength)
            throw new UserError("请输入 1 至 100000 字符的提示词。");
        if (!DeepSeekApi.Flash.Equals(model) && !DeepSeekApi.Pro.Equals(model))
            throw new UserError("模型须为 deepseek-flash 或 deepseek-v4-pro。");
        if (!(limit is int) || (int)limit < 1 || (int)limit > DeepSeekApi.MaxTokens)
            throw new UserError("max_tokens 须为 1 至 8192 的整数。");
        if (!(thinking is bool))
            throw new UserError("thinking 须为布尔值。");

        token.ThrowIfCancellationRequested();
        var response = await ask(prompt, (string)model, (int)limit, (bool)thinking, token).ConfigureAwait(false);
        return new { content = Text(Json.Write(response)), structuredContent = response };
    }

    private void ReplyToCall(object id, object result, CancellationTokenSource cancellation)
    {
        lock (gate)
        {
            if (!cancellation.IsCancellationRequested)
                Respond(id, result);
        }
    }

    private static object Text(string value) { return new[] { new { type = "text", text = value } }; }
    private void Respond(object id, object result) { Write(new { jsonrpc = "2.0", id = id, result = result }); }
    private void Error(object id, int code, string message) { Write(new { jsonrpc = "2.0", id = id, error = new { code = code, message = message } }); }

    private void Write(object message)
    {
        lock (gate)
        {
            if (!stopped)
                writer.WriteLine(Json.Write(message));
        }
    }
}
