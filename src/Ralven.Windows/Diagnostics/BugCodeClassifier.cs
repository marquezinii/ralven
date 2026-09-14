using System.IO;
using System.Net.Http;
using Ralven.Contracts;

namespace Ralven.Windows.Diagnostics;

/// <summary>
/// Maps exceptions and failure contexts to stable <see cref="BugCode"/> values.
/// This allows automatic bug classification without requiring manual user selection
/// when the bug is captured programmatically (crash reports, telemetry, updater).
/// </summary>
public static class BugCodeClassifier
{
    /// <summary>
    /// Classifies an exception into a BugCode based on its type and context.
    /// </summary>
    public static BugCode ClassifyException(Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // Network/HTTP
            HttpRequestException httpEx => ClassifyHttpRequestException(httpEx, context),
            TaskCanceledException => BugCode.NET_API_REQUEST,

            // File/IO (specific first, then base)
            FileNotFoundException fnfEx => ClassifyFileNotFoundException(fnfEx, context),
            DirectoryNotFoundException => BugCode.SYS_FILESYSTEM,
            UnauthorizedAccessException uaEx => ClassifyUnauthorizedAccessException(uaEx, context),
            IOException ioEx => ClassifyIOException(ioEx, context),

            // JSON/Serialization
            System.Text.Json.JsonException => BugCode.SYS_JSON,

            // Crypto
            System.Security.Cryptography.CryptographicException => BugCode.SYS_CRYPTO,

            // Process
            System.ComponentModel.Win32Exception win32Ex => ClassifyWin32Exception(win32Ex, context),
            InvalidOperationException invOpEx when IsProcessRelated(invOpEx) => BugCode.SYS_PROCESS,

            // Memory
            OutOfMemoryException => BugCode.SYS_MEMORY,

            // Generic fallthrough: only assume "optimization" when nothing
            // more specific matched and the caller actually said so; an
            // unrecognized context must not silently look like an
            // optimization failure.
            _ => context switch
            {
                "optimization" => BugCode.APP_OPT_ACTION_EXECUTION,
                "fivem-action" => BugCode.APP_OPT_ACTION_EXECUTION,
                "gtav-action" => BugCode.APP_OPT_ACTION_EXECUTION,
                "windows-action" => BugCode.APP_OPT_ACTION_EXECUTION,
                "app-inventory" => BugCode.APP_INV_SCAN,
                "security-health" => BugCode.SEC_HEALTH_QUERY,
                "settings" => BugCode.APP_SETTINGS_PERSISTENCE,
                _ => BugCode.Unknown
            }
        };
    }

    /// <summary>
    /// Classifies an exception specifically from the optimization engine.
    /// </summary>
    public static BugCode ClassifyOptimizationException(Exception exception, string? actionId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // If we have an action ID, we can be more specific
        if (!string.IsNullOrWhiteSpace(actionId))
        {
            if (actionId.StartsWith("fivem.", StringComparison.OrdinalIgnoreCase))
                return ClassifyFiveMActionException(exception, actionId);

            if (actionId.StartsWith("gtav.", StringComparison.OrdinalIgnoreCase))
                return ClassifyGtaVActionException(exception, actionId);

            if (actionId.StartsWith("windows.", StringComparison.OrdinalIgnoreCase))
                return ClassifyWindowsActionException(exception, actionId);
        }

        return ClassifyException(exception, "optimization");
    }

    /// <summary>
    /// Classifies an exception from the updater/launcher.
    /// </summary>
    public static BugCode ClassifyUpdaterException(Exception exception, string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            System.Security.Cryptography.CryptographicException => BugCode.UPD_INSTALLER_VERIFICATION,
            System.IO.InvalidDataException => BugCode.UPD_INSTALLER_INTEGRITY,
            UnauthorizedAccessException => BugCode.UPD_INSTALLER_EXECUTION,
            FileNotFoundException => BugCode.UPD_INSTALLER_EXECUTION,
            System.IO.IOException => BugCode.UPD_STAGING,
            HttpRequestException => BugCode.UPD_INSTALLER_DOWNLOAD,
            TaskCanceledException => BugCode.NET_UPDATE_NETWORK,
            TimeoutException => BugCode.UPD_PARENT_TIMEOUT,
            InvalidOperationException invOpEx
                when invOpEx.Message.Contains("signature", StringComparison.OrdinalIgnoreCase)
                    || invOpEx.Message.Contains("hash", StringComparison.OrdinalIgnoreCase)
                    || invOpEx.Message.Contains("security", StringComparison.OrdinalIgnoreCase)
                => BugCode.UPD_SECURITY_POLICY,
            _ => stage switch
            {
                "manifest-download" => BugCode.UPD_MANIFEST_DOWNLOAD,
                "manifest-verify" => BugCode.UPD_MANIFEST_VERIFICATION,
                "manifest-parse" => BugCode.UPD_MANIFEST_PARSING,
                "installer-download" => BugCode.UPD_INSTALLER_DOWNLOAD,
                "installer-verify" => BugCode.UPD_INSTALLER_VERIFICATION,
                "staging" => BugCode.UPD_STAGING,
                "activation" => BugCode.UPD_ACTIVATION,
                "installer-run" => BugCode.UPD_INSTALLER_EXECUTION,
                "health-check" => BugCode.UPD_HEALTH_CHECK,
                "rollback" => BugCode.UPD_ROLLBACK,
                "handoff" => BugCode.UPD_HANDOFF_INVALID,
                _ => BugCode.UPD_INSTALLER_EXECUTION
            }
        };
    }

    /// <summary>
    /// Classifies an exception from the broker (elevated operations).
    /// </summary>
    public static BugCode ClassifyBrokerException(Exception exception, string? actionId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            BrokerIntegrityException => BugCode.BRK_INTEGRITY_VALIDATION,
            FileNotFoundException => BugCode.BRK_LAUNCH,
            System.ComponentModel.Win32Exception win32Ex when win32Ex.NativeErrorCode == 1223 => BugCode.BRK_UAC_CANCELLED, // ERROR_CANCELLED
            System.ComponentModel.Win32Exception => BugCode.BRK_IPC_COMMUNICATION,
            System.IO.IOException => BugCode.BRK_IPC_COMMUNICATION,
            InvalidOperationException invOpEx
                when invOpEx.Message.Contains("transaction", StringComparison.OrdinalIgnoreCase)
                => BugCode.BRK_TRANSACTION_INCOMPLETE,
            _ => BugCode.BRK_ACTION_EXECUTION
        };
    }

    public static BugCode ClassifyBrokerFailure(string? errorCode, bool wasCancelled)
    {
        if (wasCancelled)
        {
            return BugCode.BRK_UAC_CANCELLED;
        }

        return errorCode switch
        {
            "transaction-not-committed" => BugCode.BRK_TRANSACTION_INCOMPLETE,
            "rollback-not-completed" => BugCode.BRK_ROLLBACK_INCOMPLETE,
            "broker-not-elevated" => BugCode.BRK_UAC_DENIED,
            "broker-pipe-connection-failed" => BugCode.BRK_IPC_COMMUNICATION,
            "broker-operation-failed" or "broker-operation-timeout" => BugCode.BRK_ACTION_EXECUTION,
            null or "" => BugCode.BRK_PROCESS_CRASH,
            _ => BugCode.BRK_REQUEST_VALIDATION
        };
    }

    private static BugCode ClassifyHttpRequestException(HttpRequestException ex, string? context)
    {
        if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return BugCode.NET_AUTH_NETWORK;

        if (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            return BugCode.NET_API_REQUEST;

        if (ex.StatusCode == (System.Net.HttpStatusCode)429)
            return BugCode.NET_RATE_LIMITED;

        if (ex.InnerException is System.Net.WebException webEx)
        {
            return webEx.Status switch
            {
                System.Net.WebExceptionStatus.NameResolutionFailure => BugCode.NET_DNS,
                System.Net.WebExceptionStatus.SecureChannelFailure => BugCode.NET_TLS,
                System.Net.WebExceptionStatus.TrustFailure => BugCode.NET_TLS,
                System.Net.WebExceptionStatus.ConnectFailure => BugCode.NET_API_REQUEST,
                System.Net.WebExceptionStatus.Timeout => BugCode.NET_API_REQUEST,
                _ => BugCode.NET_API_REQUEST
            };
        }

        return context switch
        {
            "telemetry" => BugCode.NET_TELEMETRY_DELIVERY,
            "bug-report" => BugCode.NET_BUG_REPORT_DELIVERY,
            "update-check" => BugCode.UPD_MANIFEST_DOWNLOAD,
            "update-download" => BugCode.UPD_INSTALLER_DOWNLOAD,
            _ => BugCode.NET_API_REQUEST
        };
    }

    private static BugCode ClassifyIOException(IOException ex, string? context)
    {
        return ex.HResult switch
        {
            -2147024891 => BugCode.WIN_REGISTRY,      // UnauthorizedAccessException HRESULT (but caught as IOException)
            -2147024784 => BugCode.SYS_FILESYSTEM,     // File not found
            -2147024786 => BugCode.SYS_FILESYSTEM,     // Path not found
            -2147024864 => BugCode.SYS_FILESYSTEM,     // Disk full
            _ => context switch
            {
                "updater" => BugCode.UPD_STAGING,
                "settings" => BugCode.APP_SETTINGS_PERSISTENCE,
                "journal" => BugCode.APP_OPT_TRANSACTION_COMMIT,
                _ => BugCode.SYS_FILESYSTEM
            }
        };
    }

    private static BugCode ClassifyUnauthorizedAccessException(UnauthorizedAccessException ex, string? context)
    {
        return context switch
        {
            "updater" => BugCode.UPD_INSTALLER_EXECUTION,
            "broker" => BugCode.BRK_UAC_DENIED,
            "registry" => BugCode.WIN_REGISTRY,
            "service" => BugCode.WIN_SERVICE,
            "power" => BugCode.WIN_POWER_PLAN,
            "app-inventory" => BugCode.APP_INV_SCAN,
            "settings" => BugCode.APP_SETTINGS_PERSISTENCE,
            _ => BugCode.WIN_PRIVILEGE
        };
    }

    private static BugCode ClassifyFileNotFoundException(FileNotFoundException ex, string? context)
    {
        return context switch
        {
            "updater" => BugCode.UPD_INSTALLER_EXECUTION,
            "gtav-settings" => BugCode.GTAV_SETTINGS_IO,
            "fivem-cache" => BugCode.FIVEM_CACHE_OPERATION,
            "settings" => BugCode.APP_SETTINGS_PERSISTENCE,
            _ => BugCode.SYS_FILESYSTEM
        };
    }

    private static BugCode ClassifyWin32Exception(System.ComponentModel.Win32Exception ex, string? context)
    {
        return ex.NativeErrorCode switch
        {
            5 => BugCode.WIN_PRIVILEGE,           // ERROR_ACCESS_DENIED
            1223 => BugCode.BRK_UAC_CANCELLED,    // ERROR_CANCELLED (UAC cancelled)
            740 => BugCode.BRK_UAC_DENIED,        // ERROR_ELEVATION_REQUIRED
            1058 => BugCode.WIN_SERVICE,          // ERROR_SERVICE_DISABLED
            1060 => BugCode.WIN_SERVICE,          // ERROR_SERVICE_DOES_NOT_EXIST
            _ => context switch
            {
                "process" => BugCode.SYS_PROCESS,
                "broker" => BugCode.BRK_IPC_COMMUNICATION,
                _ => BugCode.WIN_PRIVILEGE
            }
        };
    }

    private static bool IsProcessRelated(InvalidOperationException ex) =>
        ex.Message.Contains("process", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("HasExited", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Start", StringComparison.OrdinalIgnoreCase);

    private static BugCode ClassifyFiveMActionException(Exception ex, string actionId)
    {
        if (actionId.Contains("cache", StringComparison.OrdinalIgnoreCase)) return BugCode.FIVEM_CACHE_OPERATION;
        if (actionId.Contains("log", StringComparison.OrdinalIgnoreCase)) return BugCode.FIVEM_LOG_PARSING;
        if (actionId.Contains("crash", StringComparison.OrdinalIgnoreCase)) return BugCode.FIVEM_CRASH_ANALYSIS;
        if (actionId.Contains("installation", StringComparison.OrdinalIgnoreCase)) return BugCode.FIVEM_INSTALLATION_HEALTH;
        if (actionId.Contains("storage", StringComparison.OrdinalIgnoreCase)) return BugCode.FIVEM_STORAGE_HEALTH;
        if (actionId.Contains("process", StringComparison.OrdinalIgnoreCase)) return BugCode.FIVEM_PROCESS_DETECTION;
        return ClassifyException(ex, "fivem-action");
    }

    private static BugCode ClassifyGtaVActionException(Exception ex, string actionId)
    {
        if (actionId.Contains("settings", StringComparison.OrdinalIgnoreCase)) return BugCode.GTAV_SETTINGS_IO;
        if (actionId.Contains("graphics", StringComparison.OrdinalIgnoreCase)) return BugCode.GTAV_GRAPHICS_PRESET;
        if (actionId.Contains("launch", StringComparison.OrdinalIgnoreCase)) return BugCode.GTAV_LAUNCH_PARAMS;
        if (actionId.Contains("executable", StringComparison.OrdinalIgnoreCase)) return BugCode.GTAV_EXECUTABLE_DETECTION;
        if (actionId.Contains("benchmark", StringComparison.OrdinalIgnoreCase)) return BugCode.GTAV_BENCHMARK;
        return ClassifyException(ex, "gtav-action");
    }

    private static BugCode ClassifyWindowsActionException(Exception ex, string actionId)
    {
        if (actionId.Contains("power", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_POWER_PLAN;
        if (actionId.Contains("gaming", StringComparison.OrdinalIgnoreCase) || actionId.Contains("game-mode", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_GAMING_MODE;
        if (actionId.Contains("driver", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_DRIVER_QUERY;
        if (actionId.Contains("display", StringComparison.OrdinalIgnoreCase)
            || actionId.Contains("hags", StringComparison.OrdinalIgnoreCase)
            || actionId.Contains("appearance", StringComparison.OrdinalIgnoreCase)
            || actionId.Contains("visual-effects", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_DISPLAY_CONFIG;
        if (actionId.Contains("pcie", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_PCIE_QUERY;
        if (actionId.Contains("thermal", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_THERMAL;
        if (actionId.Contains("pagefile", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_PAGEFILE;
        if (actionId.Contains("whea", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_WHEA;
        if (actionId.Contains("bios", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_BIOS;
        if (actionId.Contains("service", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_SERVICE;
        if (actionId.Contains("registry", StringComparison.OrdinalIgnoreCase)) return BugCode.WIN_REGISTRY;
        return ClassifyException(ex, "windows-action");
    }
}
