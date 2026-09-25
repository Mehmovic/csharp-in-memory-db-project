using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RhinoDB.Core;
using RhinoDB.Core.Tables;
using RhinoDB.Generators;
using RhinoDB.Lib.Execution;
using RhinoDB.Lib.Tables;

namespace RhinoDB.Generators.Test;

// Compiles a small source snippet through the real TableGenerator, emits it to
// an in-memory assembly, and loads it into a collectible AssemblyLoadContext -
// tests assert real behavior against the generated types (via `dynamic`, since
// they don't exist at this project's own compile time), not a snapshot/string
// compare of the generated source.
static internal class GeneratorTestHost {
    static private readonly ImmutableArray<MetadataReference> References = BuildReferences();
    static private readonly ImmutableArray<IIncrementalGenerator> SerializationGenerators = BuildSerializationGenerators();

    static public (Assembly Assembly, ImmutableArray<Diagnostic> GeneratorDiagnostics) CompileAndLoad(
        string source, [System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        CompileAndLoad(source, ImmutableArray<IIncrementalGenerator>.Empty, testName, ImmutableArray<AdditionalText>.Empty);

    // Descriptor.json is consumed via AdditionalTextsProvider (Phase 2, step 13) - a real MSBuild build
    // supplies it as an <AdditionalFiles> item, so tests need an in-memory AdditionalText stand-in rather
    // than a real file on disk, matching this harness's existing "no filesystem, no MSBuild host" approach.
    static public (Assembly Assembly, ImmutableArray<Diagnostic> GeneratorDiagnostics) CompileAndLoadWithDescriptor(
        string source, string descriptorJson, [System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        CompileAndLoad(source, ImmutableArray<IIncrementalGenerator>.Empty, testName,
            ImmutableArray.Create<AdditionalText>(new InMemoryAdditionalText("Descriptor.json", descriptorJson)));

    // Also runs MemoryPack.Generator/MessagePackAnalyzer's real generators alongside TableGenerator,
    // proving end-to-end that a correctly-attributed row (or CustomType field) gets a REAL generated
    // formatter - not just that TableGenerator's own textual-presence check (RHINO015/RHINO016) is
    // satisfied. Deliberately opt-in, not folded into the default CompileAndLoad: many existing
    // fixtures across this test project were only ever validated against TableGenerator's own
    // (deliberately looser) attribute-presence check, not the real generators' stricter rules (e.g.
    // MEMPACK041 - GenerateType.VersionTolerant rejected on a fully-unmanaged struct) - running these
    // unconditionally would risk breaking tests that were never meant to prove real-generator
    // correctness in the first place.
    static public (Assembly Assembly, ImmutableArray<Diagnostic> GeneratorDiagnostics) CompileAndLoadWithSerializationGenerators(
        string source, [System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        CompileAndLoad(source, SerializationGenerators, testName, ImmutableArray<AdditionalText>.Empty);

    static private (Assembly Assembly, ImmutableArray<Diagnostic> GeneratorDiagnostics) CompileAndLoad(
        string source, ImmutableArray<IIncrementalGenerator> extraGenerators, string testName, ImmutableArray<AdditionalText> additionalTexts) {
        var assemblyName = $"Gen_{testName}_{Guid.NewGuid():N}";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generators = ImmutableArray.Create<IIncrementalGenerator>(new TableGenerator(), new CustomTypeGenerator(), new FrozenSchemaGenerator()).AddRange(extraGenerators);
        var driver = CSharpGeneratorDriver.Create(generators.ToArray()).AddAdditionalTexts(additionalTexts);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        var generatorErrors = generatorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        if (!generatorErrors.IsEmpty)
            throw new InvalidOperationException("Generator reported errors:\n" + string.Join("\n", generatorErrors));

        using var peStream = new MemoryStream();
        var emitResult = outputCompilation.Emit(peStream);
        if (!emitResult.Success) {
            var errors = emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
            throw new InvalidOperationException("Emit failed:\n" + string.Join("\n", errors));
        }

        peStream.Position = 0;
        var context = new AssemblyLoadContext(assemblyName, isCollectible: true);
        return (context.LoadFromStream(peStream), generatorDiagnostics);
    }

    // MemoryPack.Generator/MessagePackAnalyzer ship as analyzer-only NuGet packages - their generator
    // DLLs live under the NuGet global-packages cache's analyzers/ folder, not among the runtime
    // dependencies TRUSTED_PLATFORM_ASSEMBLIES surfaces, so they need an explicit Assembly.LoadFrom.
    static private ImmutableArray<IIncrementalGenerator> BuildSerializationGenerators() {
        var memoryPackAsm = Assembly.LoadFrom(FindAnalyzerDll("memorypack.generator", "1.21.4", @"analyzers\dotnet\cs\MemoryPack.Generator.dll"));
        var messagePackAsm = Assembly.LoadFrom(FindAnalyzerDll("messagepackanalyzer", "3.1.9", @"analyzers\roslyn4.3\cs\MessagePack.SourceGenerator.dll"));

        var memoryPackGeneratorType = memoryPackAsm.GetType("MemoryPack.Generator.MemoryPackGenerator")
            ?? throw new InvalidOperationException("MemoryPack.Generator.MemoryPackGenerator not found - package layout may have changed.");
        var messagePackGeneratorType = messagePackAsm.GetType("MessagePack.SourceGenerator.MessagePackGenerator")
            ?? throw new InvalidOperationException("MessagePack.SourceGenerator.MessagePackGenerator not found - package layout may have changed.");

        return ImmutableArray.Create(
            (IIncrementalGenerator)Activator.CreateInstance(memoryPackGeneratorType)!,
            (IIncrementalGenerator)Activator.CreateInstance(messagePackGeneratorType)!
        );
    }

    static private string FindAnalyzerDll(string packageId, string version, string relativeDllPath) {
        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return Path.Combine(packagesRoot, packageId, version, relativeDllPath);
    }

    // DbContext<TTx>.Run(Func<DbContext<TTx>,TTx,Result>, PropagationMode) lives
    // on the closed generic DbContext<TTx> - TTx here only exists in the
    // dynamically-loaded assembly, so it can't be named at this project's own
    // compile time and `dynamic`/direct calls don't apply. This closes the gap
    // with reflection: resolve DbContext<> + txType to get the closed context
    // type, find its (now non-generic-at-the-method-level, TTx already fixed on
    // the class) Run overload, then build a matching
    // Func<DbContext<TTx>,TTx,Result> delegate via an Expression tree that just
    // forwards to an ordinary `Func<object,object,object> body` (itself free to
    // use `dynamic` internally, since it isn't a generic-method type argument).
    static public object RunTransactional(object db, Type txType, Func<object, object, object> body, object mode) {
        var dbContextType = typeof(DbContext<>).MakeGenericType(txType);

        // Run(Func<DbContext<TTx>,TTx,Result>, PropagationMode) - distinguished from:
        //  - Run<T>(Func<DbContext<TTx>,TTx,Result<T>>, PropagationMode): a
        //    generic method (1 type param), this one isn't.
        //  - Run<TArgs>(Func<DbContext<TTx>,TTx,TArgs,Result>, TArgs, PropagationMode):
        //    same 3-type-argument delegate shape, but 3 method parameters
        //    (func, args, mode), not 2 (func, mode).
        var runMethod = dbContextType.GetMethods()
            .Single(m => m.Name == "Run" && !m.IsGenericMethodDefinition
                         && m.GetParameters().Length == 2
                         && m.GetParameters()[0].ParameterType.GetGenericArguments().Length == 3);

        var ctxParam = Expression.Parameter(dbContextType, "ctx");
        var txParam = Expression.Parameter(txType, "tx");
        var invokeBody = Expression.Invoke(
            Expression.Constant(body),
            Expression.Convert(ctxParam, typeof(object)),
            Expression.Convert(txParam, typeof(object)));
        var delegateType = typeof(Func<,,>).MakeGenericType(dbContextType, txType, typeof(Result));
        var operation = Expression.Lambda(delegateType, Expression.Convert(invokeBody, typeof(Result)), ctxParam, txParam).Compile();

        // Run now returns ValueTask<Result>, not Task<Result> - every call site here casts the
        // result to (Task<Result>), so convert via AsTask() once, in this one reflection-based
        // helper, rather than touching every one of RunTransactional's ~14 consuming test files.
        var raw = runMethod.Invoke(db, [operation, mode])!;
        var asTask = raw.GetType().GetMethod(nameof(ValueTask<Result>.AsTask))!;
        return asTask.Invoke(raw, null)!;
    }

    // QuerySet<TRow>/QuerySingle<TRow> are ref structs (inherited from
    // OffsetList), so their instances can never cross a `dynamic` call boundary - the DLR needs to
    // box the result to `object` to complete the call site, and a ref struct cannot be boxed at all
    // (confirmed empirically: InvalidProgramException at DynamicMethod.CreateDelegate). Same
    // restriction applies to plain reflection: MethodInfo.Invoke refuses any method whose parameter
    // or return type is ByRefLike. So any code touching Find/Iter/Except/Range's result must be real,
    // statically-compiled C# living inside the dynamically-compiled Source string itself (its
    // signature must avoid ref structs, even though its body is free to use them) - this helper
    // reflectively invokes such a method by name, staying entirely within reflection-safe types.
    static public object? InvokeHelper(Assembly asm, string typeName, string methodName, params object?[] args) {
        var type = asm.GetType(typeName) ?? throw new InvalidOperationException($"Type '{typeName}' not found.");
        var method = type.GetMethod(methodName) ?? throw new InvalidOperationException($"Method '{methodName}' not found on '{typeName}'.");
        return method.Invoke(null, args);
    }

    // Same as InvokeHelper, but for the private static members (e.g. the raw serializer methods
    // TableGenerator emits on each Ops class) that a plain public-only GetMethod lookup won't find.
    static public object? InvokePrivateStaticHelper(Assembly asm, string typeName, string methodName, params object?[] args) {
        var type = asm.GetType(typeName) ?? throw new InvalidOperationException($"Type '{typeName}' not found.");
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found on '{typeName}'.");
        return method.Invoke(null, args);
    }

    private sealed class InMemoryAdditionalText(string path, string text) : AdditionalText {
        public override string Path { get; } = path;
        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) =>
            Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }

    static private ImmutableArray<MetadataReference> BuildReferences() {
        var trustedPlatformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trustedPlatformAssemblies.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(AutoIncrementCounter).Assembly.Location));
        return references.ToImmutableArray();
    }
}
