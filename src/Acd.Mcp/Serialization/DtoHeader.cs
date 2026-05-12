using System.Text.RegularExpressions;

namespace Acd.Mcp.Serialization
{
    // Each DTO file declares the AutoCAD type it targets on its very first
    // line:
    //
    //   // @dto: Autodesk.AutoCAD.DatabaseServices.Circle
    //
    // The body is plain C# script that calls Acd.RegisterDto<T>(...). The
    // header exists so a) the loader can log a useful "this file is for
    // Circle" context if compilation fails, and b) we can short-circuit
    // duplicates / surface filename ↔ type mismatches as warnings.
    //
    // The body's RegisterDto<T> call is the source of truth — the header is
    // documentation, not validation.
    public static class DtoHeader
    {
        private static readonly Regex Pattern = new(
            @"^\s*//\s*@dto\s*:\s*(?<type>[A-Za-z_][\w\.\+]*)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        public static string? TryParse(string source)
        {
            var m = Pattern.Match(source);
            return m.Success ? m.Groups["type"].Value : null;
        }
    }
}
