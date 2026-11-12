// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Sample;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "OmniEurope.Diagnostics sample" };
}
