using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace glasslinq.bridge
{
    class Program
    {
        private static Stream? _stdout;
        private static readonly object _writeLock = new object();
        private static string logPath = @"C:\Temp\glasslinq_bridge_debug.txt";

        // Runtime reply state — shared between the Chrome stdin loop and RunRuntimePipe
        private static readonly object _pendingReplies = new object();
        private static ManualResetEventSlim? _pendingReplyEvent = null;
        private static string? _pendingReplyJson = null;

        static void Main(string[] args)
        {
            // Ensure log directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));

            try
            {
                _stdout = Console.OpenStandardOutput();
                LogMessage("Bridge Started");

                // Start Studio listeners in background
                Task.Run(() => StartStudioListener());

                // Main loop: Listen for messages FROM Chrome
                using (Stream stdin = Console.OpenStandardInput())
                {
                    while (true)
                    {
                        try
                        {
                            // Read 4-byte length prefix
                            byte[] lengthBytes = new byte[4];
                            int read = stdin.Read(lengthBytes, 0, 4);
                            if (read < 4)
                            {
                                LogMessage("Chrome disconnected (EOF on stdin)");
                                break;
                            }

                            int length = BitConverter.ToInt32(lengthBytes, 0);

                            // Validate message length
                            if (length <= 0 || length > 10 * 1024 * 1024) // 10MB max
                            {
                                LogMessage($"Invalid message length: {length}");
                                continue;
                            }

                            // Read message content
                            byte[] buffer = new byte[length];
                            int bytesRead = 0;
                            while (bytesRead < length)
                            {
                                read = stdin.Read(buffer, bytesRead, length - bytesRead);
                                if (read == 0)
                                {
                                    LogMessage("Incomplete message received");
                                    break;
                                }
                                bytesRead += read;
                            }

                            if (bytesRead < length) continue;

                            string message = Encoding.UTF8.GetString(buffer);
                            LogMessage($"FROM CHROME: {message}");

                            // Design-time spy responses — forward to SpyOverlayWindow
                            if (message.Contains("element_hovered") ||
                                message.Contains("element_captured"))
                            {
                                string messageToForward = message;
                                Task.Run(() => SendToStudio(messageToForward));
                            }
                            // Runtime activity responses — signal the waiting RunRuntimePipe thread
                            else if (message.Contains("CLICK_RESPONSE") ||
                                     message.Contains("GET_TEXT_RESPONSE") ||
                                     message.Contains("TYPE_INTO_RESPONSE"))
                            {
                                lock (_pendingReplies)
                                {
                                    _pendingReplyJson = message;
                                    _pendingReplyEvent?.Set();
                                }
                            }
                            else if (message.Contains("ping"))
                            {
                                // Respond to heartbeat
                                SendMessage(new { action = "pong", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                            }
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"Error reading from Chrome: {ex.Message}");
                            Thread.Sleep(100);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"FATAL ERROR: {ex}");
            }
            finally
            {
                LogMessage("Bridge Stopped");
            }
        }

        /// <summary>
        /// Send message TO Chrome Extension via stdout (native messaging protocol).
        /// </summary>
        private static void SendMessage(object message)
        {
            if (_stdout == null) return;

            lock (_writeLock)
            {
                try
                {
                    string json = JsonSerializer.Serialize(message);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    byte[] length = BitConverter.GetBytes(bytes.Length);

                    _stdout.Write(length, 0, 4);
                    _stdout.Write(bytes, 0, bytes.Length);
                    _stdout.Flush();

                    LogMessage($"TO CHROME: {json}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error sending to Chrome: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Forwards a raw JSON string from Studio directly to Chrome via stdout.
        /// </summary>
        private static void ForwardToChrome(string jsonMessage)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(jsonMessage);
                byte[] len = BitConverter.GetBytes(bytes.Length);

                lock (_writeLock)
                {
                    if (_stdout != null)
                    {
                        _stdout.Write(len, 0, 4);
                        _stdout.Write(bytes, 0, bytes.Length);
                        _stdout.Flush();
                        LogMessage($"SENT TO CHROME: {jsonMessage}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to send message to Chrome: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts both Studio pipe listeners in parallel.
        /// </summary>
        private static void StartStudioListener()
        {
            LogMessage("Studio listeners starting");
            Task.Run(() => RunSpyPipe());
            Task.Run(() => RunRuntimePipe());
        }

        /// <summary>
        /// Bidirectional pipe for SpyOverlayWindow design-time commands AND any runtime
        /// activity command (CLICK, TYPE_INTO, GET_TEXT) that mistakenly connects here
        /// instead of GlassLinqBridge.
        ///
        /// Spy commands (start_web_spy, stop_web_spy, web_spy_request) are fire-and-forget:
        /// forwarded to Chrome with no reply written back to the caller.
        ///
        /// Runtime commands are handled identically to RunRuntimePipe: forwarded to Chrome,
        /// blocked up to 8 s for a Chrome response, then the reply is written back on the
        /// same pipe connection so the activity does not time out.
        /// </summary>
        private static void RunSpyPipe()
        {
            LogMessage("Spy pipe listening on GlassLinqPipe");

            while (true)
            {
                try
                {
                    // InOut so we can write the runtime reply back on the same connection.
                    // SpyOverlayWindow opens this pipe as PipeDirection.Out, which is
                    // compatible with a server that is InOut.
                    using var server = new NamedPipeServerStream(
                        "GlassLinqPipe",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte);

                    server.WaitForConnection();

                    using var reader = new StreamReader(server);
                    using var writer = new StreamWriter(server) { AutoFlush = true };

                    string? cmd = reader.ReadLine();
                    if (string.IsNullOrEmpty(cmd)) continue;

                    LogMessage($"FROM STUDIO (spy): {cmd}");

                    // ── Design-time spy commands — fire-and-forget ───────────
                    if (cmd.Contains("web_spy_request") ||
                        cmd.Contains("start_web_spy") ||
                        cmd.Contains("stop_web_spy"))
                    {
                        ForwardToChrome(cmd);
                        // No reply needed; SpyOverlayWindow does not read back.
                        continue;
                    }

                    // ── Runtime commands that arrived on the wrong pipe ───────
                    // Activities (TypeIntoActivity, ClickActivity, GetTextActivity) should
                    // connect to GlassLinqBridge, but if they connect here instead we handle
                    // them correctly rather than silently dropping the message.
                    if (cmd.Contains("\"CLICK\"") ||
                        cmd.Contains("\"TYPE_INTO\"") ||
                        cmd.Contains("\"GET_TEXT\""))
                    {
                        LogMessage("Spy pipe: runtime command detected — routing through Chrome bridge.");

                        var replyReady = new ManualResetEventSlim(false);
                        lock (_pendingReplies)
                        {
                            _pendingReplyEvent = replyReady;
                            _pendingReplyJson = null;
                        }

                        ForwardToChrome(cmd);

                        string? replyJson = null;
                        if (replyReady.Wait(8000))
                        {
                            lock (_pendingReplies)
                            {
                                replyJson = _pendingReplyJson;
                            }
                        }

                        if (!string.IsNullOrEmpty(replyJson))
                        {
                            writer.WriteLine(replyJson);
                            server.WaitForPipeDrain();
                            LogMessage($"Spy pipe: runtime reply sent: {replyJson}");
                        }
                        else
                        {
                            string timeout = "{\"success\":false,\"reason\":\"Bridge timeout — no response from Chrome within 8s\"}";
                            writer.WriteLine(timeout);
                            LogMessage("Spy pipe: runtime reply timed out — sent failure response.");
                        }
                    }
                    else
                    {
                        LogMessage($"Spy pipe: unrecognized command ignored: {cmd.Substring(0, Math.Min(cmd.Length, 80))}");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"Spy pipe error: {ex.Message}");
                    Thread.Sleep(50);
                }
            }
        }

        /// <summary>
        /// Two-way pipe for runtime activity execution (CLICK, GET_TEXT, TYPE_INTO).
        /// ClickActivity/GetTextActivity connect here, send a JSON command, and block
        /// for the Chrome response on the same connection.
        /// </summary>
        private static void RunRuntimePipe()
        {
            LogMessage("Runtime pipe listening on GlassLinqBridge");

            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        "GlassLinqBridge",
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte);

                    server.WaitForConnection();
                    LogMessage("Studio activity connected to GlassLinqBridge");

                    using var reader = new StreamReader(server);
                    using var writer = new StreamWriter(server) { AutoFlush = true };

                    string? cmd = reader.ReadLine();
                    if (string.IsNullOrEmpty(cmd))
                    {
                        LogMessage("Runtime pipe: empty command received, skipping.");
                        continue;
                    }

                    LogMessage($"FROM STUDIO (runtime): {cmd}");

                    // Register reply slot, forward command to Chrome, then wait
                    var replyReady = new ManualResetEventSlim(false);

                    lock (_pendingReplies)
                    {
                        _pendingReplyEvent = replyReady;
                        _pendingReplyJson = null;
                    }

                    ForwardToChrome(cmd);

                    // Block up to 8 seconds for Chrome to respond
                    string? replyJson = null;
                    if (replyReady.Wait(8000))
                    {
                        lock (_pendingReplies)
                        {
                            replyJson = _pendingReplyJson;
                        }
                    }

                    if (!string.IsNullOrEmpty(replyJson))
                    {
                        writer.WriteLine(replyJson);
                        LogMessage($"TO STUDIO (runtime reply): {replyJson}");
                        server.WaitForPipeDrain(); // ← add this
                    }
                    else
                    {
                        string timeout = "{\"success\":false,\"reason\":\"Bridge timeout — no response from Chrome within 8s\"}";
                        writer.WriteLine(timeout);
                        LogMessage("Runtime reply timed out — sent failure response to Studio.");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"Runtime pipe error: {ex.Message}");
                    Thread.Sleep(50);
                }
            }
        }

        /// <summary>
        /// Send web element data TO Studio via Named Pipe (spy capture responses).
        /// </summary>
        private static void SendToStudio(string json)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    "GlassLinqResponse",
                    PipeDirection.Out);

                client.Connect(1000);

                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(json);

                LogMessage($"TO STUDIO: {json}");
            }
            catch (TimeoutException)
            {
                LogMessage("Studio response pipe timeout - Studio may not be listening");
            }
            catch (Exception ex)
            {
                LogMessage($"Error sending to Studio: {ex.Message}");
            }
        }

        /// <summary>
        /// Thread-safe logging.
        /// </summary>
        private static void LogMessage(string message)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, logEntry);
            }
            catch
            {
                // Silently fail — never crash the bridge over a log write
            }
        }
    }
}