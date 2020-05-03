using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace CarlJ.DynamicLambda
{
    public class SearchFilterCompiler
    {
        private readonly PortableExecutableReference[] References;
        private readonly string StandardHeader;

        public SearchFilterCompiler(IEnumerable<Type> referencedTypes, IEnumerable<string> usings)
        {
            References = GetReferences(referencedTypes);
            StandardHeader = GetUsingStatements(usings);
        }

        private class CollectibleAssemblyLoadContext : AssemblyLoadContext, IDisposable
        {
            public CollectibleAssemblyLoadContext() : base(true)
            { }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                return null;
            }

            public void Dispose()
            {
                Unload();
            }
        }

        private static readonly Assembly SystemRuntime = Assembly.Load(new AssemblyName("System.Runtime"));
        private static readonly Assembly NetStandard = Assembly.Load(new AssemblyName("netstandard"));

        public T CSharpScriptEvaluate<T>(string lambda)
        {
            var returnTypeAsString = GetCSharpRepresentation(typeof(T), true);
            var outerClass = StandardHeader + $"public static class Wrapper {{ public static {returnTypeAsString} expr = {lambda}; }}";

            var compilation = CSharpCompilation.Create("FilterCompiler_" + Guid.NewGuid(),
                                                        new[] { CSharpSyntaxTree.ParseText(outerClass) },
                                                        References,
                                                        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var assemblyLoadContext = new CollectibleAssemblyLoadContext();
            using var ms = new MemoryStream();

            var cr = compilation.Emit(ms);
            if (!cr.Success)
            {
                throw new InvalidOperationException("Error in expression: " + cr.Diagnostics.First(e =>
                    e.Severity == DiagnosticSeverity.Error).GetMessage());
            }

            ms.Seek(0, SeekOrigin.Begin);
            var assembly = assemblyLoadContext.LoadFromStream(ms);

            var outerClassType = assembly.GetType("Wrapper");

            var exprField = outerClassType.GetField("expr", BindingFlags.Public | BindingFlags.Static);
            // ReSharper disable once PossibleNullReferenceException
            return (T)exprField.GetValue(null);
        }

        private static string GetUsingStatements(IEnumerable<string> nonStandardUsings)
        {
            var requiredImports = new[]{ "System",
            "System.Linq",
            "System.Linq.Expressions",
            "Microsoft.EntityFrameworkCore"}.Concat(nonStandardUsings);

            var result = new StringBuilder();

            foreach (var import in requiredImports)
            {
                result.AppendLine($"using {import};");
            }

            return result.ToString();
        }

        private static PortableExecutableReference[] GetReferences(IEnumerable<Type> referencedTypes)
        {
            var standardReferenceHints = new[] { typeof(string), typeof(IQueryable), typeof(IReadOnlyCollection<>), typeof(EF), typeof(Enumerable) };
            var allHints = standardReferenceHints.Concat(referencedTypes);
            var includedAssemblies = new[] { SystemRuntime, NetStandard }.Concat(allHints.Select(t => t.Assembly)).Distinct();

            return includedAssemblies.Select(a => MetadataReference.CreateFromFile(a.Location)).ToArray();
        }

        private static string GetCSharpRepresentation(Type t, bool trimArgCount)
        {
            if (!t.IsGenericType) return t.Name;
            var genericArgs = t.GetGenericArguments().ToList();

            return GetCSharpRepresentation(t, trimArgCount, genericArgs);
        }

        private static string GetCSharpRepresentation(Type t, bool trimArgCount, List<Type> availableArguments)
        {
            if (!t.IsGenericType) return t.Name;
            var value = t.Name;
            if (trimArgCount && value.IndexOf("`", StringComparison.Ordinal) > -1)
            {
                value = value.Substring(0, value.IndexOf("`", StringComparison.Ordinal));
            }

            if (t.DeclaringType != null)
            {
                // This is a nested type, build the nesting type first
                value = GetCSharpRepresentation(t.DeclaringType, trimArgCount, availableArguments) + "+" + value;
            }

            // Build the type arguments (if any)
            var argString = "";
            var thisTypeArgs = t.GetGenericArguments();
            for (var i = 0; i < thisTypeArgs.Length && availableArguments.Count > 0; i++)
            {
                if (i != 0) argString += ", ";

                argString += GetCSharpRepresentation(availableArguments[0], trimArgCount);
                availableArguments.RemoveAt(0);
            }

            // If there are type arguments, add them with < >
            if (argString.Length > 0)
            {
                value += "<" + argString + ">";
            }

            return value;
        }
    }
}