// Adapted from
// https://github.com/dotnet/aspnetcore/blob/da6c314d76628c1f130f76ed3e55f1d39057e091/src/Components/WebAssembly/WebAssembly/src/Services/WebAssemblyConsoleLogger.cs
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Further adapted in WasmSharp (Apache-2.0); this copy is under Carbide.Core.

using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Hosting;

/// <summary>
/// U1.2 — process-wide min-level gate shared across every generic <see cref="WebAssemblyConsoleLogger{T}"/>
/// instantiation. Lives on a non-generic type so one assignment affects all loggers
/// (C# generics create a separate static slot per instantiation, which is not what we want).
/// </summary>
internal static class WebAssemblyConsoleLoggerConfig
{
    private static volatile int _minLogLevel = (int)LogLevel.Warning;

    public static LogLevel MinLogLevel
    {
        get => (LogLevel)_minLogLevel;
        set => _minLogLevel = (int)value;
    }
}

internal sealed class WebAssemblyConsoleLogger<T>(string name) : ILogger<T>, ILogger
{
    private const string LoglevelPadding = ": ";
    private static readonly string MessagePadding = new(' ', GetLogLevelString(LogLevel.Information).Length + LoglevelPadding.Length);
    private static readonly string NewLineWithMessagePadding = Environment.NewLine + MessagePadding;
    private static readonly StringBuilder LogBuilder = new();

    private readonly string _name = name ?? throw new ArgumentNullException(nameof(name));

    public WebAssemblyConsoleLogger() : this(string.Empty)
    {
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => NoOpDisposable.Instance;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None) return false;
        return logLevel >= WebAssemblyConsoleLoggerConfig.MinLogLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);

        if (!string.IsNullOrEmpty(message) || exception != null)
        {
            WebAssemblyConsoleLogger<T>.WriteMessage(logLevel, _name, eventId.Id, message, exception);
        }
    }

    private static void WriteMessage(LogLevel logLevel, string logName, int eventId, string message, Exception? exception)
    {
        lock (LogBuilder)
        {
            try
            {
                CreateDefaultLogMessage(LogBuilder, logLevel, logName, eventId, message, exception);
                var formattedMessage = LogBuilder.ToString();

                switch (logLevel)
                {
                    case LogLevel.Trace:
                    case LogLevel.Debug:
                        ConsoleLoggerInterop.ConsoleDebug(formattedMessage);
                        break;
                    case LogLevel.Information:
                        ConsoleLoggerInterop.ConsoleInfo(formattedMessage);
                        break;
                    case LogLevel.Warning:
                        ConsoleLoggerInterop.ConsoleWarn(formattedMessage);
                        break;
                    case LogLevel.Error:
                    case LogLevel.Critical:
                        ConsoleLoggerInterop.ConsoleError(formattedMessage);
                        break;
                    case LogLevel.None:
                    default:
                        ConsoleLoggerInterop.ConsoleError("WriteMessage unexpectedly called with LogLevel.None.");
                        ConsoleLoggerInterop.ConsoleError(formattedMessage);
                        break;
                }
            }
            finally
            {
                LogBuilder.Clear();
            }
        }
    }

#pragma warning disable IDE0060 // Remove unused parameter
    private static void CreateDefaultLogMessage(StringBuilder logBuilder, LogLevel logLevel, string logName, int eventId, string message, Exception? exception)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        logBuilder.Append(GetLogLevelString(logLevel));
        logBuilder.Append(LoglevelPadding);
        logBuilder.Append(logName);

        if (!string.IsNullOrEmpty(message))
        {
            var len = logBuilder.Length;
            logBuilder.Append(message);
            logBuilder.Replace(Environment.NewLine, NewLineWithMessagePadding, len, message.Length);
        }

        if (exception != null)
        {
            logBuilder.AppendLine();
            logBuilder.Append(exception.ToString());
        }
    }

    private static string GetLogLevelString(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            LogLevel.None => "info",
            _ => throw new ArgumentOutOfRangeException(nameof(logLevel)),
        };
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance = new();

        public void Dispose() { }
    }
}

internal static partial class ConsoleLoggerInterop
{
    [JSImport("globalThis.console.debug")]
    public static partial void ConsoleDebug(string message);
    [JSImport("globalThis.console.info")]
    public static partial void ConsoleInfo(string message);
    [JSImport("globalThis.console.warn")]
    public static partial void ConsoleWarn(string message);
    [JSImport("globalThis.console.error")]
    public static partial void ConsoleError(string message);
    [JSImport("globalThis.console.log")]
    public static partial void ConsoleLog(string message);
}
