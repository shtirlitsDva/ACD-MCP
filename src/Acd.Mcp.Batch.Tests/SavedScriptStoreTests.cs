using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Acd.Mcp.Batch.Tests
{
    public class SavedScriptStoreTests : IDisposable
    {
        private readonly string _root;

        public SavedScriptStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "acd-mcp-scripts-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void Save_Then_TryGet_RoundTripsBody_AndHeader()
        {
            var store = new SavedScriptStore(_root);
            var body = @"var TARGET_LAYER = ""X-FOO"";
ctx.Step(""bump"").Apply(() => ""done"");";
            store.Save(ScriptFlavor.Batch, "bump-layer", body, summary: "bumps a layer");

            var got = store.TryGet(ScriptFlavor.Batch, "bump-layer");

            Assert.NotNull(got);
            Assert.Equal(ScriptFlavor.Batch, got!.Flavor);
            Assert.Equal("bump-layer", got.Name);
            Assert.Equal("bumps a layer", got.Summary);
            Assert.Contains("var TARGET_LAYER", got.Body);
        }

        [Fact]
        public void List_Paginates_AndSortsAlphabetically()
        {
            var store = new SavedScriptStore(_root);
            foreach (var name in new[] { "zebra", "alpha", "mango", "beta" })
                store.Save(ScriptFlavor.Batch, name, "ctx.Step(\"a\").Apply(() => \"\");");

            var page1 = store.List(ScriptFlavor.Batch, limit: 2, offset: 0);
            Assert.Equal(new[] { "alpha", "beta" }, page1.Select(s => s.Name).ToArray());

            var page2 = store.List(ScriptFlavor.Batch, limit: 2, offset: 2);
            Assert.Equal(new[] { "mango", "zebra" }, page2.Select(s => s.Name).ToArray());
        }

        [Fact]
        public void Delete_RemovesFile()
        {
            var store = new SavedScriptStore(_root);
            store.Save(ScriptFlavor.Batch, "doomed", "ctx.Step(\"a\").Apply(() => \"\");");
            Assert.True(store.Delete(ScriptFlavor.Batch, "doomed"));
            Assert.Null(store.TryGet(ScriptFlavor.Batch, "doomed"));
        }

        [Fact]
        public void Rename_MovesFile()
        {
            var store = new SavedScriptStore(_root);
            store.Save(ScriptFlavor.Batch, "old-name", "ctx.Step(\"a\").Apply(() => \"\");");
            Assert.True(store.Rename(ScriptFlavor.Batch, "old-name", "new-name"));
            Assert.Null(store.TryGet(ScriptFlavor.Batch, "old-name"));
            Assert.NotNull(store.TryGet(ScriptFlavor.Batch, "new-name"));
        }

        [Fact]
        public void Rename_Throws_OnCollision()
        {
            var store = new SavedScriptStore(_root);
            store.Save(ScriptFlavor.Batch, "a", "//");
            store.Save(ScriptFlavor.Batch, "b", "//");
            Assert.Throws<IOException>(() => store.Rename(ScriptFlavor.Batch, "a", "b"));
        }

        [Fact]
        public void BatchAndScript_AreSeparateFolders()
        {
            var store = new SavedScriptStore(_root);
            store.Save(ScriptFlavor.Batch, "same-name", "//batch body");
            store.Save(ScriptFlavor.Script, "same-name", "//script body");

            var b = store.TryGet(ScriptFlavor.Batch, "same-name");
            var s = store.TryGet(ScriptFlavor.Script, "same-name");

            Assert.NotNull(b);
            Assert.NotNull(s);
            Assert.Contains("//batch body", b!.Body);
            Assert.Contains("//script body", s!.Body);
        }

        [Fact]
        public void Read_LegacyReplFlavorHeader_MapsToScript()
        {
            var store = new SavedScriptStore(_root);
            // Hand-write a file with the legacy header to simulate
            // a pre-v0.3.0 saved script. After read it should report
            // ScriptFlavor.Script (the legacy alias).
            var folder = store.FolderFor(ScriptFlavor.Script);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "legacy.csx");
            File.WriteAllText(path,
                "// @flavor: repl\n// @name: legacy\n// @summary: pre-rename\n//body\n");

            var got = SavedScriptStore.Read(path, ScriptFlavor.Script);
            Assert.Equal(ScriptFlavor.Script, got.Flavor);
            Assert.Equal("legacy", got.Name);
            Assert.Equal("pre-rename", got.Summary);
        }

        [Fact]
        public void Save_SanitisesUnsafeNames()
        {
            var store = new SavedScriptStore(_root);
            var saved = store.Save(ScriptFlavor.Batch, "weird/name with*chars", "// body");
            Assert.DoesNotContain("/", saved.Name);
            Assert.DoesNotContain("*", saved.Name);
        }
    }
}
