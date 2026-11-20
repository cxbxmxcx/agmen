# Third-party notices

Agmen's own code is under the MIT license in [LICENSE](LICENSE). A released build also contains the software below, each under its own license.

| Component | What Agmen uses it for | License | Source |
|---|---|---|---|
| EasyWindowsTerminalControl 1.0.38, © MitchCapper | The WPF wrapper around the terminal control and the pseudo-console | MIT | https://github.com/MitchCapper/EasyWindowsTerminalControl |
| Windows Terminal control (`Microsoft.Terminal.Control.dll`, `Microsoft.Terminal.Wpf.dll`, from the CI.Microsoft.Terminal.Wpf 1.25 package), © Microsoft Corporation | Drawing each session's terminal | MIT | https://github.com/microsoft/terminal |
| Microsoft.Windows.Console.ConPTY 1.24 (`conpty.dll`, `x64\OpenConsole.exe`), © Microsoft Corporation | The pseudo-console each agent CLI runs on | MIT | https://github.com/microsoft/terminal |
| .NET 10 runtime, WPF and Windows Forms, © .NET Foundation and contributors | The runtime inside `Agmen.exe` and the native files beside it (`*_cor3.dll`) | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf, https://github.com/dotnet/winforms |

The .NET runtime carries notices for the components it includes in turn: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

## MIT License

The MIT license, which all of the components above use, reads:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Agmen is not affiliated with Anthropic, OpenAI, Moonshot AI or Google. Claude Code, Codex, Kimi Code and Antigravity are their products; Agmen runs the copies you install and sign in to yourself.
