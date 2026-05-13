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

        static void Main(string[] args)
        {
            // Ensure log directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));

            try
            {
                _stdout = Console.OpenStandardOutput();
                LogMessage("Bridge Started");

                // Start Studio listener in background
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

                            // Forward web element data to Studio
                            if (message.Contains("element_hovered"))
                            {
                                SendToStudio(message);
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
        /// Send message TO Chrome Extension
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
        /// Listen for commands FROM Studio via Named Pipe
        /// </summary>
        private static void StartStudioListener()
        {
            LogMessage("Studio listener started - Listening for Web Spy commands");

            while (true)
            {
                try
                {
                    // Using a simpler NamedPipeServerStream setup for stability
                    using (var server = new NamedPipeServerStream(
                        "GlassLinqPipe",
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances))
                    {
                        server.WaitForConnection();

                        using (var reader = new StreamReader(server))
                        {
                            string? studioCommand = reader.ReadLine();
                            if (!string.IsNullOrEmpty(studioCommand))
                            {
                                LogMessage($"FROM STUDIO: {studioCommand}");

                                // Directly forward the JSON string to Chrome
                                // We check for the actions we added in C# (start_web_spy, stop_web_spy, web_spy_request)
                                if (studioCommand.Contains("web_spy_request") ||
                                    studioCommand.Contains("start_web_spy") ||
                                    studioCommand.Contains("stop_web_spy"))
                                {
                                    ForwardToChrome(studioCommand);
                                }
                                else
                                {
                                    LogMessage("Studio command received but not recognized as a Web Spy action.");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"Studio listener error: {ex.Message}");
                    // Brief sleep to prevent CPU hammering if the pipe fails repeatedly
                    Thread.Sleep(50);
                }
            }
        }

        /// <summary>
        /// Forwards a raw JSON string from Studio directly to Chrome via Standard Output.
        /// </summary>
        private static void ForwardToChrome(string jsonMessage)
        {
            try
            {
                // Chrome expects: [4-byte length prefix] + [JSON string]
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
        /// Send web element data TO Studio via Named Pipe
        /// </summary>
        private static void SendToStudio(string json)
        {
            try
            {
                using (var client = new NamedPipeClientStream(
                    ".",
                    "GlassLinqResponse",
                    PipeDirection.Out))
                {
                    // Wait up to 1 second for Studio to be ready
                    client.Connect(1000);

                    using (var writer = new StreamWriter(client) { AutoFlush = true })
                    {
                        writer.WriteLine(json);
                    }

                    LogMessage($"TO STUDIO: {json}");
                }
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
        /// Thread-safe logging
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
                // Silently fail if logging fails - don't crash the bridge
            }
        }
    }
}