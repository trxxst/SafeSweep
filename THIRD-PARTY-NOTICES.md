# Third-party notices

SafeSweep is distributed under the MIT License (see [LICENSE](LICENSE)). The released
application includes the following third-party components.

| Component | Version | License | Project |
| --- | --- | --- | --- |
| WPF-UI (`WPF-UI`, `WPF-UI.Abstractions`) | 4.3.0 | MIT | https://github.com/lepoco/wpfui |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| .NET runtime and WPF (included in self-contained builds) | 9.0 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |

The setup program (`SafeSweep-Setup-x64.exe`) and the MSI are built with the
[WiX Toolset](https://github.com/wixtoolset/wix) 5.0.2 (Microsoft Reciprocal License,
MS-RL). The setup program contains the WiX bootstrapper engine and its standard user
interface, whose source code is available from that project.

Development-only dependencies (not included in the released application):

| Component | License |
| --- | --- |
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| Xunit.SkippableFact | MS-PL |
| WiX Toolset SDK and extensions (build tooling) | MS-RL |

The .NET runtime's own third-party notices are published at
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT.
