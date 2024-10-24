﻿using Dalamud.Utility;
using Mono.Cecil;

namespace FetchDependencies;

internal class TargetAssembly : IDisposable
{
    public TargetAssembly(string assemblyPath)
    {
        AssemblyPath = assemblyPath;

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath));
        Assembly = AssemblyDefinition.ReadAssembly(AssemblyPath,
                                                   new ReaderParameters { AssemblyResolver = resolver });
    }

    public AssemblyDefinition Assembly { get; }
    private string AssemblyPath { get; }

    public Version Version => Assembly.MainModule.Assembly.Name.Version;

    public void Dispose()
    {
        Assembly.Dispose();
    }

    public MethodDefinition GetMethod(string name)
    {
        return GetAllTypes()
               .Where(o => o.IsClass)
               .SelectMany(type => type.Methods)
               .First(o => o.FullName.Contains(name));
    }

    public void MakePublic()
    {
        static bool CheckCompilerGeneratedAttribute(ICustomAttributeProvider member)
        {
            return member.CustomAttributes.Any(x =>
                                                   x.AttributeType.FullName ==
                                                   "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        foreach (var type in GetAllTypes())
        {
            if (CheckCompilerGeneratedAttribute(type))
                continue;

            if (type.IsNested)
                type.IsNestedPublic = true;
            else
                type.IsPublic = true;

            foreach (var method in type.Methods.Where(method =>
                                                          !CheckCompilerGeneratedAttribute(method) &&
                                                          !method.IsCompilerControlled))
                method.IsPublic = true;

            foreach (var field in type.Fields.Where(field =>
                                                        !CheckCompilerGeneratedAttribute(field) &&
                                                        !field.IsCompilerControlled))
                field.IsPublic = true;
        }
    }

    public void RemoveStrongNaming()
    {
        var name = Assembly.Name;
        name.HasPublicKey = false;
        name.PublicKey = Array.Empty<byte>();

        foreach (var module in Assembly.Modules)
        {
            module.Attributes &= ~ModuleAttributes.StrongNameSigned;
            var coreLibs = new[] { "netstandard", "mscorlib", "System" };
            foreach (var reference in module.AssemblyReferences)
            {
                if (coreLibs.Any(coreLib => reference.Name == coreLib))
                    continue;
                reference.HasPublicKey = false;
                reference.PublicKey = Array.Empty<byte>();
            }
        }
    }

    private IEnumerable<TypeDefinition> GetAllTypes()
    {
        var types = new Queue<TypeDefinition>(Assembly.MainModule.Types);

        while (types.Count > 0)
        {
            var type = types.Dequeue();
            yield return type;
            foreach (var nestedType in type.NestedTypes)
                types.Enqueue(nestedType);
        }
    }
    
    public bool ApiVersionMatches()
    {
        foreach (var type in Assembly.MainModule.Types)
            if (type.Namespace == ApiVersion.NamespaceIdentifier && type.Name == "WasHere")
                return true;
        
        return false;
    }

    public string GetDieMoeBuildVersion()
    {
        foreach (var type in Assembly.MainModule.Types)
        {
            foreach (var field in type.Fields)
            {
                if (field.Name == "DieMoeBuildVersion")
                    return field.Constant.ToString() ?? "";
            }
        }
        return "";
    }

    public void WriteOut()
    {
        if (!ApiVersionMatches())
        {
            // Log.WriteLine($"[PatchWasHere] Adding type {ApiVersion.NamespaceIdentifier}.WasHere");
            var wasHere = new TypeDefinition(ApiVersion.NamespaceIdentifier, "WasHere", TypeAttributes.Public | TypeAttributes.Class) {
                BaseType = Assembly.MainModule.TypeSystem.Object
            };
            Assembly.MainModule.Types.Add(wasHere);
        }

        if (!FetchDependencies.RemoteDieMoeBuildVersion.IsNullOrEmpty())
        {
            // Define a string constant with Mono.Cecil
            var field = new FieldDefinition("DieMoeBuildVersion", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly, Assembly.MainModule.TypeSystem.String);
            field.Constant = FetchDependencies.RemoteDieMoeBuildVersion;
            Assembly.MainModule.Types.First().Fields.Add(field);
        }

        var patchedPath = AssemblyPath + ".patched";
        Assembly.Write(patchedPath);
        Assembly.Dispose();
        File.Move(patchedPath, AssemblyPath, true);
    }
}
