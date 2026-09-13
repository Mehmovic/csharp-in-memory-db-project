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

namespace RhinoDB.Test.Generators;

// Compiles a small source snippet through the real TableGenerator, emits it to
// an in-memory assembly, and loads it into a collectible AssemblyLoadContext -
// tests assert real behavior against the generated types (via `dynamic`, since
// they don't exist at this project's own compile time), not a snapshot/string
// compare of the generated source.
static internal class GeneratorTestHost {
    static private readonly ImmutableArray<MetadataReference> References = BuildReferences();

    static public (Assembly Assembly, ImmutableArray<Diagnostic> GeneratorDiagnostics) CompileAndLoad(
        string source, [System.Runtime.CompilerServices.CallerMemberName] string testName = "") {
        var assemblyName = $"Gen_{testName}_{Guid.NewGuid():N}";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new TableGenerator());
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

    static private ImmutableArray<MetadataReference> BuildReferences() {
        var trustedPlatformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trustedPlatformAssemblies.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(AutoIncrementCounter).Assembly.Location));
        return references.ToImmutableArray();
    }
}
