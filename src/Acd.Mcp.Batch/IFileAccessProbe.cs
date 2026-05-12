using System;
using System.IO;

namespace Acd.Mcp.Batch
{
    // Restrictive file-access probe. Both Test and Live mode demand that no
    // other process has the drawing open for write while we process it.
    //
    // Contract: OpenLease MUST throw if the file is locked by another writer.
    // The runner does NOT silently skip a locked file — a half-finished batch
    // is worse than a slower-but-safer one. The user must intervene before
    // the batch continues.
    //
    // Why a probe-and-release, not a held handle for the whole file lifetime:
    //   AutoCAD's ReadDwgFile + SaveAs internally open the underlying file
    //   for read + write. If we held a FileStream with FileShare.Read
    //   throughout, Windows would refuse AutoCAD's SaveAs write within the
    //   same process. The probe verifies exclusivity at the moment of open
    //   and immediately releases the OS handle; the very short window
    //   between probe-release and AutoCAD's read is the same window the
    //   reference loop relies on (it doesn't probe at all). We accept the
    //   TOCTOU sliver — the user does too: the alternative would be to
    //   never lock-check, which the spec explicitly rejects.
    public interface IFileAccessProbe
    {
        FileLease OpenLease(string path);
    }

    // Marker disposable representing a successful probe. The underlying OS
    // handle is closed during construction (the probe is the open-and-close
    // act itself). Dispose is a no-op kept for symmetry with the call-site
    // pattern (using-statements at the runner).
    public sealed class FileLease : IDisposable
    {
        public string Path { get; }
        public FileLease(string path) { Path = path; }
        public void Dispose() { }
    }

    // Default implementation. Lives in this assembly because it's
    // AutoCAD-free — anyone testing the runner can hand a real probe.
    // Tests that need to simulate a locked file inject a fake probe that
    // throws on OpenLease.
    public sealed class DefaultFileAccessProbe : IFileAccessProbe
    {
        public FileLease OpenLease(string path)
        {
            // FileShare.Read: other processes may READ but not WRITE while
            // this handle is open. If another writer (e.g. AutoCAD with the
            // file open for edit) currently holds the file with a less
            // permissive share, our open throws IOException — exactly what
            // the runner converts into a hard abort per the spec.
            //
            // We close the handle immediately after a successful probe;
            // see <Why a probe-and-release> in the IFileAccessProbe doc.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // no-op: opening was the check.
            }
            return new FileLease(path);
        }
    }
}
