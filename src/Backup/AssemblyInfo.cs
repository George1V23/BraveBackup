using System.Runtime.CompilerServices;

// Allow detector tests to supply controlled filesystem and registry inputs.
[assembly: InternalsVisibleTo("Brave.Tests")]
[assembly: InternalsVisibleTo("Linux.Tests")]
