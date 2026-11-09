using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyEditor.Mcp
{
    /// <summary>
    /// Loopback MCP server. Speaks JSON-RPC over a small HTTP listener on 127.0.0.1
    /// so a client can attach to the already running editor.
    /// TcpListener is used instead of HttpListener: HTTP.sys refuses localhost
    /// prefixes on this machine with a sharing-violation error.
    /// </summary>
    internal sealed class FrostyMcpServer : IDisposable
    {
        private const string ProtocolVersion = "2025-03-26";
        private const int MaxHeaderBytes = 65536;
        private const int MaxBodyBytes = 2 * 1024 * 1024;

        private static readonly byte[] HeaderTerminator = { 13, 10, 13, 10 };

        private readonly object m_sync = new object();
        private TcpListener m_listener;
        private Thread m_thread;
        private volatile bool m_running;
        private int m_port;

        public bool IsRunning => m_running;
        public int Port => m_port;

        public void Start(int port)
        {
            lock (m_sync)
            {
                if (m_running)
                    return;

                Exception lastError = null;
                foreach (int candidate in PortCandidates(port))
                {
                    TcpListener listener = null;
                    try
                    {
                        listener = new TcpListener(IPAddress.Loopback, candidate);
                        listener.Start();

                        m_listener = listener;
                        m_port = candidate;
                        m_running = true;
                        m_thread = new Thread(Listen)
                        {
                            IsBackground = true,
                            Name = "FrostyMcp"
                        };
                        m_thread.Start();
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (listener != null)
                        {
                            try { listener.Stop(); }
                            catch { /* bind failed before the listener was usable */ }
                        }
                    }
                }

                throw new IOException("No loopback port could be opened. " + (lastError == null ? "" : lastError.Message), lastError);
            }
        }

        private static int[] PortCandidates(int preferred)
        {
            int[] fallbacks = { 24816, 27182, 4040, 8765, 9090, 54321, 50080 };
            List<int> ports = new List<int>();
            if (preferred >= 1024 && preferred <= 65535)
                ports.Add(preferred);
            foreach (int fallback in fallbacks)
            {
                if (!ports.Contains(fallback))
                    ports.Add(fallback);
            }
            return ports.ToArray();
        }

        public void Stop()
        {
            Thread thread;
            lock (m_sync)
            {
                m_running = false;
                TcpListener listener = m_listener;
                m_listener = null;
                thread = m_thread;
                m_thread = null;
                if (listener != null)
                {
                    try { listener.Stop(); }
                    catch { /* listener is already stopped */ }
                }
            }

            if (thread != null && thread != Thread.CurrentThread)
                thread.Join(2000);
        }

        public void Dispose() => Stop();

        private void Listen()
        {
            while (m_running)
            {
                TcpListener listener = m_listener;
                if (listener == null)
                    break;

                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    break;
                }

                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        private static void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    string method;
                    string path;
                    string body;
                    Dictionary<string, string> headers;
                    if (!TryReadRequest(stream, out method, out path, out headers, out body))
                    {
                        WriteText(stream, 400, "Bad Request", "application/json", "{\"error\":\"bad request\"}");
                        return;
                    }

                    if (!IsTrusted(headers))
                    {
                        WriteText(stream, 403, "Forbidden", "application/json", "{\"error\":\"forbidden origin\"}");
                        return;
                    }

                    if (!path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteText(stream, 404, "Not Found", "application/json", "{\"error\":\"not found\"}");
                        return;
                    }

                    if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteText(stream, 204, "No Content", null, "");
                        return;
                    }

                    if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteText(stream, 200, "OK", null, "");
                        return;
                    }

                    if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(stream, 405, "Method Not Allowed", RpcError(null, -32000, "This server does not stream server messages."), false);
                        return;
                    }

                    if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(stream, 405, "Method Not Allowed", RpcError(null, -32000, "Method not allowed."), false);
                        return;
                    }

                    JToken parsed;
                    try
                    {
                        parsed = string.IsNullOrWhiteSpace(body) ? null : JToken.Parse(body);
                    }
                    catch (JsonException)
                    {
                        WriteJson(stream, 400, "Bad Request", RpcError(null, -32700, "Parse error."), false);
                        return;
                    }

                    if (parsed == null)
                    {
                        WriteJson(stream, 400, "Bad Request", RpcError(null, -32700, "Parse error."), false);
                        return;
                    }

                    bool sse = WantsEventStream(headers);
                    JArray batch = parsed as JArray;
                    if (batch != null)
                    {
                        JArray replies = new JArray();
                        foreach (JToken item in batch)
                        {
                            JObject reply = Dispatch(item as JObject);
                            if (reply != null)
                                replies.Add(reply);
                        }

                        if (replies.Count == 0)
                        {
                            WriteText(stream, 202, "Accepted", null, "");
                            return;
                        }

                        WriteJson(stream, 200, "OK", replies, sse);
                        return;
                    }

                    JObject single = Dispatch(parsed as JObject);
                    if (single == null)
                    {
                        WriteText(stream, 202, "Accepted", null, "");
                        return;
                    }

                    WriteJson(stream, 200, "OK", single, sse);
                }
            }
            catch
            {
                try { client.Close(); }
                catch { /* socket already closed */ }
            }
        }

        private static bool TryReadRequest(NetworkStream stream, out string method, out string path, out Dictionary<string, string> headers, out string body)
        {
            method = null;
            path = null;
            headers = null;
            body = null;

            MemoryStream raw = new MemoryStream();
            byte[] buffer = new byte[8192];
            int headerEnd = -1;
            while (raw.Length < MaxHeaderBytes && headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    return false;
                raw.Write(buffer, 0, read);
                headerEnd = IndexOf(raw.GetBuffer(), (int)raw.Length, HeaderTerminator);
            }

            if (headerEnd < 0)
                return false;

            byte[] data = raw.GetBuffer();
            int length = (int)raw.Length;
            string headerText = Encoding.ASCII.GetString(data, 0, headerEnd);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                return false;

            string[] request = lines[0].Split(' ');
            if (request.Length < 2)
                return false;

            method = request[0];
            path = request[1];
            int query = path.IndexOf('?');
            if (query >= 0)
                path = path.Substring(0, query);

            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                    continue;
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            int contentLength = 0;
            string lengthHeader;
            if (headers.TryGetValue("Content-Length", out lengthHeader))
                int.TryParse(lengthHeader, out contentLength);
            if (contentLength < 0)
                contentLength = 0;
            if (contentLength > MaxBodyBytes)
                return false;

            int bodyStart = headerEnd + HeaderTerminator.Length;
            int already = length - bodyStart;
            if (already < 0)
                already = 0;
            if (already > contentLength)
                already = contentLength;

            byte[] payload = new byte[contentLength];
            if (already > 0)
                Buffer.BlockCopy(data, bodyStart, payload, 0, already);

            int offset = already;
            while (offset < contentLength)
            {
                int read = stream.Read(payload, offset, contentLength - offset);
                if (read <= 0)
                    break;
                offset += read;
            }

            body = Encoding.UTF8.GetString(payload, 0, offset);
            return true;
        }

        private static int IndexOf(byte[] data, int length, byte[] pattern)
        {
            int last = length - pattern.Length;
            for (int i = 0; i <= last; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Returns a JSON-RPC response, or null when the message is a notification.
        /// </summary>
        private static JObject Dispatch(JObject message)
        {
            if (message == null)
                return RpcError(null, -32600, "Invalid request.");

            JToken id = message["id"];
            bool notification = id == null || id.Type == JTokenType.Null;
            string method = message["method"]?.ToString();
            if (string.IsNullOrEmpty(method))
                return notification ? null : RpcError(id, -32600, "Invalid request.");

            JObject parameters = message["params"] as JObject;

            try
            {
                switch (method)
                {
                    case "initialize":
                        return RpcResult(id, Initialize(parameters));
                    case "notifications/initialized":
                    case "notifications/cancelled":
                        return null;
                    case "ping":
                        return RpcResult(id, new JObject());
                    case "tools/list":
                        return RpcResult(id, new JObject { ["tools"] = FrostyMcpTools.Definitions() });
                    case "tools/call":
                        return RpcResult(id, CallTool(parameters));
                    default:
                        if (method.StartsWith("notifications/", StringComparison.Ordinal))
                            return null;
                        return notification ? null : RpcError(id, -32601, "Method not found.");
                }
            }
            catch (Exception ex)
            {
                if (notification)
                    return null;
                return RpcError(id, -32603, ex.Message);
            }
        }

        private static JObject Initialize(JObject parameters)
        {
            string version = parameters?["protocolVersion"]?.ToString();
            if (string.IsNullOrEmpty(version))
                version = ProtocolVersion;

            return new JObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false }
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = "frosty-editor",
                    ["version"] = "1.0.0"
                },
                ["instructions"] = "Read and edit the game database open in Frosty Editor. list_folder browses one level of the EBX tree, search_assets finds assets, get_asset reads one asset, and get_object reads a single object. get_asset_metadata and get_type_info describe assets and classes. get_array_slice reads an array page, including references past the get_asset truncation limit, and does not modify the project. set_object_field, set_object_fields, and the array tools edit one object. clone_object, create_object, and remove_object change arrays. clone_layer_object and remove_layer_object edit a LayerData Objects array. validate_project does not modify data. undo_last_change reverses the latest MCP edit. save_project and export_mod write the open project."
            };
        }

        private static JObject CallTool(JObject parameters)
        {
            string name = parameters?["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return ToolError("Tool name is required.");

            JToken arguments = parameters?["arguments"];
            JObject args;
            if (arguments == null || arguments.Type == JTokenType.Null)
                args = new JObject();
            else
            {
                args = arguments as JObject;
                if (args == null)
                    return ToolError("Tool arguments must be an object.");
            }

            try
            {
                JObject data = FrostyMcpTools.Call(name, args);
                return ToolText(data.ToString(Formatting.None), false);
            }
            catch (FrostyMcpToolException ex)
            {
                return ToolText(new JObject { ["error"] = ex.Message }.ToString(Formatting.None), true);
            }
            catch (Exception ex)
            {
                return ToolText(new JObject { ["error"] = ex.Message }.ToString(Formatting.None), true);
            }
        }

        private static JObject ToolText(string text, bool isError)
        {
            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = text
                    }
                },
                ["isError"] = isError
            };
        }

        private static JObject ToolError(string message) => ToolText(new JObject { ["error"] = message }.ToString(Formatting.None), true);

        private static JObject RpcResult(JToken id, JToken result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = result
            };
        }

        private static JObject RpcError(JToken id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message ?? "Error"
                }
            };
        }

        private static bool WantsEventStream(Dictionary<string, string> headers)
        {
            string accept;
            if (headers == null || !headers.TryGetValue("Accept", out accept) || string.IsNullOrEmpty(accept))
                return false;

            bool json = accept.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0;
            bool stream = accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
            return stream && !json;
        }

        private static bool IsTrusted(Dictionary<string, string> headers)
        {
            string host;
            if (headers != null && headers.TryGetValue("Host", out host) && !string.IsNullOrEmpty(host))
            {
                string hostOnly = host;
                int colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(']') < 0)
                    hostOnly = host.Substring(0, colon);
                else if (host.StartsWith("[", StringComparison.Ordinal) && host.Contains("]"))
                    hostOnly = host.Substring(1, host.IndexOf(']') - 1);

                if (!hostOnly.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                    !hostOnly.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                    hostOnly != "::1")
                    return false;
            }

            string origin;
            if (headers == null || !headers.TryGetValue("Origin", out origin) || string.IsNullOrEmpty(origin))
                return true;

            Uri uri;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out uri))
                return true;
            if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return true;

            return uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host == "::1";
        }

        private static void WriteJson(NetworkStream stream, int status, string reason, JToken payload, bool eventStream)
        {
            string json = payload.ToString(Formatting.None);
            if (eventStream)
            {
                WriteText(stream, status, reason, "text/event-stream", "event: message\ndata: " + json + "\n\n");
                return;
            }

            WriteText(stream, status, reason, "application/json", json);
        }

        private static void WriteText(NetworkStream stream, int status, string reason, string contentType, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text ?? "");
            StringBuilder header = new StringBuilder();
            header.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            if (!string.IsNullOrEmpty(contentType))
                header.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
            header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            header.Append("Access-Control-Allow-Origin: *\r\n");
            header.Append("Access-Control-Allow-Methods: POST, GET, DELETE, OPTIONS\r\n");
            header.Append("Access-Control-Allow-Headers: Content-Type, Accept, Mcp-Session-Id, MCP-Protocol-Version, Last-Event-ID\r\n");
            header.Append("Connection: close\r\n\r\n");

            byte[] head = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(head, 0, head.Length);
            if (body.Length > 0)
                stream.Write(body, 0, body.Length);
        }
    }
}
