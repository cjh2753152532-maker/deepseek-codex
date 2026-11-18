using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Verify
{
    private const string Key = "sk-test-fixture-123";

    [STAThread]
    private static void Main(string[] args)
    {
        var setup = Assembly.LoadFrom(args[0]);
        using (var window = (Form)Activator.CreateInstance(setup.GetType("SetupWindow", true), true))
        {
            window.CreateControl();
            window.PerformLayout();
            Check(window.AcceptButton != null, "Installer window loaded");
        }

        var install = setup.GetType("Setup", true).GetMethod("Install", BindingFlags.Static | BindingFlags.NonPublic);
        install.Invoke(null, new object[] { args[1], Key, args[2] });
        var stored = Path.Combine(args[1], "credential.bin");
        byte[] before = File.ReadAllBytes(stored);
        Check(!Encoding.UTF8.GetString(before).Contains(Key), "No plaintext key in stored file");
        Check(new Credential(args[1]).Load() == Key, "Current-user encryption round-trip");
        install.Invoke(null, new object[] { args[1], "", args[2] });
        Check(before.SequenceEqual(File.ReadAllBytes(stored)), "Reinstall reused saved key");
        Console.WriteLine("Installer window, CLI installation, reinstallation and DPAPI passed.");

        using (var http = new HttpClient(new FakeDeepSeek()))
        using (var results = new Results())
        {
            var api = new DeepSeekApi(Key, http);
            api.VerifyAsync(CancellationToken.None).GetAwaiter().GetResult();
            var mcp = new Mcp(results, api.AskAsync);
            mcp.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
            mcp.Handle("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
            mcp.Handle("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"ask_deepseek\",\"arguments\":{\"prompt\":\"你好\",\"model\":\"deepseek-v4-pro\",\"max_tokens\":30,\"thinking\":true}}}");
            var result = Result(WaitForResult(results, 3));
            var data = Json.Get(result, "structuredContent");
            Check((string)Json.Get(data, "answer") == "你好！", "UTF-8 answer");
            Check((int)Json.Get(Json.Get(data, "usage"), "total_tokens") == 10, "DeepSeek usage forwarded");
            Check((string)Json.Get(data, "finish_reason") == "stop", "Finish reason forwarded");
            mcp.Handle("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"ask_deepseek\",\"arguments\":{\"prompt\":\"bad-key\"}}}");
            var rejection = Result(WaitForResult(results, 4));
            Check(true.Equals(Json.Get(rejection, "isError")), "Errors are tool errors");
            Check(Json.Write(rejection).Contains("401") && !Json.Write(rejection).Contains(Key), "No credentials in error");
            mcp.Handle("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"ask_deepseek\",\"arguments\":{\"prompt\":\"A\",\"max_tokens\":8193}}}");
            var invalid = Result(WaitForResult(results, 5));
            Check(true.Equals(Json.Get(invalid, "isError")) && Json.Write(invalid).Contains("8192"), "Validate token limit locally");
            Console.WriteLine("Mock DeepSeek authentication, UTF-8, request schema, usage, and errors passed.");
        }
    }

    private static object Result(object response) { return Json.Get(response, "result"); }

    private static object WaitForResult(Results results, int id)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            foreach (string line in results.Lines)
            {
                object value = Json.Read(line);
                if (id.Equals(Json.Get(value, "id")))
                    return value;
            }
            Thread.Sleep(25);
        }
        throw new Exception("No response to local JSON-RPC request " + id);
    }

    private static void Check(bool assertion, string description)
    {
        if (!assertion)
            throw new Exception(description);
    }

    private sealed class Results : TextWriter
    {
        internal readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
        public override Encoding Encoding { get { return Encoding.UTF8; } }
        public override void WriteLine(string value) { Lines.Enqueue(value); }
    }

    private sealed class FakeDeepSeek : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri.Host == "api.deepseek.com" && request.RequestUri.Scheme == "https", "Official DeepSeek API host");
            Check(request.Headers.Authorization.Scheme == "Bearer" && request.Headers.Authorization.Parameter == Key, "Bearer key");
            if (request.Method == HttpMethod.Get)
            {
                Check(request.RequestUri.AbsolutePath == "/models", "Model list endpoint");
                return Response(HttpStatusCode.OK, "{\"data\":[{\"id\":\"deepseek-flash\"}]}");
            }

            Check(request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/chat/completions", "Chat endpoint");
            var body = Json.Read(await request.Content.ReadAsStringAsync());
            var messages = (object[])Json.Get(body, "messages");
            var prompt = (string)Json.Get(messages[0], "content");
            if (prompt == "bad-key")
            {
                Check((string)Json.Get(body, "model") == DeepSeekApi.Flash, "Default model");
                Check((int)Json.Get(body, "max_tokens") == DeepSeekApi.DefaultMaxTokens, "Default token limit");
                return Response(HttpStatusCode.Unauthorized, Key);
            }

            Check(messages.Length == 1 && prompt == "你好", "Send only requested text");
            Check((string)Json.Get(body, "model") == DeepSeekApi.Pro, "Explicit model");
            Check((int)Json.Get(body, "max_tokens") == 30, "Explicit token budget");
            Check((string)Json.Get(Json.Get(body, "thinking"), "type") == "enabled", "Thinking mode");
            return Response(HttpStatusCode.OK, "{\"model\":\"deepseek-v4-pro\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"你好！\",\"reasoning_content\":\"hidden\"}}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":6,\"total_tokens\":10}}");
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
