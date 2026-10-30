using System.IO;
using Xehn.App;

if (args.Length != 2) throw new ArgumentException("Usage: Xehn.IconBuilder <original.ico> <rounded.ico>");
var source = Path.GetFullPath(args[0]); var target = Path.GetFullPath(args[1]);
if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Preserve the original ICO; use a separate output path.");
using var input = File.OpenRead(source);
File.WriteAllBytes(target, IconArtwork.RoundIco(input));
Console.WriteLine("Rounded branding ICO: " + target);
