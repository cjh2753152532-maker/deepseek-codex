using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Json
{
    private static JavaScriptSerializer Serializer()
    {
        return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 32 };
    }

    internal static string Write(object value) { return Serializer().Serialize(value); }
    internal static object Read(string value) { return Serializer().DeserializeObject(value); }

    internal static object Get(object value, string name)
    {
        var map = value as Dictionary<string, object>;
        object field;
        return map != null && map.TryGetValue(name, out field) ? field : null;
    }
}

internal static class LocalFiles
{
    internal static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekForCodex");

    internal static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

internal sealed class Credential
{
    private readonly string path;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeepSeekForCodex/credential/v1");

    internal Credential(string root) { path = Path.Combine(root, "credential.bin"); }
    internal bool Exists { get { return File.Exists(path); } }

    internal static string Validate(string value)
    {
        value = (value ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Za-z0-9._~-]{8,512}\z"))
            throw new UserError("请输入格式有效的 DeepSeek API 密钥。");
        return value;
    }

    internal string Load()
    {
        byte[] clear;
        try
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var buffer = new MemoryStream())
            {
                input.CopyTo(buffer);
                clear = ProtectedData.Unprotect(buffer.ToArray(), Entropy, DataProtectionScope.CurrentUser);
            }
        }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException)) throw;
            throw new UserError("当前 Windows 账户无法读取 DeepSeek 密钥。请重新打开安装程序并保存密钥。");
        }
        try { return Validate(Encoding.UTF8.GetString(clear)); }
        finally { Array.Clear(clear, 0, clear.Length); }
    }

    internal void Save(string key)
    {
        byte[] clear = Encoding.UTF8.GetBytes(Validate(key));
        try
        {
            LocalFiles.WriteAtomically(path, ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            Array.Clear(clear, 0, clear.Length);
        }
    }
}

internal sealed class UserError : Exception
{
    internal UserError(string message) : base(message) { }
}

internal sealed class DeepSeekApi
{
    internal const string Flash = "deepseek-flash";
    internal const string Pro = "deepseek-v4-pro";
    internal const int DefaultMaxTokens = 2048;
    internal const int MaxTokens = 8192;
    internal const int MaxPromptLength = 100000;
    private static readonly Uri Endpoint = new Uri("https://api.deepseek.com/");
    private static readonly HttpClient LiveClient = CreateClient();
    private readonly HttpClient client;
    private readonly string key;

    internal DeepSeekApi(string key, HttpClient client = null)
    {
        this.key = Credential.Validate(key);
        this.client = client ?? LiveClient;
    }

    private static HttpClient CreateClient()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(50),
            MaxResponseContentBufferSize = 2 * 1024 * 1024
        };
    }

    internal async Task VerifyAsync(CancellationToken token)
    {
        var response = await SendAsync(HttpMethod.Get, "models", null, token).ConfigureAwait(false);
        if (!(Json.Get(response, "data") is object[]))
            throw new UserError("DeepSeek 返回了无法识别的模型列表。");
    }

    internal async Task<object> AskAsync(string prompt, string model, int maxTokens, bool thinking, CancellationToken token)
    {
        var body = new
        {
            model = model,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
            max_tokens = maxTokens,
            thinking = new { type = thinking ? "enabled" : "disabled" }
        };
        var response = await SendAsync(HttpMethod.Post, "chat/completions", body, token).ConfigureAwait(false);
        var choices = Json.Get(response, "choices") as object[];
        if (choices == null || choices.Length == 0)
            throw new UserError("DeepSeek 没有返回回答。");
        var answer = Json.Get(Json.Get(choices[0], "message"), "content") as string;
        var finishReason = Json.Get(choices[0], "finish_reason") as string;
        return new
        {
            model = Json.Get(response, "model") as string ?? model,
            answer = answer ?? "",
            finish_reason = finishReason,
            usage = Json.Get(response, "usage") as Dictionary<string, object>
        };
    }

    private async Task<object> SendAsync(HttpMethod method, string path, object body, CancellationToken token)
    {
        using (var request = new HttpRequestMessage(method, new Uri(Endpoint, path)))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (body != null)
                request.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
            try
            {
                using (var response = await client.SendAsync(request, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new UserError(StatusMessage((int)response.StatusCode));
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    try { return Json.Read(text); }
                    catch (Exception ex)
                    {
                        if (!(ex is ArgumentException || ex is InvalidOperationException)) throw;
                        throw new UserError("DeepSeek 返回了无法解析的数据。");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) throw;
                throw new UserError("DeepSeek 请求超时。生成请求可能已开始计费，请按需重新调用。");
            }
            catch (HttpRequestException)
            {
                throw new UserError("无法连接 DeepSeek 或接收其响应。请检查网络连接。");
            }
        }
    }

    private static string StatusMessage(int status)
    {
        switch (status)
        {
            case 400:
            case 422: return "DeepSeek 拒绝了请求参数（HTTP " + status + "）。";
            case 401:
            case 403: return "DeepSeek 拒绝了密钥（HTTP " + status + "）。请核对密钥及权限。";
            case 402: return "DeepSeek 账户余额不足（HTTP 402）。";
            case 429: return "DeepSeek 请求过于频繁（HTTP 429）。请稍后再试。";
            default: return "DeepSeek 请求失败（HTTP " + status + "）。";
        }
    }
}
