// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Hosting;

namespace OmniEurope.Diagnostics.Maui;

/// <summary>
/// Runs once when the MAUI application's services are built: checks the options registered with
/// <c>ValidateOnStart</c> (what a server host does when it starts) and starts the performance recorder, which a
/// MAUI host would otherwise never start since it runs no hosted service.
/// </summary>
internal sealed class OmniDiagnosticsInitializer : IMauiInitializeService
{
    /// <inheritdoc />
    public void Initialize(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Sync over async is safe here: the options validators complete synchronously.
        services.GetService<IAsyncStartupValidator>()?.ValidateAsync(CancellationToken.None).GetAwaiter().GetResult();
        services.GetRequiredService<RequestPerformanceRecorder>().Start();
    }
}
