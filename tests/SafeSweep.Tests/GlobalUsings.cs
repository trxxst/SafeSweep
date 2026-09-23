// UseWPF removes System.IO from the implicit usings (Path clashes with
// System.Windows.Shapes.Path); these tests work with files everywhere.
global using System.IO;
