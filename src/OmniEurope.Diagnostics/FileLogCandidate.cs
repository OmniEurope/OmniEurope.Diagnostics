// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;

namespace OmniEurope.Diagnostics;

/// <summary>
/// An entry the file logger is about to write, as <see cref="FileLoggingOptions.Filter"/> sees it: the filter decides
/// whether it is written.
/// </summary>
/// <param name="Category">The logger's category (<c>SourceContext</c> in the file).</param>
/// <param name="Level">The entry's level.</param>
/// <param name="EventId">The entry's event id.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="Exception">The exception logged with it, or null.</param>
public sealed record FileLogCandidate(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);
