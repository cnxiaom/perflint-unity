using System.Runtime.CompilerServices;

// Allow the test assembly to access internal members (e.g. pure-logic unit tests for TextureImportScanner.IsUncompressedFormat).
[assembly: InternalsVisibleTo("PerfLint.Tests.Editor")]
