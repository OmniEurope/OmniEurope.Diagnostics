// SPDX-License-Identifier: EUPL-1.2
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Marks a host registered by <see cref="PerformanceServiceCollectionExtensions.AddOmniPerformance"/>:
/// the page's services are there, so <c>MapOmniPerformance</c> may map it.</summary>
internal sealed class PerformancePageServices;
