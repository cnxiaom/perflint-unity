# Third-Party Notices

PerfLint for Unity bundles the following third-party binaries, used **only** by the optional
script-analysis module (Roslyn). They live dormant under `Editor/Scripting/RoslynDlls~/` and are
copied into your project only when you explicitly enable script analysis. All are licensed under the
**MIT License** by the **.NET Foundation and Contributors** (Microsoft).

| Component | File | License |
|---|---|---|
| Microsoft.CodeAnalysis (Roslyn) | `Microsoft.CodeAnalysis.dll` | MIT |
| Microsoft.CodeAnalysis.CSharp (Roslyn) | `Microsoft.CodeAnalysis.CSharp.dll` | MIT |
| System.Collections.Immutable | `System.Collections.Immutable.dll` | MIT |
| System.Reflection.Metadata | `System.Reflection.Metadata.dll` | MIT |
| System.Runtime.CompilerServices.Unsafe | `System.Runtime.CompilerServices.Unsafe.dll` | MIT |
| System.Text.Encoding.CodePages | `System.Text.Encoding.CodePages.dll` | MIT |

Sources: <https://github.com/dotnet/roslyn> and <https://github.com/dotnet/runtime>.

---

## MIT License

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
