// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>
/// Hands an exported or downloaded log file to the host, which saves or offers it as its platform allows (a save
/// dialog in a desktop application, a browser download in a web one). The package registers no implementation:
/// the logs view hides export and download when none is registered.
/// </summary>
public interface ILogFileExport
{
    /// <summary>Saves or offers <paramref name="content"/> under <paramref name="fileName"/>.</summary>
    /// <param name="fileName">The suggested file name, without a folder (<c>app-20261112.json</c>, <c>logs.csv</c>).</param>
    /// <param name="contentType">The media type of the content (<c>text/csv</c>, <c>application/json</c>).</param>
    /// <param name="content">The file's content, read from its current position; the caller disposes it.</param>
    /// <param name="cancellationToken">Ends the export.</param>
    Task ExportAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken);
}
