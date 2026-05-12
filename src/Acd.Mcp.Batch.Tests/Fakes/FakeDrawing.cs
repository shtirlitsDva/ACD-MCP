using System;
using System.Collections.Generic;
using System.IO;
using Acd.Mcp.Batch;

namespace Acd.Mcp.Batch.Tests.Fakes
{
    // The minimum AutoCAD-shaped surface a script body needs to exercise.
    // The whole point of the test project: the runtime exposes the runner
    // generic over TGlobals, so we can plug any TGlobals + any host without
    // a single Autodesk.* reference.
    public sealed class FakeDatabase
    {
        public Dictionary<string, int> EntitiesByLayer { get; } = new();
        public bool Committed { get; private set; }
        public bool Saved { get; private set; }

        public void Commit() => Committed = true;
        public void Save() => Saved = true;
    }

    public sealed class FakeTransaction
    {
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    // Globals the test script sees. Same shape as the real Acad globals
    // (xDb / xTx / ctx) but typed against fake state.
    public sealed class FakeGlobals
    {
        public FakeDatabase xDb { get; set; } = default!;
        public FakeTransaction xTx { get; set; } = default!;
        public IBatchContext ctx { get; set; } = default!;
    }

    // The fake drawing host. Keeps a per-path dictionary of the
    // "current drawing state" so tests can pre-seed entities.
    public sealed class FakeDrawingHost : IDrawingHost<FakeGlobals>
    {
        public Dictionary<string, FakeDatabase> Drawings { get; } = new();
        public List<string> OpenedPaths { get; } = new();
        public List<FakeSession> OpenedSessions { get; } = new();

        public IBatchSession Open(string path, FileLease lease)
        {
            if (!Drawings.TryGetValue(path, out var db))
            {
                db = new FakeDatabase();
                Drawings[path] = db;
            }
            OpenedPaths.Add(path);
            var session = new FakeSession(path, db);
            OpenedSessions.Add(session);
            return session;
        }

        public FakeGlobals BuildGlobals(IBatchSession session, IBatchContext ctx)
        {
            var s = (FakeSession)session;
            return new FakeGlobals
            {
                xDb = s.Db,
                xTx = s.Tx,
                ctx = ctx,
            };
        }
    }

    public sealed class FakeSession : IBatchSession
    {
        public FakeDatabase Db { get; }
        public FakeTransaction Tx { get; } = new();
        public string Path { get; }
        public bool DisposedFlag { get; private set; }
        public bool CommittedAndSaved { get; private set; }

        public FakeSession(string path, FakeDatabase db)
        {
            Path = path;
            Db = db;
        }

        public void CommitAndSave()
        {
            Db.Commit();
            Db.Save();
            CommittedAndSaved = true;
        }

        public void Dispose()
        {
            if (DisposedFlag) return;
            DisposedFlag = true;
            if (!CommittedAndSaved) Tx.Abort();
        }
    }

    // Fake probe that mostly just creates an in-memory MemoryStream-backed
    // lease. Tests can substitute paths that throw on OpenLease to simulate
    // a locked file.
    public sealed class FakeFileAccessProbe : IFileAccessProbe
    {
        public HashSet<string> LockedPaths { get; } = new();
        public List<string> LeasedPaths { get; } = new();

        public FileLease OpenLease(string path)
        {
            if (LockedPaths.Contains(path))
                throw new IOException($"Simulated lock on '{path}'.");
            LeasedPaths.Add(path);

            // The FileLease wraps a FileStream. To keep tests fast we point
            // the stream at a temp file we create on demand. The real
            // FileLease just needs an IDisposable handle.
            var temp = System.IO.Path.GetTempFileName();
            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new FileLease(path, stream);
        }
    }
}
