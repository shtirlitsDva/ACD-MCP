using System;
using Acd.Mcp.Serialization;

namespace Acd.Mcp.Api
{
    // The `Acd` global a REPL submission sees. Mirrors the same identifier
    // the DTO .csx files use, but limited to read-only surface (no
    // RegisterDto) because the REPL is not a DTO authoring context.
    //
    // The skill documents `Acd.DataProvider.ReadAll(entity)` as the
    // canonical pattern for reading entity metadata (block attributes /
    // PropertySets / XData) without picking a mechanism. Before this
    // type existed, that pattern silently failed in the REPL because
    // `Acd` was only available inside DTO bodies — F9 in the crash-test
    // journal.
    //
    // Lives in Acd.Mcp.Api alongside AcadGlobals because both are
    // referenced by the IL the REPL submissions emit, and that IL must
    // bind through the default ALC.
    public sealed class AcdReplApi
    {
        public AcdReplApi(DtoDataProviderApi dataProvider)
        {
            DataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        }

        public DtoDataProviderApi DataProvider { get; }
    }
}
