using System;
using System.IO;

namespace Acd.Mcp.Batch
{
    // Restrictive file-access lease. Both Test and Live mode use FileShare.Read
    // — no other process may have write access while we're processing the
    // drawing.
    //
    // Contract: OpenLease MUST throw if the file is locked by another writer.
    // The runner does NOT silently skip a locked file — a half-finished batch
    // is worse than a slower-but-safer one. The user must intervene before the
    // batch continues.
    //
    // The lease is held for the entire duration of one file's processing
    // (open + transact + save + dispose) so no other process can grab it
    // mid-flight. Disposing the lease releases the OS handle.
    public interface IFileAccessProbe
    {
        FileLease OpenLease(string path);
    }

    // Owns a FileStream opened with FileShare.Read so that other readers can
    // coexist but no other writer can sneak in. Dispose to release.
    public sealed class FileLease : IDisposable
    {
        private readonly FileStream _stream;
        public string Path { get; }

        public FileLease(string path, FileStream stream)
        {
            Path = path;
            _stream = stream;
        }

        public void Dispose() => _stream.Dispose();
    }

    // Default implementation. Lives in this assembly because it's
    // AutoCAD-free — anyone testing the runner can hand a real probe.
    // Tests that need to simulate a locked file inject a fake probe that
    // throws on OpenLease.
    public sealed class DefaultFileAccessProbe : IFileAccessProbe
    {
        public FileLease OpenLease(string path)
        {
            // FileShare.Read: other processes may READ but not WRITE; another
            // writer (e.g. AutoCAD with the file open for edit) makes this
            // call throw IOException, which the runner converts into a hard
            // abort per the spec.
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new FileLease(path, stream);
        }
    }
}
