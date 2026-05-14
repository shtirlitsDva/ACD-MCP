// Repro / regression artifact for issue #1 ("Live batch fails with
// eFilerError on first file when Test pass succeeded for all files").
//
// HOW TO RUN
//   In Civil 3D with ACD-MCP loaded, agents call this via the MCP tool
//   autocad_script_execute (or paste the body into the SCRIPT palette
//   and click Run). Requires the running plugin to expose
//   autocad_script_execute; nothing about the script is plugin-specific
//   otherwise — it talks straight to AutoCAD's managed Database API.
//
// WHAT IT CHECKS
//   Mimics BatchRunner's "open file once for Test phase → dispose →
//   open same file for Live phase → SaveAs" sequence directly against
//   AutoCAD's side-loaded Database. The pre-fix code used
//   FileShare.Read, which makes the Live SaveAs fail with eFilerError
//   because Database.Dispose() does NOT immediately release the OS
//   handle — the handle's share rules forbid concurrent writers. The
//   fix flips share mode to FileShare.ReadWrite at AcadDrawingHost so
//   the lingering handle no longer blocks our own SaveAs.
//
// EXPECTED RESULT (after the fix)
//   first_live_saved = True
//   loop_failures    = 0
//
// EXPECTED RESULT (before the fix)
//   first_live_saved   = False
//   first_live_save_error = "Exception: eFilerError"
//
// SAFETY
//   The script copies a real test drawing to %TEMP% before exercising
//   SaveAs, so the on-disk crashtest-v2-dwgs set is never modified. The
//   temp copies are deleted at the end.
using System.IO;

string srcRoot = @"H:\GitHub\shtirlitsDva\ACD-MCP\crashtest-v2-dwgs";
string firstSrc = System.IO.Path.Combine(srcRoot, "crashtest-01.dwg");
string firstTmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"efiler-repro-1-{System.Guid.NewGuid():N}.dwg");
File.Copy(firstSrc, firstTmp, overwrite: true);
File.SetAttributes(firstTmp, File.GetAttributes(firstTmp) & ~FileAttributes.ReadOnly);

// First-file Test→Live race. The share mode here MUST match
// AcadDrawingHost.cs. Failure on the next-to-last line is the issue's
// observed symptom.
string firstLiveSaveErr = null;
bool firstLiveSaved = false;
{
    using (var db = new Autodesk.AutoCAD.DatabaseServices.Database(false, true))
    {
        db.ReadDwgFile(firstTmp, FileShare.ReadWrite, allowCPConversion: false, password: "");
        using (var tx = db.TransactionManager.StartTransaction()) { }
    }
    Autodesk.AutoCAD.DatabaseServices.Database db2 = null;
    try
    {
        db2 = new Autodesk.AutoCAD.DatabaseServices.Database(false, true);
        db2.ReadDwgFile(firstTmp, FileShare.ReadWrite, allowCPConversion: false, password: "");
        using (var tx2 = db2.TransactionManager.StartTransaction()) { tx2.Commit(); }
        db2.SaveAs(firstTmp, db2.OriginalFileVersion);
        firstLiveSaved = true;
    }
    catch (System.Exception ex) { firstLiveSaveErr = ex.GetType().Name + ": " + ex.Message; }
    finally { try { db2?.Dispose(); } catch {} }
}

// Same race repeated over the remaining four crashtest dwgs to
// confirm stability across the runner's foreach loop.
int loopFailures = 0;
string loopFirstError = null;
for (int i = 2; i <= 5; i++)
{
    var src = System.IO.Path.Combine(srcRoot, $"crashtest-0{i}.dwg");
    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"efiler-repro-{i}-{System.Guid.NewGuid():N}.dwg");
    File.Copy(src, tmp, overwrite: true);
    File.SetAttributes(tmp, File.GetAttributes(tmp) & ~FileAttributes.ReadOnly);
    try
    {
        using (var db = new Autodesk.AutoCAD.DatabaseServices.Database(false, true))
        {
            db.ReadDwgFile(tmp, FileShare.ReadWrite, allowCPConversion: false, password: "");
            using (var tx = db.TransactionManager.StartTransaction()) { }
        }
        using (var db = new Autodesk.AutoCAD.DatabaseServices.Database(false, true))
        {
            db.ReadDwgFile(tmp, FileShare.ReadWrite, allowCPConversion: false, password: "");
            using (var tx = db.TransactionManager.StartTransaction()) { tx.Commit(); }
            db.SaveAs(tmp, db.OriginalFileVersion);
        }
    }
    catch (System.Exception ex) { loopFailures++; if (loopFirstError == null) loopFirstError = $"file {i}: {ex.GetType().Name}: {ex.Message}"; }
    finally { try { File.Delete(tmp); } catch {} }
}

try { File.Delete(firstTmp); } catch {}

return new {
    first_live_saved        = firstLiveSaved,
    first_live_save_error   = firstLiveSaveErr,
    loop_files_processed    = 4,
    loop_failures           = loopFailures,
    loop_first_error        = loopFirstError
};
