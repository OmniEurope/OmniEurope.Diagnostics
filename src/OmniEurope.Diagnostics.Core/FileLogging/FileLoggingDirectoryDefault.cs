// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OmniEurope.Diagnostics.FileLogging;

/// <summary>
/// Gives <see cref="FileLoggingOptions.Directory"/> a default when the host names none: <c>logs</c> under the content
/// root in the Development environment, <c>{CommonApplicationData}/{ApplicationName}/logs</c> everywhere else, so a
/// deployed application never writes its logs inside its own (often read-only or replaced) program folder. Without a
/// host environment, or without an application name, no default applies and the options validation asks for a folder.
/// </summary>
internal sealed class FileLoggingDirectoryDefault(IHostEnvironment? environment = null) : IPostConfigureOptions<FileLoggingOptions>
{
    /// <summary>The folder name used under the content root in Development, and under the application's folder elsewhere.</summary>
    internal const string FolderName = "logs";

    /// <inheritdoc />
    public void PostConfigure(string? name, FileLoggingOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Directory) && DefaultFor(environment) is { } directory)
            options.Directory = directory;
    }

    /// <summary>The default folder for <paramref name="host"/>, or null when it gives nothing to derive one from.</summary>
    internal static string? DefaultFor(IHostEnvironment? host, string? commonApplicationData = null)
    {
        if (host is null)
            return null;
        string? environmentName, applicationName;
        try
        {
            environmentName = host.EnvironmentName;
            applicationName = host.ApplicationName;
        }
        catch (NotImplementedException)
        {
            // The environment a .NET MAUI application registers implements none of its members.
            return null;
        }
        if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
            return FolderName;
        var application = Sanitize(applicationName);
        var root = commonApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return application.Length == 0 || root.Length == 0 ? null : Path.Combine(root, application, FolderName);
    }

    private static string Sanitize(string? applicationName) =>
        string.Concat((applicationName ?? "").Trim().Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)).Trim('.', ' ');
}
