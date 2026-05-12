using System;
using Acd.Mcp.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Xunit;

namespace Acd.Mcp.Tests
{
    // Exercises the same Roslyn-scripting wiring DtoLoader uses, against a
    // fake "Acd" facade that mirrors DtoRegistrationApi's RegisterDto surface.
    // We can't use DtoRegistrationApi here because that type lives in
    // Acd.Mcp.Api alongside AutoCAD types (Entity / Transaction) — the
    // pure-net8 test project has no AutoCAD references.
    //
    // What this catches: a regression where the registration mechanism stops
    // functioning end-to-end through CSharpScript — e.g. a future refactor
    // of DtoRegistry that breaks Roslyn binding, or a missing
    // <Compile Include> entry. The full ALC-split scenario from F7 cannot
    // be reproduced without a real isolated AssemblyLoadContext, but the
    // happy-path script→register→retrieve chain is covered here.
    public class DtoLoaderScriptTests
    {
        public sealed class FakeRegistrationGlobals
        {
            public FakeRegistration Acd { get; }
            public FakeRegistrationGlobals(DtoRegistry registry, string source)
            {
                Acd = new FakeRegistration(registry, source);
            }
        }

        public sealed class FakeRegistration
        {
            private readonly DtoRegistry _registry;
            private readonly string _source;
            public FakeRegistration(DtoRegistry registry, string source)
            {
                _registry = registry;
                _source = source;
            }
            public void RegisterDto<T>(Func<T, object?> projection)
                => _registry.Register(projection, _source);
        }

        public sealed class Widget
        {
            public int X { get; init; }
            public string Name { get; init; } = "";
        }

        [Fact]
        public async System.Threading.Tasks.Task Script_calling_RegisterDto_populates_registry()
        {
            var registry = new DtoRegistry();
            var globals = new FakeRegistrationGlobals(registry, source: "test:Widget.csx");

            var options = ScriptOptions.Default
                .WithReferences(typeof(Widget).Assembly, typeof(DtoRegistry).Assembly)
                .WithImports("System", "Acd.Mcp.Tests");

            const string body = @"Acd.RegisterDto<DtoLoaderScriptTests.Widget>(w => new { x = w.X, n = w.Name });";

            await CSharpScript.RunAsync(body, options, globals, typeof(FakeRegistrationGlobals));

            Assert.Contains(typeof(Widget), registry.RegisteredTypes);
        }

        [Fact]
        public async System.Threading.Tasks.Task Script_compile_error_does_not_register_type()
        {
            var registry = new DtoRegistry();
            var globals = new FakeRegistrationGlobals(registry, "test:Bad.csx");

            var options = ScriptOptions.Default
                .WithReferences(typeof(Widget).Assembly, typeof(DtoRegistry).Assembly)
                .WithImports("System", "Acd.Mcp.Tests");

            // Reference a property that doesn't exist on Widget — the script
            // must fail to compile and the registry must stay empty.
            const string body = @"Acd.RegisterDto<DtoLoaderScriptTests.Widget>(w => new { y = w.NoSuchMember });";

            await Assert.ThrowsAsync<CompilationErrorException>(async () =>
                await CSharpScript.RunAsync(body, options, globals, typeof(FakeRegistrationGlobals)));

            Assert.Empty(registry.RegisteredTypes);
        }

        [Fact]
        public async System.Threading.Tasks.Task Two_scripts_overwrite_in_registration_order()
        {
            var registry = new DtoRegistry();
            var systemGlobals = new FakeRegistrationGlobals(registry, "system:Widget.csx");
            var userGlobals = new FakeRegistrationGlobals(registry, "user:Widget.csx");

            var options = ScriptOptions.Default
                .WithReferences(typeof(Widget).Assembly, typeof(DtoRegistry).Assembly)
                .WithImports("System", "Acd.Mcp.Tests");

            await CSharpScript.RunAsync(
                @"Acd.RegisterDto<DtoLoaderScriptTests.Widget>(w => ""system"");",
                options, systemGlobals, typeof(FakeRegistrationGlobals));

            await CSharpScript.RunAsync(
                @"Acd.RegisterDto<DtoLoaderScriptTests.Widget>(w => ""user"");",
                options, userGlobals, typeof(FakeRegistrationGlobals));

            Assert.Single(registry.RegisteredTypes);
            Assert.Contains(typeof(Widget), registry.RegisteredTypes);
        }
    }
}
