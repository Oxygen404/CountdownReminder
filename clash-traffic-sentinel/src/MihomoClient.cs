using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClashTrafficSentinel
{
    internal sealed class MihomoException : Exception
    {
        public MihomoException(string message) : base(message) { }
        public MihomoException(string message, Exception inner) : base(message, inner) { }
    }

    internal sealed class NamedPipeMihomoClient
    {
        private const string PipeName = "verge-mihomo";
        private const int MaxResponseBytes = 32 * 1024 * 1024;
        private readonly JavaScriptSerializer serializer;

        public NamedPipeMihomoClient()
        {
            serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaxResponseBytes;
        }

        public Task<MihomoSnapshot> FetchAsync()
        {
            return Task.Run(delegate { return Fetch(); });
        }

        private MihomoSnapshot Fetch()
        {
            string secret = LoadSecret();
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(1500);
                    WriteRequest(pipe, secret);
                    byte[] body = ReadResponse(pipe);
                    string json = Encoding.UTF8.GetString(body);
                    return ParseSnapshot(json);
                }
            }
            catch (TimeoutException ex)
            {
                throw new MihomoException("Clash Verge 尚未运行", ex);
            }
            catch (IOException ex)
            {
                throw new MihomoException("正在等待 Clash Verge", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new MihomoException("无法访问 Clash 本地接口", ex);
            }
        }

        private static void WriteRequest(Stream stream, string secret)
        {
            using (StreamWriter writer = new StreamWriter(stream, Encoding.ASCII, 1024, true))
            {
                writer.NewLine = "\r\n";
                writer.WriteLine("GET /connections HTTP/1.1");
                writer.WriteLine("Host: localhost");
                if (!string.IsNullOrEmpty(secret))
                    writer.WriteLine("Authorization: Bearer " + secret);
                writer.WriteLine("Connection: close");
                writer.WriteLine();
                writer.Flush();
            }
        }

        private static byte[] ReadResponse(Stream stream)
        {
            string status = ReadAsciiLine(stream);
            if (string.IsNullOrEmpty(status)) throw new MihomoException("Clash 本地接口没有响应");

            bool chunked = false;
            int contentLength = -1;
            string line;
            while (!string.IsNullOrEmpty(line = ReadAsciiLine(stream)))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) chunked = true;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength);
            }

            if (status.IndexOf(" 401 ", StringComparison.Ordinal) >= 0 ||
                status.IndexOf(" 403 ", StringComparison.Ordinal) >= 0)
                throw new MihomoException("Clash 本地认证失败，请重启 Clash Verge");
            if (status.IndexOf(" 200 ", StringComparison.Ordinal) < 0)
                throw new MihomoException("Clash 本地接口暂时不可用");

            if (chunked) return ReadChunkedBody(stream);
            if (contentLength < 0 || contentLength > MaxResponseBytes)
                throw new MihomoException("Clash 返回的数据大小异常");
            return ReadExact(stream, contentLength);
        }

        private static byte[] ReadChunkedBody(Stream stream)
        {
            using (MemoryStream body = new MemoryStream())
            {
                while (true)
                {
                    string sizeLine = ReadAsciiLine(stream);
                    if (string.IsNullOrWhiteSpace(sizeLine)) continue;
                    int semicolon = sizeLine.IndexOf(';');
                    string hex = semicolon >= 0 ? sizeLine.Substring(0, semicolon) : sizeLine;
                    int size;
                    if (!int.TryParse(hex.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size) || size < 0)
                        throw new MihomoException("Clash 返回了无法识别的数据块");
                    if (size == 0) break;
                    if (body.Length + size > MaxResponseBytes)
                        throw new MihomoException("Clash 返回的数据超过安全上限");
                    byte[] chunk = ReadExact(stream, size);
                    body.Write(chunk, 0, chunk.Length);
                    ReadAsciiLine(stream);
                }
                return body.ToArray();
            }
        }

        private static byte[] ReadExact(Stream stream, int length)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(buffer, offset, length - offset);
                if (read <= 0) throw new MihomoException("Clash 本地接口连接中断");
                offset += read;
            }
            return buffer;
        }

        private static string ReadAsciiLine(Stream stream)
        {
            using (MemoryStream line = new MemoryStream())
            {
                while (line.Length < 8192)
                {
                    int value = stream.ReadByte();
                    if (value < 0) break;
                    if (value == 10) break;
                    if (value != 13) line.WriteByte((byte)value);
                }
                if (line.Length >= 8192) throw new MihomoException("Clash 响应头过长");
                return Encoding.ASCII.GetString(line.ToArray());
            }
        }

        internal MihomoSnapshot ParseSnapshot(string json)
        {
            try
            {
                Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) throw new InvalidDataException();
                MihomoSnapshot snapshot = new MihomoSnapshot();
                snapshot.UploadTotal = GetLong(root, "uploadTotal");
                snapshot.DownloadTotal = GetLong(root, "downloadTotal");

                object connectionsValue;
                object[] connections = root.TryGetValue("connections", out connectionsValue)
                    ? connectionsValue as object[] : null;
                if (connections == null) return snapshot;

                foreach (object item in connections)
                {
                    Dictionary<string, object> connection = item as Dictionary<string, object>;
                    if (connection == null) continue;
                    Dictionary<string, object> metadata = GetDictionary(connection, "metadata");
                    string process = GetString(metadata, "process");
                    string processPath = GetString(metadata, "processPath");
                    string appName = TrafficIdentity.NormalizeAppName(process, processPath);
                    string appPath = TrafficIdentity.NormalizeAppPath(processPath, appName);
                    ConnectionSample sample = new ConnectionSample();
                    sample.Id = GetString(connection, "id") ?? string.Empty;
                    sample.AppName = appName;
                    sample.AppPath = appPath;
                    sample.Domain = TrafficIdentity.NormalizeDomain(
                        GetString(metadata, "host"), GetString(metadata, "destinationIP"));
                    sample.Upload = Math.Max(0, GetLong(connection, "upload"));
                    sample.Download = Math.Max(0, GetLong(connection, "download"));
                    sample.IsProxy = IsProxyChain(connection);
                    if (sample.Id.Length > 0) snapshot.Connections.Add(sample);
                }
                return snapshot;
            }
            catch (MihomoException) { throw; }
            catch (Exception ex)
            {
                throw new MihomoException("Clash 返回的数据格式发生变化", ex);
            }
        }

        internal static bool IsProxyChain(Dictionary<string, object> connection)
        {
            object chainsValue;
            object[] chains = connection != null && connection.TryGetValue("chains", out chainsValue)
                ? chainsValue as object[] : null;
            if (chains == null || chains.Length == 0) return false;
            foreach (object value in chains)
            {
                string chain = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (string.Equals(chain, "DIRECT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(chain, "REJECT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(chain, "REJECT-DROP", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        internal static string ParseYamlScalar(string value)
        {
            if (value == null) return string.Empty;
            string text = value.Trim();
            if (text.Length >= 2 && text[0] == '\'' && text[text.Length - 1] == '\'')
                return text.Substring(1, text.Length - 2).Replace("''", "'");
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                return text.Substring(1, text.Length - 2)
                    .Replace("\\\"", "\"").Replace("\\\\", "\\");
            int comment = text.IndexOf(" #", StringComparison.Ordinal);
            return (comment >= 0 ? text.Substring(0, comment) : text).Trim();
        }

        private static string LoadSecret()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string path = Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev", "clash-verge.yaml");
            if (!File.Exists(path)) throw new MihomoException("未找到 Clash Verge 配置");
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    if (!line.Substring(0, colon).Trim().Equals("secret", StringComparison.OrdinalIgnoreCase)) continue;
                    string secret = ParseYamlScalar(line.Substring(colon + 1));
                    if (secret.IndexOf('\r') >= 0 || secret.IndexOf('\n') >= 0)
                        throw new MihomoException("Clash 本地密钥格式异常");
                    return secret;
                }
                return string.Empty;
            }
            catch (MihomoException) { throw; }
            catch (Exception ex)
            {
                throw new MihomoException("无法读取 Clash Verge 配置", ex);
            }
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> data, string key)
        {
            if (data == null) return null;
            object value;
            return data.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (data == null) return null;
            object value;
            return data.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static long GetLong(Dictionary<string, object> data, string key)
        {
            if (data == null) return 0;
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return 0;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }
}

