// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Localization;

// The texts live in Resources/ whatever ResourcesPath the host gives AddLocalization.
[assembly: ResourceLocation("Resources")]
// The assembly is OmniEurope.Diagnostics.Core but its root namespace, and so its resource names, are
// OmniEurope.Diagnostics: the localizer composes the resource name from this attribute, not the assembly name.
[assembly: RootNamespace("OmniEurope.Diagnostics")]
// The ASP.NET Core package renders its pages with the internal readers, formats and texts of this assembly.
[assembly: InternalsVisibleTo("OmniEurope.Diagnostics")]
[assembly: InternalsVisibleTo("OmniEurope.Diagnostics.Tests")]
