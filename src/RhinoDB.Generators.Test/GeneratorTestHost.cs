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

    // DbContext.Run<TTx>(Func<DbContext,TTx,Result>, PropagationMode) is generic
    // over TTx, and TTx here only exists in the dynamically-loaded assembly - it
    // can't be named at this project's own compile time, so `dynamic`/direct
    // generic calls don't apply. This closes the gap with reflection: resolve
    // Run<TTx> via MakeGenericMethod(txType), then build a matching
    // Func<DbContext,TTx,Result> delegate via an Expression tree that just
    // forwards to an ordinary `Func<object,object,object> body` (itself free to
    // use `dynamic` internally, since it isn't a generic-method type argument).
    static public object RunTransactional(object db, Type txType, Func<object, object, object> body, object mode) {
        // Run<TTx>(Func<DbContext,TTx,Result>, PropagationMode) - distinguished from:
        //  - Run<T>(Func<DbContext,Result<T>>, PropagationMode): also 1 generic
        //    method parameter, but a 2-type-argument (not 3) delegate parameter.
        //  - Run<TArgs>(Func<DbContext,TArgs,Result>, TArgs, PropagationMode): same
        //    1 generic parameter AND the same 3-type-argument delegate shape, but 3
        //    method parameters (func, args, mode), not 2 (func, mode).
        var runMethod = typeof(DbContext).GetMethods()
            .Single(m => m.Name == "Run" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1
                         && m.GetParameters().Length == 2
                         && m.GetParameters()[0].ParameterType.GetGenericArguments().Length == 3);
        var genericRun = runMethod.MakeGenericMethod(txType);

        var ctxParam = Expression.Parameter(typeof(DbContext), "ctx");
        var txParam = Expression.Parameter(txType, "tx");
        var invokeBody = Expression.Invoke(
            Expression.Constant(body),
            Expression.Convert(ctxParam, typeof(object)),
            Expression.Convert(txParam, typeof(object)));
        var delegateType = typeof(Func<,,>).MakeGenericType(typeof(DbContext), txType, typeof(Result));
        var operation = Expression.Lambda(delegateType, Expression.Convert(invokeBody, typeof(Result)), ctxParam, txParam).Compile();

        return genericRun.Invoke(db, [operation, mode])!;
    }

    static private ImmutableArray<MetadataReference> BuildReferences() {
        var trustedPlatformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trustedPlatformAssemblies.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(GenerateTableAttribute).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Table<,>).Assembly.Location));

        return references.ToImmutableArray();
    }
}
