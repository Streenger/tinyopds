/*
 * This file is part of TinyOPDS server project
 * https://github.com/sensboston/tinyopds
 *
 * Copyright (c) 2013-2025 SeNSSoFT
 * SPDX-License-Identifier: MIT
 *
 * Simple logging class
 *
 */

using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace TinyOPDS
{
    /// <summary>
    /// A lightweight logging class
    /// </summary>
    public static class Log
    {
        // Store only the filename, full path will be calculated dynamically
        private static string logFileName = "TinyOPDS.log";

        /// <summary>
        /// Get full path to log file (calculated each time to ensure correct location)
        /// </summary>
        public static string LogFileName
        {
            get { return GetLogPath(); }
        }

        /// <summary>
        /// 
        /// </summary>
        public static LogLevel VerbosityLevel = LogLevel.Info;

        /// <summary>
        /// Flag indicating if running as system service
        /// Should be set by service initialization code
        /// </summary>
        public static bool IsRunningAsService = false;

        /// <summary>
        /// Enable or disable logging to file
        /// </summary>
        private static bool saveToFile = false;
        public static bool SaveToFile
        {
            get { return saveToFile; }
            set
            {
                bool wasOn = saveToFile;
                saveToFile = value;
                if (wasOn && !value) Flush();

                // Ensure directory exists when enabling logging
                if (saveToFile)
                {
                    try
                    {
                        string logPath = GetLogPath();
                        string directory = Path.GetDirectoryName(logPath);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to create log directory: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Determine log file path based on platform
        /// </summary>
        /// <returns>Full path to log file</returns>
        private static string GetLogPath()
        {
            // Direct platform check without Utils to avoid circular dependency
            PlatformID platform = Environment.OSVersion.Platform;
            bool isWindows = platform == PlatformID.Win32NT ||
                            platform == PlatformID.Win32S ||
                            platform == PlatformID.Win32Windows ||
                            platform == PlatformID.WinCE;

            // On Unix-like systems (everything that's NOT Windows), try system log directory
            if (!isWindows)
            {
                string systemLogDir = "/var/log/tinyopds";
                try
                {
                    if (!Directory.Exists(systemLogDir))
                    {
                        Directory.CreateDirectory(systemLogDir);
                    }

                    // Test write access
                    string testFile = Path.Combine(systemLogDir, ".test");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);

                    return Path.Combine(systemLogDir, logFileName);
                }
                catch
                {
                    // Fall back to application directory if cannot write to /var/log/tinyopds/
                    Debug.WriteLine("Cannot write to /var/log/tinyopds/, falling back to application directory");
                }
            }

            // For Windows (including Microsoft Store) and Unix fallback:
            // Use Utils.ServiceFilesLocation which handles:
            // - Regular Windows: exe directory
            // - Microsoft Store: LocalApplicationData/TinyOPDS
            // - Unix fallback: exe directory or LocalApplicationData
            return Path.Combine(Utils.ServiceFilesLocation, logFileName);
        }

        /// <summary>
        /// Writes the args to the default logging output using the format provided.
        /// </summary>
        public static void WriteLine(string format, params object[] args)
        {
            WriteLine(LogLevel.Info, format, args);
        }

        /// <summary>
        /// Writes the args to the default logging output using the format provided.
        /// </summary>
        public static void WriteLine(LogLevel level, string format, params object[] args)
        {
            if (level >= VerbosityLevel || level == LogLevel.Authentication)
            {
                string caller = "---";

                if (level != LogLevel.Authentication)
                {
                    try
                    {
                        caller = new StackTrace().GetFrame(2).GetMethod().ReflectedType.Name;
                        if (caller.StartsWith("<>")) caller = new StackTrace().GetFrame(1).GetMethod().ReflectedType.Name;
                    }
                    catch { }
                }
                else caller = "HTTPServer";
                string prefix = string.Format("{0}\t{1}\t{2}", (level == LogLevel.Info) ? 'I' : ((level == LogLevel.Warning) ? 'W' : ((level == LogLevel.Authentication) ? 'A' : 'E')), caller, (caller.Length > 7 ? "" : "\t"));

                string message = string.Format(prefix + format, args);
                Debug.WriteLine(message);
                if (SaveToFile) WriteToFile(message, level == LogLevel.Error);
            }
        }

        private static readonly object fileSyncObject = new object();

        /// <summary>
        /// Как часто (в секундах) накопленные в памяти строки сбрасываются в файл.
        /// 0 - писать каждую строку сразу (прежнее поведение).
        /// </summary>
        public static int FlushIntervalSeconds = 30;

        // Сбрасывать раньше срока, если в памяти накопилось столько строк
        private const int MaxPendingLines = 5000;

        private static readonly StringBuilder pendingText = new StringBuilder();
        private static readonly object pendingSync = new object();
        private static int pendingLines = 0;
        private static Timer flushTimer;
        private static bool exitHooked = false;

        /// <summary>
        /// Ставит строку в очередь на запись. Сообщения уровня Error сбрасываются сразу,
        /// остальные - по таймеру, при переполнении очереди и при выходе из программы.
        /// </summary>
        private static void WriteToFile(string message, bool flushNow)
        {
            string line = string.Format("{0:MM/dd/yyyy HH:mm:ss.f}\t{1}", DateTime.Now, message);
            bool needFlush;

            lock (pendingSync)
            {
                pendingText.AppendLine(line);
                pendingLines++;

                if (!exitHooked)
                {
                    exitHooked = true;
                    try
                    {
                        AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush();
                        AppDomain.CurrentDomain.UnhandledException += (s, e) => Flush();
                    }
                    catch { }
                }

                int interval = FlushIntervalSeconds;
                if (interval > 0 && flushTimer == null)
                    flushTimer = new Timer(_ => Flush(), null, interval * 1000, interval * 1000);

                needFlush = flushNow || interval <= 0 || pendingLines >= MaxPendingLines;
            }

            if (needFlush) Flush();
        }

        /// <summary>
        /// Немедленно записывает накопленные строки в файл лога.
        /// </summary>
        public static void Flush()
        {
            string text;
            lock (pendingSync)
            {
                if (pendingText.Length == 0) return;
                text = pendingText.ToString();
                pendingText.Clear();
                pendingLines = 0;
            }

            lock (fileSyncObject)
            {
                try
                {
                    // Always get fresh path to ensure correct location
                    string fullLogPath = GetLogPath();

                    // Ensure directory exists (in case Utils.ServiceFilesLocation changed)
                    string directory = Path.GetDirectoryName(fullLogPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    using (var fileStream = new FileStream(fullLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(fileStream))
                    {
                        writer.Write(text);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to write to log file: {ex.Message}");

                    // Не теряем строки: возвращаем в начало очереди (с ограничением по размеру)
                    lock (pendingSync)
                    {
                        if (pendingText.Length < 4 * 1024 * 1024)
                            pendingText.Insert(0, text);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The type of error to log
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// A message containing information only.
        /// </summary>
        Info = 0,
        /// <summary>
        /// A non-critical warning error message.
        /// </summary>
        Warning = 1,
        /// <summary>
        /// A fatal error message.
        /// </summary>
        Error = 2,
        /// <summary>
        /// Authentication message, MUST be logged.
        /// </summary>
        Authentication = 3,
    }
}